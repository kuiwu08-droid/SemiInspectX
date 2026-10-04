namespace SemiInspectX.Core;

public interface IStage
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task MoveToAsync(Die die, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
public interface ICamera
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ImageFrame> CaptureAsync(Die die, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}
public interface IInspectionEngine : IDisposable
{
    Inspection Inspect(ImageFrame frame);
}
public interface IResultStore
{
    Task BeginAsync(RunInfo run);
    Task<DieResult> SaveAsync(DieResult result, ImageFrame frame, long acquisitionTicks);
    Task EndAsync(string runId, RunStatus status, IReadOnlyList<EquipmentEvent> events);
    Task<IReadOnlyList<RunSummary>> QueryRunsAsync(string? lot = null, string? wafer = null, string? run = null);
    Task<IReadOnlyList<DieResult>> QueryResultsAsync(string runId, Verdict? verdict = null);
}
