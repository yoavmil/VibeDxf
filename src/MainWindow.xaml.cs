using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VibeDxf.Wpf;
namespace VibeDxf;
public partial class MainWindow : Window
{
    private bool loading;
    private readonly CancellationTokenSource lifetime = new();
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { lifetime.Cancel(); Viewer.Dispose(); };
        Viewer.RenderingFailed += (_, e) => Status.Text = "Drawing renderer failed: " + e.Exception.Message;
        Viewer.SelectionChanged += (_, _) =>
        {
            var selected = Viewer.Selected;
            if (selected.Count == 2)
            {
                var m = Viewer.Measurement!.Pair!.Value;
                var approximate = selected.Any(t => t.Entity.Kind == "SPLINE" || t.Entity.IsApproximate) ? "≈ " : "";
                Measurement.Text = $"{selected[0].Describe(Viewer.FirstMeasurementStrategy)} → {selected[1].Describe(Viewer.SecondMeasurementStrategy)} | {approximate}Distance: {m.Distance:0.######}   ΔX: {m.B.X - m.A.X:+0.######;-0.######;0}   ΔY: {m.B.Y - m.A.Y:+0.######;-0.######;0}   [{Viewer.Document?.Units}]";
            }
            else Measurement.Text = selected.Count == 1
                ? $"Selected {selected[0].Describe(Viewer.FirstMeasurementStrategy)} • layer {selected[0].Entity.Layer} ({DxfColors.Hex(selected[0].Entity.Color)}) • handle {selected[0].Entity.Handle}. Select another element to measure."
                : "Select two elements to measure their distance.";
        };
        Layers.Attach(Viewer);
        Viewer.ModelViewChanged += (_,_)=>RefreshModelCameras();
        // Tab 0 is model space; tab i shows layout i−1.
        Viewer.LayoutChanged += (_, _) => LayoutTabs.SelectedIndex = Viewer.ActiveLayout is { } id ? Viewer.Document!.Layouts.ToList().FindIndex(l => l.Id == id) + 1 : 0;
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ChooseFile(); }
            if (e.Key == Key.F && Keyboard.FocusedElement is not System.Windows.Controls.TextBox) Viewer.FitToDrawing();
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { ShowPanel(PanelColumn.Width.Value == 0); e.Handled = true; }
            if (e.Key is Key.PageUp or Key.PageDown && Keyboard.Modifiers == ModifierKeys.Control && Viewer.Document != null)
            {
                var current = Viewer.ActiveLayout is { } id ? Viewer.Document.Layouts.ToList().FindIndex(l => l.Id == id) : -1;
                var next = Math.Clamp(current + (e.Key == Key.PageDown ? 1 : -1), -1, Viewer.Document.Layouts.Count - 1);
                Viewer.ActiveLayout = next < 0 ? null : Viewer.Document.Layouts[next].Id;
                e.Handled = true;
            }
            if (e.Key == Key.Escape) Viewer.ClearSelection();
        };
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await OpenFile(args[1]);
        };
    }
    private void RefreshModelCameras()
    {
        ModelCameraMenu.Items.Clear();
        var xy=new System.Windows.Controls.MenuItem{Header="Original XY / measurement",IsCheckable=true,IsChecked=Viewer.ActiveModelView==null};
        xy.Click+=(_,_)=>Viewer.ActiveModelView=null;ModelCameraMenu.Items.Add(xy);
        foreach(var camera in Viewer.Document?.ModelViews??[])
        {
            var item=new System.Windows.Controls.MenuItem{Header=camera.Name+(camera.Perspective?" (perspective)":camera.IsInteractive?"":" (3D)"),IsCheckable=true,IsChecked=Viewer.ActiveModelView==camera.Id,IsEnabled=camera.IsSupported};
            item.Click+=(_,_)=>{Viewer.ActiveLayout=null;Viewer.ActiveModelView=camera.Id;};ModelCameraMenu.Items.Add(item);
        }
        if(Viewer.ActiveLayout==null&&Viewer.ActiveModelView is { } id&&Viewer.Document?.ModelViews[id.Value].IsInteractive==false)
            Measurement.Text="Saved 3D camera — display only. Choose Original XY / measurement to measure.";
    }
    private void StrategyChanged(object sender, RoutedEventArgs e)
    {
        // Unchecked can fire while InitializeComponent is still building the controls.
        if (Viewer == null || FirstStrategyToggle == null || SecondStrategyToggle == null) return;
        Viewer.FirstMeasurementStrategy = FirstStrategyToggle.IsChecked == true ? MeasurementStrategy.Center : MeasurementStrategy.Closest;
        Viewer.SecondMeasurementStrategy = SecondStrategyToggle.IsChecked == true ? MeasurementStrategy.Center : MeasurementStrategy.Closest;
    }
    private double panelWidth = 270;
    private void ShowPanel(bool show)
    {
        if (!show && PanelColumn.Width.Value > 0) panelWidth = PanelColumn.Width.Value;
        PanelColumn.Width = new GridLength(show ? panelWidth : 0);
        PanelSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PanelMenu.IsChecked = show;
    }
    private void PanelMenuClicked(object sender, RoutedEventArgs e) => ShowPanel(PanelMenu.IsChecked);
    private void LayoutTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Viewer == null || LayoutTabs.SelectedIndex < 0) return;
        Viewer.ActiveLayout = LayoutTabs.SelectedIndex == 0 ? null : Viewer.Document!.Layouts[LayoutTabs.SelectedIndex - 1].Id;
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
            var doc = (await DxfDocument.LoadAsync(path, lifetime.Token)).Document;
            lifetime.Token.ThrowIfCancellationRequested();
            // Fill the tabs first: SetDocument raises LayoutChanged, which selects the matching tab.
            LayoutTabs.ItemsSource = new[] { "Model" }.Concat(doc.Layouts.Select(l => l.Name)).ToList();
            LayoutTabs.Visibility = doc.Layouts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            await Viewer.SetDocumentAsync(doc, lifetime.Token);
            // Open the panel when the file's saved layer state hides content, so the drawing doesn't look broken.
            if (Layers.HasHiddenContent) ShowPanel(true);
            Title = $"VibeDxf — {Path.GetFileName(path)}";
            var layouts = doc.Layouts.Count > 0 ? $" • {doc.Layouts.Count} layout{(doc.Layouts.Count == 1 ? "" : "s")} (Ctrl+PgUp/PgDn)" : "";
            Status.Text = $"{doc.Entities.Count:N0} elements{layouts} • {doc.Units} • Wheel: zoom • Middle/right drag: pan • Triangle: midpoint • Shift+click: whole element • F: fit • Esc: clear";
            if(doc.Entities.Concat(doc.Layouts.SelectMany(l => l.Entities)).Any(e=>e.Text!=null)) Status.Text += " • Text font: Arial substitute";
            if (doc.UnsupportedWarning is { } warning)
            {
                Status.Text += doc.Skipped.Count>0 ? " • WARNING: incomplete drawing • Skipped: " + string.Join(", ", doc.Skipped.Select(p => $"{p.Key} ({p.Value})")) : " • WARNING: some text formatting is approximated";
                MessageBox.Show(this, warning, "Unsupported DXF elements", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
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
