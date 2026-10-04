using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SemiInspectX.Core;

namespace SemiInspectX.Infrastructure;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int X, Y, Width, Height; public NativeRect(Rect r)
    {
        X = r.X;
        Y = r.Y;
        Width = r.Width;
        Height = r.Height;
    }
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRecipe
{
    public NativeRect Marker, Inspection, Measurement;
    public int MaxShift, Threshold, MinArea;
    public double MinAlignment, MinWidth, MaxWidth, MicronsPerPixel;
    public NativeRecipe(Recipe r)
    {
        Marker = new(r.Marker);
        Inspection = new(r.Inspection);
        Measurement = new(r.Measurement);
        MaxShift = r.MaxShift;
        Threshold = r.Threshold;
        MinArea = r.MinArea;
        MinAlignment = r.MinAlignment;
        MinWidth = r.MinWidth;
        MaxWidth = r.MaxWidth;
        MicronsPerPixel = r.MicronsPerPixel;
    }
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSummary
{
    public int ShiftX, ShiftY, DefectCount, Passed; public double Alignment, WidthPixels, WidthMicrons, Milliseconds;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDefect
{
    public int X, Y, Width, Height, Area;
}
internal sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public EngineHandle(IntPtr pointer) : base(true) { SetHandle(pointer); }
    protected override bool ReleaseHandle()
    {
        Native.Destroy(handle);
        return true;
    }
}
internal sealed class ResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ResultHandle(IntPtr pointer) : base(true) { SetHandle(pointer); }
    protected override bool ReleaseHandle()
    {
        Native.Release(handle);
        return true;
    }
}
internal static unsafe partial class Native
{
    [LibraryImport("semiinspect", EntryPoint = "si_version")] internal static partial int Version();
    [LibraryImport("semiinspect", EntryPoint = "si_create")] internal static partial int Create(byte* pixels, int width, int height, int stride, in NativeRecipe recipe, out IntPtr engine);
    [LibraryImport("semiinspect", EntryPoint = "si_destroy")] internal static partial void Destroy(IntPtr engine);
    [LibraryImport("semiinspect", EntryPoint = "si_inspect")] internal static partial int Inspect(EngineHandle engine, byte* pixels, int width, int height, int stride, out IntPtr result);
    [LibraryImport("semiinspect", EntryPoint = "si_get_summary")] internal static partial int Summary(ResultHandle result, out NativeSummary summary);
    [LibraryImport("semiinspect", EntryPoint = "si_get_defect")] internal static partial int Defect(ResultHandle result, int index, out NativeDefect defect);
    [LibraryImport("semiinspect", EntryPoint = "si_release_result")] internal static partial void Release(IntPtr result);
}
public sealed class NativeEngine : IInspectionEngine
{
    private readonly EngineHandle _handle;
    private readonly object _gate = new();
    private readonly int _delayMs;
    public static int AbiVersion => Native.Version();
    public unsafe NativeEngine(Recipe recipe, string dataset, int delayMs = 0)
    {
        recipe.Validate();
        if (Native.Version() != 1)
            throw new InvalidOperationException("Unsupported native ABI.");
        _delayMs = delayMs;
        using var reference = Pgm.Read(Path.Combine(dataset, recipe.ReferenceImage));
        var nativeRecipe = new NativeRecipe(recipe);
        fixed (byte* pixels = reference.Pixels)
        {
            Check(Native.Create(pixels, reference.Width, reference.Height, reference.Stride, in nativeRecipe, out var p));
            _handle = new(p);
        }
    }
    private static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException(status switch
            {
                1 => "Native engine: invalid frame or recipe.",
                2 => "Native engine: alignment failed.",
                _ => "Native engine: processing failed."
            });
    }
    public unsafe Inspection Inspect(ImageFrame frame)
    {
        lock (_gate)
        {
            if (_delayMs > 0)
                Thread.Sleep(_delayMs);
            fixed (byte* pixels = frame.Pixels)
            {
                Check(Native.Inspect(_handle, pixels, frame.Width, frame.Height, frame.Stride, out var p));
                using var result = new ResultHandle(p);
                Check(Native.Summary(result, out var s));
                var defects = new List<Defect>(s.DefectCount);
                for (int i = 0; i < s.DefectCount; i++)
                {
                    Check(Native.Defect(result, i, out var d));
                    defects.Add(new(d.X, d.Y, d.Width, d.Height, d.Area));
                }
                return new(s.Passed == 1 ? Verdict.Pass : Verdict.Fail, s.ShiftX, s.ShiftY, s.Alignment, s.WidthPixels, s.WidthMicrons, s.Milliseconds, defects);
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
            _handle.Dispose();
    }
}
