using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SemiInspectX.Core;

namespace SemiInspectX.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _closed;
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        var args = Environment.GetCommandLineArgs();
        int dataset = Array.IndexOf(args, "--dataset");
        if (dataset >= 0 && dataset + 1 < args.Length)
            _vm.DatasetPath = args[dataset + 1];
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.Initialize();
                if (args.Contains("--snapshot"))
                {
                    await _vm.Start();
                    await _vm.Session!.Controller.Completion;
                    await Task.Delay(300);
                    _vm.SelectedResult = _vm.Results.FirstOrDefault(r => r.Inspection.Defects.Count > 0);
                    await Task.Delay(100);
                    SaveSnapshot(Path.Combine(_vm.Root, "artifacts/screenshots/workstation.png"));
                    Close();
                }
                if (args.Contains("--demo"))
                    await Demo();
            }
            catch (Exception e) { _vm.Message = e.ToString(); File.WriteAllText(Path.Combine(_vm.Root, "artifacts/desktop-error.txt"), e.ToString()); }
        };
        Closing += CloseAsync;
    }
    private async void CloseAsync(object? sender, CancelEventArgs e)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        try
        {
            await _vm.DisposeAsync();
            _closed = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception error) { _closing = false; _vm.Message = error.Message; }
    }
    private void SaveSnapshot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UpdateLayout();
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
    private async Task Demo()
    {
        // Render only this application's surface. No other desktop content is captured.
        string dir = Path.Combine(_vm.Root, "artifacts/demo-frames");
        Directory.CreateDirectory(dir);
        int frame = 0;
        bool recording = true;
        async Task Record()
        {
            while (recording)
            {
                SaveSnapshot(Path.Combine(dir, $"frame-{frame++:D4}.png"));
                await Task.Delay(1000);
            }
        }
        var recordingTask = Record();
        async Task Hold(string message, int seconds)
        {
            _vm.Message = message;
            await Task.Delay(seconds * 1000);
        }
        await _vm.Load(_vm.Session!.Recipe with
        {
            StageDelayMs = 80,
            CameraDelayMs = 40
        });
        await Hold("01 / Architecture: WPF + C# control + C++17/OpenCV + SQLite. All images and devices are simulated.", 15);
        await _vm.Start();
        await Task.Delay(4000);
        await _vm.Session.Controller.PauseAsync();
        await Hold("02 / Pause: no new dies scheduled; all acquired frames are processed and persisted before Paused.", 15);
        await _vm.Session.Controller.ResumeAsync();
        await _vm.Session.Controller.Completion;
        _vm.SelectedResult = _vm.Results.FirstOrDefault(r => r.Inspection.Defects.Count > 0);
        await Hold("03 / Completed: orange boxes identify anomalies. Turquoise ROI measures line width; 0.5 um/pixel is simulated calibration.", 20);
        _vm.SelectedFault = FaultKind.CameraDisconnected;
        await _vm.Start();
        await _vm.Session.Controller.Completion;
        await Hold("04 / Fault at die 10: camera disconnect stops scheduling, raises an alarm and marks this run Failed.", 20);
        _vm.SelectedFault = FaultKind.None;
        _vm.Session.Faults.Clear();
        await _vm.Session.Controller.ResetAsync();
        await _vm.Start();
        await _vm.Session.Controller.Completion;
        await Hold("05 / Reset: reconnect devices, start a new RunId, and preserve the failed run for traceability.", 20);
        await _vm.Start();
        await Task.Delay(3000);
        await _vm.Session.Controller.StopAsync();
        await Hold("06 / Stop: cancel simulated acquisition, drain acquired frames, persist partial results, and mark Aborted.", 20);
        _vm.SelectedFault = FaultKind.DatabaseWrite;
        await _vm.Start();
        await _vm.Session.Controller.Completion;
        await Hold("07 / Database fault: a result is marked complete only after persistence succeeds. Storage failures raise an alarm.", 20);
        _vm.SelectedFault = FaultKind.None;
        _vm.Session.Faults.Clear();
        await _vm.Session.Controller.ResetAsync();
        await _vm.Start();
        await _vm.Session.Controller.Completion;
        await Hold("08 / Verification: CTest + xUnit; 1000 start/stop cycles; seeded evaluation; serial/pipeline equivalence. See reports for measured results.", 20);
        SaveSnapshot(Path.Combine(_vm.Root, "artifacts/screenshots/workstation.png"));
        recording = false;
        await recordingTask;
        Close();
    }
}
