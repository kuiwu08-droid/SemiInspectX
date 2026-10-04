using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SemiInspectX.Core;
using SemiInspectX.Infrastructure;
using Xunit;

namespace SemiInspectX.Tests;

public sealed class RegressionTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SemiInspectX.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root missing.");
        }
    }
    private static string Dataset => Path.Combine(Root, "datasets/generated/demo");
    private static Recipe Fast => Recipe.Load(Path.Combine(Root, "recipes/default.json")) with { StageDelayMs = 0, CameraDelayMs = 0 };
    private static SimulationSession Session() => new(Dataset, Path.Combine(Root, "artifacts/tests/runs", Guid.NewGuid().ToString("N")));
    [Fact]
    public void RecipeRejectsInvalidConfiguration()
    {
        Assert.Throws<ArgumentException>(() => (Fast with { Threshold = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Fast with { MicronsPerPixel = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (Fast with { Marker = new(0, 0, 48, 48) }).Validate());
    }
    [Fact]
    public void NativeAbiAndInvalidFrames()
    {
        Assert.Equal(1, NativeEngine.AbiVersion);
        using var engine = new NativeEngine(Fast, Dataset);
        using var invalid = new ImageFrame(1, 1, [0]);
        Assert.Throws<InvalidOperationException>(() => engine.Inspect(invalid));
        using var blank = new ImageFrame(512, 512, new byte[512 * 512]);
        Assert.Throws<InvalidOperationException>(() => engine.Inspect(blank));
    }
    [Fact]
    public void KnownGeometryDetectsAllCases()
    {
        using var engine = new NativeEngine(Fast, Dataset);
        var truth = Wafer.Manifest(Dataset);
        foreach (var t in truth.Frames.Take(10))
        {
            using var frame = Pgm.Read(Path.Combine(Dataset, t.Image));
            var result = engine.Inspect(frame);
            Assert.Equal(t.ShiftX, result.ShiftX);
            Assert.Equal(t.ShiftY, result.ShiftY);
            Assert.InRange(Math.Abs(t.WidthPixels - result.WidthPixels), 0, 0.5);
            Assert.Equal(t.Kind == "normal" ? Verdict.Pass : Verdict.Fail, result.Verdict);
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scan100DiesAndPersistSnapshot(bool serial)
    {
        await using var s = Session();
        await s.Controller.InitializeAsync();
        await s.LoadAsync(Fast);
        int notifications = 0;
        s.Controller.ResultSaved += _ => notifications++;
        await s.Controller.StartAsync("LOT-TEST", "WAFER-TEST", serial);
        await s.Controller.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStatus.Completed, s.Controller.LastStatus);
        Assert.Equal(EquipmentState.Ready, s.Controller.State);
        var results = await s.Store.QueryResultsAsync(s.Controller.RunId!);
        Assert.Equal(100, results.Count);
        Assert.Equal(100, notifications);
        Assert.Equal(100, results.Select(r => (r.Die.X, r.Die.Y)).Distinct().Count());
        Assert.All(results, r => Assert.True(r.EndToEndMs > 0));
        foreach (var result in results)
        {
            using var source = Pgm.Read(result.Die.ImagePath);
            using var saved = Pgm.Read(Path.Combine(Path.GetDirectoryName(s.Store.DatabasePath)!, result.ImagePath));
            Assert.True(source.Pixels.AsSpan(0, source.Width * source.Height).SequenceEqual(saved.Pixels.AsSpan(0, saved.Width * saved.Height)), "Persisted image must match its die; pooled buffers must not be reused early.");
        }
        var run = Assert.Single(await s.Store.QueryRunsAsync("LOT-TEST", "WAFER-TEST", s.Controller.RunId));
        Assert.Equal(100, run.SavedDies);
        Assert.Equal(Fast, JsonSerializer.Deserialize<Recipe>(run.RecipeJson, Recipe.Json));
        Assert.Equal(50, (await s.Store.QueryResultsAsync(s.Controller.RunId!, Verdict.Pass)).Count);
        using var connection = new SqliteConnection($"Data Source={s.Store.DatabasePath}");
        connection.Open();
        using var terminal = connection.CreateCommand();
        terminal.CommandText = "SELECT COUNT(*) FROM Events WHERE RunId=$run AND Code='STATE' AND Message='Ready'";
        terminal.Parameters.AddWithValue("$run", s.Controller.RunId);
        Assert.Equal(1L, terminal.ExecuteScalar());
    }
    [Theory]
    [InlineData(FaultKind.CameraDisconnected)]
    [InlineData(FaultKind.StageTimeout)]
    [InlineData(FaultKind.InvalidImage)]
    [InlineData(FaultKind.DatabaseWrite)]
    public async Task FaultAlarmAndResetRecovery(FaultKind kind)
    {
        await using var s = Session();
        await s.Controller.InitializeAsync();
        await s.LoadAsync(Fast);
        int alarms = 0;
        s.Controller.EventRaised += e => { if (e.Code == "ALARM") alarms++; };
        s.Faults.Plan = new(kind, 5);
        await s.Controller.StartAsync();
        await s.Controller.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(EquipmentState.Error, s.Controller.State);
        Assert.Equal(RunStatus.Failed, s.Controller.LastStatus);
        Assert.True(alarms > 0);
        string old = s.Controller.RunId!;
        Assert.Equal("Failed", Assert.Single(await s.Store.QueryRunsAsync(run: old)).Status);
        s.Faults.Clear();
        await s.Controller.ResetAsync();
        await s.Controller.StartAsync();
        await s.Controller.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(old, s.Controller.RunId);
        Assert.Equal(RunStatus.Completed, s.Controller.LastStatus);
        Assert.Equal(100, s.Controller.SavedDies);
    }
    [Fact]
    public async Task InvalidRecipeCannotEnterReady()
    {
        await using var s = Session();
        await s.Controller.InitializeAsync();
        s.Faults.Plan = new(FaultKind.InvalidRecipe, 0);
        await Assert.ThrowsAsync<ArgumentException>(() => s.LoadAsync(Fast));
        Assert.Equal(EquipmentState.Idle, s.Controller.State);
        s.Faults.Clear();
        await s.LoadAsync(Fast);
        Assert.Equal(EquipmentState.Ready, s.Controller.State);
    }
    [Fact]
    public async Task PauseDrainsAndResumeKeepsRun()
    {
        await using var s = Session();
        await s.Controller.InitializeAsync();
        await s.LoadAsync(Fast with
        {
            StageDelayMs = 2,
            QueueCapacity = 1
        });
        s.Faults.InspectionDelayMs = 5;
        await s.Controller.StartAsync();
        await Task.Delay(30);
        await s.Controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EquipmentState.Paused, s.Controller.State);
        string id = s.Controller.RunId!;
        int saved = s.Controller.SavedDies;
        await Task.Delay(40);
        Assert.Equal(saved, s.Controller.SavedDies);
        Assert.Equal(saved, (await s.Store.QueryResultsAsync(id)).Count);
        await s.Controller.ResumeAsync();
        await s.Controller.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(id, s.Controller.RunId);
        Assert.Equal(100, s.Controller.SavedDies);
    }
    [Fact]
    public async Task StopUnderBackpressureAndRejectDuplicateCommands()
    {
        await using var s = Session();
        s.Faults.InspectionDelayMs = 10;
        s.Faults.WriteDelayMs = 10;
        await s.Controller.InitializeAsync();
        await s.LoadAsync(Fast with
        {
            QueueCapacity = 1
        });
        await s.Controller.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Controller.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.LoadAsync(Fast));
        await Task.Delay(30);
        await s.Controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunStatus.Aborted, s.Controller.LastStatus);
        Assert.Equal(EquipmentState.Ready, s.Controller.State);
        var results = await s.Store.QueryResultsAsync(s.Controller.RunId!);
        Assert.Equal(s.Controller.SavedDies, results.Count);
        Assert.True(results.Count < 100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Controller.ResumeAsync());
    }
    [Fact]
    public async Task SerialAndPipelineProduceIdenticalResults()
    {
        var fingerprints = new List<string>();
        foreach (bool serial in new[] { true, false })
        {
            await using var s = Session();
            await s.Controller.InitializeAsync();
            await s.LoadAsync(Fast, 20);
            await s.Controller.StartAsync(serial: serial);
            await s.Controller.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            fingerprints.Add(JsonSerializer.Serialize((await s.Store.QueryResultsAsync(s.Controller.RunId!)).Select(r => new { r.Die.Index, r.Inspection.Verdict, r.Inspection.WidthPixels, r.Inspection.ShiftX, r.Inspection.ShiftY, r.Inspection.Defects })));
        }
        Assert.Equal(fingerprints[0], fingerprints[1]);
    }
    [Fact]
    [Trait("Category", "Stress")]
    public async Task OneThousandStartStopCycles()
    {
        await using var s = Session();
        await s.Controller.InitializeAsync();
        await s.LoadAsync(Fast with
        {
            StageDelayMs = 1
        }, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        long initial = Process.GetCurrentProcess().PrivateMemorySize64;
        for (int i = 0; i < 1000; i++)
        {
            await s.Controller.StartAsync().WaitAsync(timeout.Token);
            // Exercise the pooled image/native call/persistence lifetime, not just empty scheduling.
            while (s.Controller.SavedDies == 0 && !s.Controller.Completion.IsCompleted)
                await Task.Delay(1, timeout.Token);
            if (s.Controller.State == EquipmentState.Running)
            {
                try
                {
                    await s.Controller.StopAsync().WaitAsync(timeout.Token);
                }
                catch (InvalidOperationException) when (s.Controller.State == EquipmentState.Ready && s.Controller.Completion.IsCompleted) { }
            }
            await s.Controller.Completion.WaitAsync(timeout.Token);
            Assert.Equal(EquipmentState.Ready, s.Controller.State);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long growth = Process.GetCurrentProcess().PrivateMemorySize64 - initial;
        Assert.InRange(growth, long.MinValue, 200L * 1024 * 1024);
        var runs = await s.Store.QueryRunsAsync();
        Assert.Equal(1000, runs.Count);
        Assert.All(runs, r => Assert.True(r.SavedDies >= 1));
        await File.WriteAllTextAsync(Path.Combine(Root, "artifacts/tests/stress.json"), JsonSerializer.Serialize(new
        {
            Cycles = 1000,
            MinimumSavedFramesPerCycle = 1,
            PrivateMemoryGrowthBytes = growth,
            MaximumAllowedGrowthBytes = 200L * 1024 * 1024
        }, Recipe.Json));
    }
}
