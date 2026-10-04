using System.Diagnostics;
using System.Threading.Channels;

namespace SemiInspectX.Core;

public sealed class EquipmentController : IAsyncDisposable
{
    private readonly IStage _stage;
    private readonly ICamera _camera;
    private readonly IResultStore _store;
    private readonly Func<Recipe, IInspectionEngine> _engineFactory;
    private readonly SemaphoreSlim _commands = new(1);
    private readonly object _sync = new();
    private readonly List<EquipmentEvent> _events = [];
    private Recipe? _recipe;
    private IReadOnlyList<Die> _dies = [];
    private CancellationTokenSource? _acquisition;
    private Task _run = Task.CompletedTask;
    private TaskCompletionSource _resume = NewSignal(), _paused = NewSignal();
    private int _inflight, _saved, _faulted;
    private bool _stopRequested, _disposed;
    private EquipmentState _state = EquipmentState.Initializing;
    public EquipmentState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }
    public Task Completion => _run;
    public string? RunId
    {
        get; private set;
    }
    public RunStatus? LastStatus
    {
        get; private set;
    }
    public int SavedDies => Volatile.Read(ref _saved);
    public event Action<EquipmentState>? StateChanged;
    public event Action<EquipmentEvent>? EventRaised;
    public event Action<Die>? DieStarted;
    public event Action<DieResult>? ResultSaved;
    public EquipmentController(IStage stage, ICamera camera, IResultStore store, Func<Recipe, IInspectionEngine> engineFactory)
    {
        _stage = stage;
        _camera = camera;
        _store = store;
        _engineFactory = engineFactory;
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SetState(EquipmentState state)
    {
        lock (_sync)
            _state = state;
        Emit("STATE", state.ToString());
        StateChanged?.Invoke(state);
    }
    private void Emit(string code, string message)
    {
        var e = new EquipmentEvent(DateTimeOffset.UtcNow, code, message);
        lock (_sync)
            _events.Add(e);
        EventRaised?.Invoke(e);
    }
    private void Fault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            Emit("ALARM", exception.Message);
            _acquisition?.Cancel();
            _resume.TrySetResult();
        }
    }
    private async Task Command(Func<Task> command)
    {
        await _commands.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await command();
        }
        finally { _commands.Release(); }
    }
    public Task InitializeAsync() => Command(async () =>
    {
        if (State != EquipmentState.Initializing)
            throw new InvalidOperationException("Initialize only once; use Reset for recovery.");
        try
        {
            await _stage.InitializeAsync(default);
            await _camera.InitializeAsync(default);
            SetState(EquipmentState.Idle);
        }
        catch (Exception e) { Emit("ALARM", e.Message); SetState(EquipmentState.Error); throw; }
    });
    public Task LoadAsync(Recipe recipe, IReadOnlyList<Die> dies) => Command(() =>
    {
        if (State is not (EquipmentState.Idle or EquipmentState.Ready))
            throw new InvalidOperationException("Load requires Idle or Ready.");
        recipe.Validate();
        if (dies.Count == 0 || dies.Select(d => (d.X, d.Y)).Distinct().Count() != dies.Count || dies.Select(d => d.Index).Distinct().Count() != dies.Count)
            throw new ArgumentException("Wafer must contain unique dies.");
        // Validate native recipe/reference before entering Ready.
        using var engine = _engineFactory(recipe);
        _recipe = recipe;
        _dies = dies.ToArray();
        SetState(EquipmentState.Ready);
        return Task.CompletedTask;
    });
    public Task StartAsync(string lot = "LOT-DEMO", string wafer = "WAFER-01", bool serial = false) => Command(async () =>
    {
        if (State != EquipmentState.Ready || !_run.IsCompleted)
            throw new InvalidOperationException("Start requires Ready and no active run.");
        if (string.IsNullOrWhiteSpace(lot) || string.IsNullOrWhiteSpace(wafer))
            throw new ArgumentException("Lot and wafer identifiers are required.");
        _acquisition?.Dispose();
        _acquisition = new();
        _resume = NewSignal();
        _paused = NewSignal();
        _stopRequested = false;
        _faulted = 0;
        _inflight = 0;
        _saved = 0;
        LastStatus = null;
        lock (_sync)
            _events.Clear();
        RunId = Guid.NewGuid().ToString("N");
        try
        {
            await _store.BeginAsync(new(RunId, lot, wafer, DateTimeOffset.UtcNow, _recipe!, _dies.Count));
        }
        catch (Exception e) { Emit("ALARM", e.Message); SetState(EquipmentState.Error); throw; }
        SetState(EquipmentState.Running);
        _run = Task.Run(() => RunAsync(serial));
    });
    public Task PauseAsync() => Command(async () =>
    {
        if (State != EquipmentState.Running)
            throw new InvalidOperationException("Pause requires Running.");
        SetState(EquipmentState.Pausing);
        await Task.WhenAny(_paused.Task, _run);
    });
    public Task ResumeAsync() => Command(() =>
    {
        if (State != EquipmentState.Paused)
            throw new InvalidOperationException("Resume requires Paused.");
        SetState(EquipmentState.Running);
        _resume.TrySetResult();
        return Task.CompletedTask;
    });
    public Task StopAsync() => Command(async () =>
    {
        if (State is not (EquipmentState.Running or EquipmentState.Pausing or EquipmentState.Paused))
            throw new InvalidOperationException("Stop requires an active run.");
        _stopRequested = true;
        SetState(EquipmentState.Stopping);
        _acquisition!.Cancel();
        _resume.TrySetResult();
        await _stage.StopAsync(default);
        await _run;
    });
    public Task ResetAsync() => Command(async () =>
    {
        if (State != EquipmentState.Error || !_run.IsCompleted)
            throw new InvalidOperationException("Reset requires Error and exited tasks.");
        SetState(EquipmentState.Initializing);
        try
        {
            await _camera.CloseAsync(default);
            await _stage.InitializeAsync(default);
            await _camera.InitializeAsync(default);
            SetState(_recipe is null ? EquipmentState.Idle : EquipmentState.Ready);
        }
        catch (Exception e) { Emit("ALARM", e.Message); SetState(EquipmentState.Error); throw; }
    });
    private async Task PauseBoundary()
    {
        if (State != EquipmentState.Pausing)
            return;
        while (Volatile.Read(ref _inflight) > 0)
            await Task.Delay(1);
        if (_stopRequested || Volatile.Read(ref _faulted) != 0)
            return;
        _resume = NewSignal();
        SetState(EquipmentState.Paused);
        _paused.TrySetResult();
        await _resume.Task;
        _paused = NewSignal();
    }
    private sealed record Captured(Die Die, ImageFrame Frame, DateTimeOffset At, long Ticks);
    private sealed record Pending(Captured Captured, Inspection Inspection);
    private async Task<Captured> Capture(Die die)
    {
        await _stage.MoveToAsync(die, _acquisition!.Token);
        _acquisition.Token.ThrowIfCancellationRequested();
        DieStarted?.Invoke(die);
        long ticks = Stopwatch.GetTimestamp();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        return new(die, await _camera.CaptureAsync(die, _acquisition.Token), at, ticks);
    }
    private Inspection Inspect(IInspectionEngine engine, Captured captured)
    {
        try
        {
            return engine.Inspect(captured.Frame);
        }
        catch (Exception e) { Fault(e); return new(Verdict.Error, 0, 0, 0, 0, 0, 0, [], e.Message); }
    }
    private async Task Persist(Pending pending)
    {
        try
        {
            var c = pending.Captured;
            var result = await _store.SaveAsync(new(RunId!, c.Die, pending.Inspection, c.At, 0, ""), c.Frame, c.Ticks);
            Interlocked.Increment(ref _saved);
            ResultSaved?.Invoke(result);
        }
        catch (Exception e) { Fault(e); }
        finally { pending.Captured.Frame.Dispose(); Interlocked.Decrement(ref _inflight); }
    }
    private async Task RunAsync(bool serial)
    {
        try
        {
            if (serial)
            {
                using var engine = _engineFactory(_recipe!);
                foreach (var die in _dies)
                {
                    await PauseBoundary();
                    _acquisition!.Token.ThrowIfCancellationRequested();
                    var captured = await Capture(die);
                    Interlocked.Increment(ref _inflight);
                    await Persist(new(captured, Inspect(engine, captured)));
                }
                await PauseBoundary();
            }
            else
                await RunPipeline();
        }
        catch (OperationCanceledException) when (_acquisition!.IsCancellationRequested) { }
        catch (Exception e) { Fault(e); }
        LastStatus = Volatile.Read(ref _faulted) != 0 ? RunStatus.Failed : _stopRequested ? RunStatus.Aborted : RunStatus.Completed;
        try
        {
            EquipmentEvent[] events;
            lock (_sync)
                events = [.. _events, new(DateTimeOffset.UtcNow, "STATE", LastStatus == RunStatus.Failed ? EquipmentState.Error.ToString() : EquipmentState.Ready.ToString())];
            await _store.EndAsync(RunId!, LastStatus.Value, events);
        }
        catch (Exception e) { Fault(e); LastStatus = RunStatus.Failed; }
        SetState(LastStatus == RunStatus.Failed ? EquipmentState.Error : EquipmentState.Ready);
    }
    private async Task RunPipeline()
    {
        var r = _recipe!;
        var frames = Channel.CreateBounded<Captured>(new BoundedChannelOptions(r.QueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var results = Channel.CreateBounded<Pending>(new BoundedChannelOptions(r.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        // Create every worker context before starting tasks; never strand a full producer queue on init failure.
        var engines = new List<IInspectionEngine>();
        try
        {
            for (int i = 0; i < r.Workers; i++)
                engines.Add(_engineFactory(r));
        }
        catch { foreach (var engine in engines) engine.Dispose(); throw; }
        var writer = Task.Run(async () => { await foreach (var pending in results.Reader.ReadAllAsync()) await Persist(pending); });
        var workers = engines.Select(engine => Task.Run(async () =>
        {
            using (engine)
            {
                await foreach (var c in frames.Reader.ReadAllAsync())
                    await results.Writer.WriteAsync(new(c, Inspect(engine, c)));
            }
        })).ToArray();
        try
        {
            foreach (var die in _dies)
            {
                await PauseBoundary();
                _acquisition!.Token.ThrowIfCancellationRequested();
                var captured = await Capture(die);
                Interlocked.Increment(ref _inflight);
                try
                {
                    await frames.Writer.WriteAsync(captured);
                }
                catch { captured.Frame.Dispose(); Interlocked.Decrement(ref _inflight); throw; }
            }
            await PauseBoundary();
        }
        catch (OperationCanceledException) when (_acquisition!.IsCancellationRequested) { }
        catch (Exception e) { Fault(e); }
        finally
        {
            frames.Writer.TryComplete();
            try
            {
                await Task.WhenAll(workers);
            }
            finally { results.Writer.TryComplete(); await writer; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _stopRequested = true;
            _acquisition?.Cancel();
            _resume.TrySetResult();
            await _run;
            await _camera.CloseAsync(default);
            await _stage.StopAsync(default);
            _acquisition?.Dispose();
            _disposed = true;
        }
        finally { _commands.Release(); }
    }
}
