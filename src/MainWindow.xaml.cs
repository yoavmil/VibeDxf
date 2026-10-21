using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
namespace VibeDxf;
public partial class MainWindow : Window
{
    private bool loading;
    public MainWindow()
    {
        InitializeComponent();
        Viewer.SelectionChanged += () =>
        {
            var selected = Viewer.Selected;
            if (selected.Count == 2)
            {
                var m = Viewer.Measurement!.Value;
                var approximate = selected.Any(t => t.Entity.Kind == "SPLINE" || t.Entity.IsApproximate) ? "≈ " : "";
                Measurement.Text = $"{selected[0].Describe(Viewer.FirstStrategy)} → {selected[1].Describe(Viewer.SecondStrategy)} | {approximate}Distance: {m.Distance:0.######}   ΔX: {m.B.X - m.A.X:+0.######;-0.######;0}   ΔY: {m.B.Y - m.A.Y:+0.######;-0.######;0}   [{Viewer.Document?.Units}]";
            }
            else Measurement.Text = selected.Count == 1
                ? $"Selected {selected[0].Describe(Viewer.FirstStrategy)} • layer {selected[0].Entity.Layer} • handle {selected[0].Entity.Handle}. Select another element to measure."
                : "Select two elements to measure their distance.";
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ChooseFile(); }
            if (e.Key == Key.F) Viewer.Fit();
            if (e.Key == Key.Escape) Viewer.ClearSelection();
        };
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await OpenFile(args[1]);
        };
    }
    private void StrategyChanged(object sender, RoutedEventArgs e)
    {
        // Unchecked can fire while InitializeComponent is still building the controls.
        if (Viewer == null || FirstStrategyToggle == null || SecondStrategyToggle == null) return;
        Viewer.FirstStrategy = FirstStrategyToggle.IsChecked == true ? MeasurementStrategy.Center : MeasurementStrategy.Closest;
        Viewer.SecondStrategy = SecondStrategyToggle.IsChecked == true ? MeasurementStrategy.Center : MeasurementStrategy.Closest;
    }
    private async void OpenClicked(object sender, RoutedEventArgs e) => await ChooseFile();
    private async Task ChooseFile()
    {
        var dialog = new OpenFileDialog { Filter = "DXF drawings (*.dxf)|*.dxf", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenFile(dialog.FileName);
    }
    private async Task OpenFile(string path)
    {
        if (loading) return;
        loading = true;
        Status.Text = $"Loading {Path.GetFileName(path)}…";
        try
        {
            var doc = await Task.Run(() => DxfReader.Load(path));
            Viewer.SetDocument(doc);
            Title = $"VibeDxf — {Path.GetFileName(path)}";
            Status.Text = $"{doc.Entities.Count:N0} elements • {doc.Units} • Wheel: zoom • Middle/right drag: pan • Triangle: midpoint • Shift+click: whole element • F: fit • Esc: clear";
            if(doc.Entities.Any(e=>e.Text!=null)) Status.Text += " • Text font: Arial substitute";
            if (doc.UnsupportedWarning is { } warning)
            {
                Status.Text += doc.Skipped.Count>0 ? " • WARNING: incomplete drawing • Skipped: " + string.Join(", ", doc.Skipped.Select(p => $"{p.Key} ({p.Value})")) : " • WARNING: some text formatting is approximated";
                MessageBox.Show(this, warning, "Unsupported DXF elements", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Could not open drawing.";
            MessageBox.Show(this, ex.Message, "Open DXF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { loading = false; }
    }
    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 &&
            Path.GetExtension(paths[0]).Equals(".dxf", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void FileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
            Path.GetExtension(paths[0]).Equals(".dxf", StringComparison.OrdinalIgnoreCase)) await OpenFile(paths[0]);
    }
}
