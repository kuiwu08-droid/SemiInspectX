using System.Text;
using System.Text.Json;
using SemiInspectX.Core;

namespace SemiInspectX.Infrastructure;

public static class Pgm
{
    public static ImageFrame Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int cursor = 0;
        string Token()
        {
            while (cursor < bytes.Length)
            {
                if (bytes[cursor] == '#')
                {
                    while (cursor < bytes.Length && bytes[cursor] != '\n')
                        cursor++;
                }
                else if (char.IsWhiteSpace((char)bytes[cursor]))
                    cursor++;
                else
                    break;
            }
            int start = cursor;
            while (cursor < bytes.Length && !char.IsWhiteSpace((char)bytes[cursor]))
                cursor++;
            return Encoding.ASCII.GetString(bytes, start, cursor - start);
        }
        if (Token() != "P5")
            throw new InvalidDataException("Expected binary PGM (P5).");
        int w = int.Parse(Token()), h = int.Parse(Token());
        if (Token() != "255" || w is <= 0 or > 8192 || h is <= 0 or > 8192 || cursor >= bytes.Length)
            throw new InvalidDataException("Invalid PGM header.");
        if (bytes[cursor++] == '\r' && cursor < bytes.Length && bytes[cursor] == '\n')
            cursor++;
        if (bytes.Length - cursor != checked(w * h))
            throw new InvalidDataException("Truncated PGM payload.");
        return new(w, h, bytes.AsSpan(cursor));
    }
    public static async Task WriteAsync(string path, ImageFrame frame)
    {
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.ASCII.GetBytes($"P5\n{frame.Width} {frame.Height}\n255\n"));
        await file.WriteAsync(frame.Pixels.AsMemory(0, frame.Width * frame.Height));
    }
}
public sealed class FaultSettings
{
    public FaultPlan Plan { get; set; } = new();
    public int InspectionDelayMs
    {
        get; set;
    }
    public int WriteDelayMs
    {
        get; set;
    }
    public bool Matches(FaultKind kind, int index) => Plan.Kind == kind && Plan.DieIndex == index;
    public void Clear() => Plan = new();
}
public sealed class VirtualStage(FaultSettings faults, Func<Recipe> recipe) : IStage
{
    private bool _open;
    public Task InitializeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _open = true;
        return Task.CompletedTask;
    }
    public async Task MoveToAsync(Die die, CancellationToken token)
    {
        if (!_open)
            throw new InvalidOperationException("Stage not initialized.");
        if (faults.Matches(FaultKind.StageTimeout, die.Index))
        {
            await Task.Delay(20, token);
            throw new TimeoutException($"STAGE_TIMEOUT die={die.Index}");
        }
        await Task.Delay(recipe().StageDelayMs, token);
    }
    public Task StopAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
public sealed class VirtualCamera(FaultSettings faults, Func<Recipe> recipe) : ICamera
{
    private bool _open;
    public Task InitializeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _open = true;
        return Task.CompletedTask;
    }
    public async Task<ImageFrame> CaptureAsync(Die die, CancellationToken token)
    {
        if (!_open)
            throw new InvalidOperationException("Camera not initialized.");
        if (faults.Matches(FaultKind.CameraDisconnected, die.Index))
        {
            _open = false;
            throw new IOException($"CAMERA_DISCONNECTED die={die.Index}");
        }
        await Task.Delay(recipe().CameraDelayMs, token);
        token.ThrowIfCancellationRequested();
        return faults.Matches(FaultKind.InvalidImage, die.Index) ? new(1, 1, [0]) : Pgm.Read(die.ImagePath);
    }
    public Task CloseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _open = false;
        return Task.CompletedTask;
    }
}
public sealed record TruthFrame(int Index, string Image, string Mask, string Kind, int ShiftX, int ShiftY, int Brightness,
    double WidthPixels, int DefectX, int DefectY, int DefectWidth, int DefectHeight);
public sealed record DatasetManifest(int Seed, double NoiseSigma, List<TruthFrame> Frames);
public static class Wafer
{
    public static DatasetManifest Manifest(string directory) => JsonSerializer.Deserialize<DatasetManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")), Recipe.Json) ?? throw new InvalidDataException("Missing manifest.");
    public static IReadOnlyList<Die> Create(string directory, int count = 100)
    {
        // Scan path depends only on available image names, never on truth labels.
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        int radius = (int)Math.Ceiling(Math.Sqrt(count / Math.PI)) + 2;
        var positions = (from y in Enumerable.Range(-radius, 2 * radius + 1)
                         from x in Enumerable.Range(-radius, 2 * radius + 1)
                         select (x, y)).OrderBy(p => p.x * p.x + p.y * p.y).ThenBy(p => p.y).ThenBy(p => p.x).Take(count)
                       .GroupBy(p => p.y).OrderBy(g => g.Key).SelectMany((g, row) => row % 2 == 0 ? g.OrderBy(p => p.x) : g.OrderByDescending(p => p.x)).ToArray();
        return positions.Select((p, i) => new Die(i, p.x, p.y, Path.Combine(directory, $"die-{i}.pgm"))).ToArray();
    }
}
public sealed class SimulationSession : IAsyncDisposable
{
    public FaultSettings Faults { get; } = new();
    public SqliteResultStore Store
    {
        get;
    }
    public EquipmentController Controller
    {
        get;
    }
    public Recipe Recipe { get; private set; } = new();
    public string Dataset
    {
        get;
    }
    public SimulationSession(string dataset, string output)
    {
        Dataset = Path.GetFullPath(dataset);
        Store = new(output, Faults);
        Controller = new(new VirtualStage(Faults, () => Recipe), new VirtualCamera(Faults, () => Recipe), Store,
            r => new NativeEngine(r, Dataset, Faults.InspectionDelayMs));
    }
    public async Task LoadAsync(Recipe recipe, int count = 100)
    {
        if (Faults.Plan.Kind == FaultKind.InvalidRecipe)
            recipe = recipe with
            {
                Threshold = -1
            };
        await Controller.LoadAsync(recipe, Wafer.Create(Dataset, count));
        Recipe = recipe;
    }
    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        await Store.DisposeAsync();
    }
}
