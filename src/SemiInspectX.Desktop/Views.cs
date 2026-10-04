using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SemiInspectX.Core;
using SemiInspectX.Infrastructure;

namespace SemiInspectX.Desktop;

public sealed class WaferView : FrameworkElement
{
    public static readonly DependencyProperty DiesProperty = DependencyProperty.Register(nameof(Dies), typeof(IReadOnlyList<Die>), typeof(WaferView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ResultsProperty = DependencyProperty.Register(nameof(Results), typeof(ObservableCollection<DieResult>), typeof(WaferView), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty ActiveDieProperty = DependencyProperty.Register(nameof(ActiveDie), typeof(Die), typeof(WaferView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<Die>? Dies
    {
        get => (IReadOnlyList<Die>?)GetValue(DiesProperty); set => SetValue(DiesProperty, value);
    }
    public ObservableCollection<DieResult>? Results
    {
        get => (ObservableCollection<DieResult>?)GetValue(ResultsProperty); set => SetValue(ResultsProperty, value);
    }
    public Die? ActiveDie
    {
        get => (Die?)GetValue(ActiveDieProperty); set => SetValue(ActiveDieProperty, value);
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaferView)d;
        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= view.ResultsChanged;
        if (e.NewValue is INotifyCollectionChanged current)
            current.CollectionChanged += view.ResultsChanged;
        view.InvalidateVisual();
    }
    private void ResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double size = Math.Min(ActualWidth, ActualHeight), cx = ActualWidth / 2, cy = ActualHeight / 2;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(16, 28, 44)), new Pen(new SolidColorBrush(Color.FromRgb(65, 86, 114)), 1.5), new(cx, cy), size * .46, size * .46);
        if (Dies is null || Dies.Count == 0)
            return;
        int extent = Dies.Max(d => Math.Max(Math.Abs(d.X), Math.Abs(d.Y))) + 1;
        double cell = size * .87 / (2 * extent + 1);
        var results = Results?.ToDictionary(r => r.Die.Index) ?? [];
        foreach (var die in Dies)
        {
            string color = results.TryGetValue(die.Index, out var r) ? r.Inspection.Verdict switch
            {
                Verdict.Pass => "#61E1C1",
                Verdict.Fail => "#FFB971",
                _ => "#F47191"
            } : ActiveDie?.Index == die.Index ? "#74ACFF" : "#30435F";
            dc.DrawRoundedRectangle((Brush)new BrushConverter().ConvertFromString(color)!, null, new(cx + die.X * cell - cell * .4, cy + die.Y * cell - cell * .4, cell * .8, cell * .8), 2, 2);
        }
    }
}
public sealed class InspectionView : FrameworkElement
{
    public static readonly DependencyProperty MeasurementRegionProperty = DependencyProperty.Register(nameof(MeasurementRegion), typeof(SemiInspectX.Core.Rect), typeof(InspectionView), new FrameworkPropertyMetadata(new SemiInspectX.Core.Rect(330, 330, 80, 120), FrameworkPropertyMetadataOptions.AffectsRender));
    public SemiInspectX.Core.Rect MeasurementRegion
    {
        get => (SemiInspectX.Core.Rect)GetValue(MeasurementRegionProperty); set => SetValue(MeasurementRegionProperty, value);
    }
    public static readonly DependencyProperty BitmapProperty = DependencyProperty.Register(nameof(Bitmap), typeof(BitmapSource), typeof(InspectionView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(nameof(Result), typeof(DieResult), typeof(InspectionView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public BitmapSource? Bitmap
    {
        get => (BitmapSource?)GetValue(BitmapProperty); set => SetValue(BitmapProperty, value);
    }
    public DieResult? Result
    {
        get => (DieResult?)GetValue(ResultProperty); set => SetValue(ResultProperty, value);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Bitmap is null)
            return;
        double scale = Math.Min(ActualWidth / Bitmap.PixelWidth, ActualHeight / Bitmap.PixelHeight);
        double ox = (ActualWidth - Bitmap.PixelWidth * scale) / 2, oy = (ActualHeight - Bitmap.PixelHeight * scale) / 2;
        dc.DrawImage(Bitmap, new(ox, oy, Bitmap.PixelWidth * scale, Bitmap.PixelHeight * scale));
        if (Result is null || Result.Inspection.Verdict == Verdict.Error)
            return;
        foreach (var d in Result.Inspection.Defects)
            dc.DrawRectangle(null, new Pen(Brushes.OrangeRed, 2), new(ox + (d.X + Result.Inspection.ShiftX) * scale, oy + (d.Y + Result.Inspection.ShiftY) * scale, d.Width * scale, d.Height * scale));
        var roi = MeasurementRegion;
        dc.DrawRectangle(null, new Pen(Brushes.Turquoise, 1), new(ox + (roi.X + Result.Inspection.ShiftX) * scale, oy + (roi.Y + Result.Inspection.ShiftY) * scale, roi.Width * scale, roi.Height * scale));
    }
}
public sealed class HistoryWindow : Window
{
    public HistoryWindow(SqliteResultStore store, Action<RunSummary, IReadOnlyList<DieResult>> select)
    {
        Title = "Inspection history";
        Width = 1020;
        Height = 560;
        Background = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        var panel = new DockPanel { Margin = new Thickness(18) };
        Content = panel;
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(filters, Dock.Top);
        panel.Children.Add(filters);
        var lot = new TextBox { Width = 135, ToolTip = "Exact Lot ID (blank = all)" };
        var wafer = new TextBox { Width = 135, ToolTip = "Exact Wafer ID (blank = all)" };
        var run = new TextBox { Width = 220, ToolTip = "Exact Run ID (blank = all)" };
        var verdict = new ComboBox { ItemsSource = new[] { "All", "Pass", "Fail", "Error" }, SelectedIndex = 0, Width = 100 };
        foreach (var pair in new[] { ("Lot", lot), ("Wafer", wafer), ("Run", run) })
        {
            filters.Children.Add(new TextBlock { Text = pair.Item1, Margin = new(8, 12, 5, 0) });
            filters.Children.Add(pair.Item2);
        }
        filters.Children.Add(verdict);
        var query = new Button { Content = "Query", Margin = new(10, 5, 0, 12) };
        filters.Children.Add(query);
        var grid = new DataGrid { AutoGenerateColumns = true };
        panel.Children.Add(grid);
        async Task Query()
        {
            grid.ItemsSource = await store.QueryRunsAsync(string.IsNullOrWhiteSpace(lot.Text) ? null : lot.Text, string.IsNullOrWhiteSpace(wafer.Text) ? null : wafer.Text, string.IsNullOrWhiteSpace(run.Text) ? null : run.Text);
        }
        query.Click += async (_, _) => { try { await Query(); } catch (Exception e) { MessageBox.Show(e.Message); } };
        Loaded += async (_, _) => { try { await Query(); } catch (Exception e) { MessageBox.Show(e.Message); } };
        grid.MouseDoubleClick += async (_, _) =>
        {
            if (grid.SelectedItem is not RunSummary r)
                return;
            try
            {
                select(r, await store.QueryResultsAsync(r.Id, Enum.TryParse<Verdict>(verdict.SelectedItem?.ToString(), out var v) ? v : null));
                Close();
            }
            catch (Exception e) { MessageBox.Show(e.Message); }
        };
    }
}
