using System.Buffers;
using System.Text.Json;

namespace SemiInspectX.Core;

public enum EquipmentState
{
    Initializing, Idle, Ready, Running, Pausing, Paused, Stopping, Error
}
public enum RunStatus
{
    Running, Completed, Aborted, Failed
}
public enum Verdict
{
    Pass, Fail, Error
}
public enum FaultKind
{
    None, CameraDisconnected, StageTimeout, InvalidImage, InvalidRecipe, DatabaseWrite
}
public sealed record FaultPlan(FaultKind Kind = FaultKind.None, int DieIndex = 10);
public readonly record struct Rect(int X, int Y, int Width, int Height);
public sealed record Recipe
{
    public string Name { get; init; } = "Synthetic-Line-32";
    public int Version { get; init; } = 1;
    public string ReferenceImage { get; init; } = "reference.pgm";
    public Rect Marker { get; init; } = new(25, 25, 48, 48);
    public Rect Inspection { get; init; } = new(120, 120, 180, 180);
    public Rect Measurement { get; init; } = new(330, 330, 80, 120);
    public int MaxShift { get; init; } = 5;
    public int Threshold { get; init; } = 30;
    public int MinArea { get; init; } = 9;
    public double MinAlignment { get; init; } = 0.8;
    public double MinWidth { get; init; } = 30;
    public double MaxWidth { get; init; } = 34;
    public double MicronsPerPixel { get; init; } = 0.5;
    public int Workers { get; init; } = 2;
    public int QueueCapacity { get; init; } = 8;
    public int StageDelayMs { get; init; } = 2;
    public int CameraDelayMs { get; init; } = 2;
    public void Validate()
    {
        static bool Inside(Rect r) => r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 && (long)r.X + r.Width <= 512 && (long)r.Y + r.Height <= 512;
        if (string.IsNullOrWhiteSpace(Name) || Version < 1 || string.IsNullOrWhiteSpace(ReferenceImage) ||
            !Inside(Marker) || !Inside(Inspection) || !Inside(Measurement) || Measurement.Height < 20 ||
            MaxShift is < 0 or > 32 || Marker.X < MaxShift || Marker.Y < MaxShift ||
            Marker.X + Marker.Width + MaxShift > 512 || Marker.Y + Marker.Height + MaxShift > 512 ||
            Threshold is < 1 or > 255 || MinArea < 1 || !double.IsFinite(MinAlignment) || MinAlignment is <= 0 or > 1 ||
            !double.IsFinite(MinWidth) || !double.IsFinite(MaxWidth) || MinWidth <= 0 || MaxWidth < MinWidth ||
            !double.IsFinite(MicronsPerPixel) || MicronsPerPixel <= 0 || Workers is < 1 or > 16 ||
            QueueCapacity is < 1 or > 128 || StageDelayMs is < 0 or > 10000 || CameraDelayMs is < 0 or > 10000)
            throw new ArgumentException("Invalid recipe: ROI, tolerance, calibration or pipeline parameters.");
    }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static Recipe Load(string path) => JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Json) ?? throw new ArgumentException("Empty recipe.");
}
public sealed record Die(int Index, int X, int Y, string ImagePath);
public sealed record Defect(int X, int Y, int Width, int Height, int Area);
public sealed record Inspection(Verdict Verdict, int ShiftX, int ShiftY, double Alignment,
    double WidthPixels, double WidthMicrons, double AlgorithmMs, IReadOnlyList<Defect> Defects, string? Error = null);
public sealed record RunInfo(string Id, string LotId, string WaferId, DateTimeOffset StartedAt, Recipe Recipe, int PlannedDies);
public sealed record DieResult(string RunId, Die Die, Inspection Inspection, DateTimeOffset CapturedAt,
    double EndToEndMs, string ImagePath);
public sealed record EquipmentEvent(DateTimeOffset Timestamp, string Code, string Message);
public sealed record RunSummary(string Id, string LotId, string WaferId, string Status, int PlannedDies, int SavedDies, int PassedDies, string RecipeJson);

// One owner at a time. Buffers return to the pool only after inspection and persistence.
public sealed class ImageFrame : IDisposable
{
    private byte[]? _pixels;
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int Stride => Width;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(ImageFrame));
    public ImageFrame(int width, int height, ReadOnlySpan<byte> source)
    {
        if (width <= 0 || height <= 0 || source.Length < checked(width * height))
            throw new ArgumentException("Invalid image dimensions.");
        Width = width;
        Height = height;
        _pixels = ArrayPool<byte>.Shared.Rent(width * height);
        source[..(width * height)].CopyTo(_pixels);
    }
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
            ArrayPool<byte>.Shared.Return(pixels);
    }
}
