using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SemiInspectX.Core;
using SemiInspectX.Infrastructure;

namespace SemiInspectX.Desktop;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> allowed, Action<Exception> error) : ICommand
{
    private bool _busy;
    public bool CanExecute(object? parameter) => !_busy && allowed();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        _busy = true;
        Refresh();
        try
        {
            await execute();
        }
        catch (Exception e) { error(e); }
        finally { _busy = false; Refresh(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "recipes/default.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
    public string Root { get; } = FindRoot();
    public string Output
    {
        get;
    }
    public SimulationSession? Session
    {
        get; private set;
    }
    public ObservableCollection<DieResult> Results { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    private IReadOnlyList<Die> _dies = [];
    public IReadOnlyList<Die> Dies
    {
        get => _dies; private set
        {
            _dies = value;
            Notify();
        }
    }
    private Die? _activeDie;
    public Die? ActiveDie
    {
        get => _activeDie; private set
        {
            _activeDie = value;
            Notify();
        }
    }
    public EquipmentState State => Session?.Controller.State ?? EquipmentState.Initializing;
    private string _datasetPath = "";
    public string DatasetPath
    {
        get => _datasetPath; set
        {
            _datasetPath = value;
            Notify();
        }
    }
    public string LotId { get; set; } = "LOT-DEMO";
    public string WaferId { get; set; } = "WAFER-01";
    public string FaultDie { get; set; } = "10";
    public IReadOnlyList<FaultKind> FaultKinds { get; } = Enum.GetValues<FaultKind>();
    public FaultKind SelectedFault
    {
        get; set;
    }
    public string RecipeName => Session?.Recipe.Name ?? "Not loaded";
    private Recipe? _historicalRecipe;
    private RunSummary? _historicalRun;
    public SemiInspectX.Core.Rect MeasurementRegion => _historicalRecipe?.Measurement ?? Session?.Recipe.Measurement ?? new(330, 330, 80, 120);
    private readonly Stopwatch _timer = new();
    public string Progress => $"{Results.Count} / {_historicalRun?.PlannedDies ?? Dies.Count}";
    public string PassRate => Results.All(r => r.Inspection.Verdict == Verdict.Error) ? "—" : $"{Results.Count(r => r.Inspection.Verdict == Verdict.Pass) * 100.0 / Results.Count(r => r.Inspection.Verdict != Verdict.Error):F1}%";
    public string Throughput => _timer.Elapsed.TotalSeconds <= 0 ? "—" : $"{Results.Count / _timer.Elapsed.TotalSeconds:F1} dies/s";
    public string LastStatus => _historicalRun?.Status ?? Session?.Controller.LastStatus?.ToString() ?? "—";
    private string _message = "Synthetic images and simulated devices. μm values use simulated calibration.";
    public string Message
    {
        get => _message; set
        {
            _message = value;
            Notify();
        }
    }
    private DieResult? _selectedResult;
    public DieResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            _selectedResult = value;
            Notify();
            Notify(nameof(MeasurementText));
            if (value is null)
            {
                Bitmap = null;
                return;
            }
            try
            {
                using var frame = Pgm.Read(Path.Combine(Output, value.ImagePath));
                var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Gray8, null, frame.Pixels, frame.Stride);
                bitmap.Freeze();
                Bitmap = bitmap;
            }
            catch (Exception e) { Message = e.Message; Bitmap = null; }
        }
    }
    private BitmapSource? _bitmap;
    public BitmapSource? Bitmap
    {
        get => _bitmap; private set
        {
            _bitmap = value;
            Notify();
        }
    }
    public string MeasurementText => SelectedResult is null ? "Select a persisted die to inspect its image." : $"{SelectedResult.Inspection.Verdict} · Width {SelectedResult.Inspection.WidthPixels:F3} px / {SelectedResult.Inspection.WidthMicrons:F3} μm · Shift ({SelectedResult.Inspection.ShiftX}, {SelectedResult.Inspection.ShiftY}) · {SelectedResult.Inspection.Defects.Count} anomaly regions";
    public AsyncCommand LoadCommand
    {
        get;
    }
    public AsyncCommand StartCommand
    {
        get;
    }
    public AsyncCommand PauseCommand
    {
        get;
    }
    public AsyncCommand ResumeCommand
    {
        get;
    }
    public AsyncCommand StopCommand
    {
        get;
    }
    public AsyncCommand ResetCommand
    {
        get;
    }
    public AsyncCommand HistoryCommand
    {
        get;
    }
    public AsyncCommand ExportCommand
    {
        get;
    }
    private readonly List<AsyncCommand> _commands = [];
    public MainViewModel()
    {
        DatasetPath = Path.Combine(Root, "datasets/generated/demo");
        Output = Path.Combine(Root, "artifacts/desktop");
        AsyncCommand Cmd(Func<Task> execute, Func<bool> allowed)
        {
            var c = new AsyncCommand(execute, allowed, e => Message = e.Message);
            _commands.Add(c);
            return c;
        }
        LoadCommand = Cmd(ChooseRecipe, () => State is EquipmentState.Idle or EquipmentState.Ready);
        StartCommand = Cmd(Start, () => State == EquipmentState.Ready);
        PauseCommand = Cmd(() => Session!.Controller.PauseAsync(), () => State == EquipmentState.Running);
        ResumeCommand = Cmd(() => Session!.Controller.ResumeAsync(), () => State == EquipmentState.Paused);
        StopCommand = Cmd(() => Session!.Controller.StopAsync(), () => State is EquipmentState.Running or EquipmentState.Paused);
        ResetCommand = Cmd(async () => { Session!.Faults.Plan = new(SelectedFault, int.Parse(FaultDie)); await Session.Controller.ResetAsync(); }, () => State == EquipmentState.Error);
        HistoryCommand = Cmd(() => { new HistoryWindow(Session!.Store, (run, results) => { _historicalRun = run; _historicalRecipe = System.Text.Json.JsonSerializer.Deserialize<Recipe>(run.RecipeJson, Recipe.Json); Results.Clear(); foreach (var r in results) Results.Add(r); SelectedResult = Results.FirstOrDefault(); Notify(nameof(MeasurementRegion)); Message = $"History: {run.Id} · {run.Status}"; UpdateStats(); }).ShowDialog(); return Task.CompletedTask; }, () => State is EquipmentState.Ready or EquipmentState.Idle or EquipmentState.Error);
        ExportCommand = Cmd(async () => { var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "inspection-results.csv" }; if (dialog.ShowDialog() == true) { await SqliteResultStore.ExportCsvAsync(dialog.FileName, Results.ToArray()); Message = $"Exported {Results.Count} records."; } }, () => Results.Count > 0);
    }
    private void Ui(Action action) => Application.Current.Dispatcher.InvokeAsync(action);
    public async Task Initialize()
    {
        await OpenSession();
        await Load(Recipe.Load(Path.Combine(Root, "recipes/default.json")));
    }
    private async Task OpenSession()
    {
        Session = new(DatasetPath, Output);
        Session.Controller.StateChanged += _ => Ui(() => { if (State is EquipmentState.Ready or EquipmentState.Error) { _timer.Stop(); ActiveDie = null; } Notify(nameof(State)); UpdateStats(); foreach (var command in _commands) command.Refresh(); });
        Session.Controller.EventRaised += e => Ui(() => { Events.Insert(0, $"{e.Timestamp.ToLocalTime():HH:mm:ss.fff}  {e.Code}  {e.Message}"); while (Events.Count > 200) Events.RemoveAt(Events.Count - 1); });
        Session.Controller.DieStarted += d => Ui(() => ActiveDie = d);
        Session.Controller.ResultSaved += r => Ui(() => { Results.Add(r); SelectedResult = r; UpdateStats(); });
        await Session.Controller.InitializeAsync();
    }
    private async Task ChooseRecipe()
    {
        var dialog = new OpenFileDialog { Filter = "Recipe JSON|*.json", InitialDirectory = Path.Combine(Root, "recipes") };
        if (dialog.ShowDialog() == true)
            await Load(Recipe.Load(dialog.FileName));
    }
    public async Task Load(Recipe recipe)
    {
        _historicalRun = null;
        _historicalRecipe = null;
        if (Path.GetFullPath(DatasetPath) != Session!.Dataset)
        {
            await Session.DisposeAsync();
            await OpenSession();
        }
        Session.Faults.Plan = new(SelectedFault, int.Parse(FaultDie));
        await Session.LoadAsync(recipe);
        Dies = Wafer.Create(DatasetPath);
        Results.Clear();
        SelectedResult = null;
        Notify(nameof(RecipeName));
        Notify(nameof(MeasurementRegion));
        UpdateStats();
    }
    public async Task Start()
    {
        _historicalRun = null;
        _historicalRecipe = null;
        Notify(nameof(MeasurementRegion));
        Session!.Faults.Plan = new(SelectedFault, int.Parse(FaultDie));
        if (SelectedFault == FaultKind.InvalidRecipe)
        {
            await Session.LoadAsync(Session.Recipe);
            return;
        }
        Results.Clear();
        SelectedResult = null;
        _timer.Restart();
        UpdateStats();
        await Session.Controller.StartAsync(LotId, WaferId);
    }
    private void UpdateStats()
    {
        Notify(nameof(Progress));
        Notify(nameof(PassRate));
        Notify(nameof(Throughput));
        Notify(nameof(LastStatus));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
            await Session.DisposeAsync();
    }
}
