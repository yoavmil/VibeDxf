using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VibeDxf.Wpf;

namespace PackageViewer;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Exception> renderingErrors = [];
    private int measurementEvents;
    private bool updating;
    public MainWindow(bool smoke = false)
    {
        InitializeComponent();
        if (smoke) { Left = -20000; Top = -20000; ShowInTaskbar = false; ShowActivated = false; }
        Viewer.MeasurementChanged += (_, args) => { measurementEvents++; ShowMeasurement(args.Measurement); };
        Viewer.LayoutChanged += (_, _) => RefreshEntities();
        Viewer.RenderingFailed += (_, args) => { renderingErrors.Add(args.Exception); Status.Text = args.Exception.Message; };
        Closed += (_, _) => { lifetime.Cancel(); Viewer.Dispose(); };
    }
    public async Task LoadDrawingAsync(string path)
    {
        var loaded = await DxfDocument.LoadAsync(path, lifetime.Token);
        await Viewer.SetDocumentAsync(loaded.Document, lifetime.Token);
        updating = true;
        Layouts.ItemsSource = new[] { new LayoutRow("Model", null) }.Concat(loaded.Document.Layouts.Select(l => new LayoutRow(l.Name, l.Id))).ToArray();
        Layouts.SelectedIndex = Viewer.ActiveLayout == null ? 0 : loaded.Document.Layouts.ToList().FindIndex(l => l.Id == Viewer.ActiveLayout) + 1;
        LayerList.ItemsSource = loaded.Document.Layers.Select(l => new LayerRow(l.Name, Viewer.IsLayerVisible(l.Id), enabled => Viewer.SetLayerVisible(l.Id, enabled))).ToArray();
        updating = false;
        RefreshEntities();
        Status.Text = $"{loaded.Document.Entities.Count:N0} model entities · {loaded.Document.Layouts.Count} sheets · {loaded.Diagnostics.Count} load notes";
    }
    private void RefreshEntities()
    {
        if (Viewer.Document is not { } document) { Entities.ItemsSource = null; return; }
        var entities = Viewer.ActiveLayout is { } id ? document.Layouts.Single(l => l.Id == id).Entities : document.Entities;
        Entities.ItemsSource = entities.Select(e => new EntityRow(e)).ToArray();
    }
    private void ShowMeasurement(MeasurementResult? result) => Status.Text = result is { Status: MeasurementStatus.Success }
        ? $"{(result.IsApproximate ? "≈ " : "")}Distance {result.Distance:0.######} · ΔX {result.DeltaX:0.######} · ΔY {result.DeltaY:0.######}"
        : "Select two elements to measure.";
    private async void OpenClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "DXF (*.dxf)|*.dxf", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await LoadDrawingAsync(dialog.FileName); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open DXF", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void FitClicked(object sender, RoutedEventArgs args) => Viewer.FitToDrawing();
    private void ShowAllClicked(object sender, RoutedEventArgs args)
    {
        Viewer.ShowAll();
        foreach (var row in LayerList.Items.OfType<LayerRow>()) row.Enabled = true;
    }
    private void MonochromeChanged(object sender, RoutedEventArgs args) { if (Viewer != null) Viewer.Monochrome = Monochrome.IsChecked == true; }
    private void LayoutChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!updating && Viewer != null && Layouts.SelectedItem is LayoutRow row) Viewer.ActiveLayout = row.Id;
    }
    private void MeasureRowsClicked(object sender, RoutedEventArgs args)
    {
        var rows = Entities.SelectedItems.OfType<EntityRow>().ToArray();
        if (Viewer.Document == null || rows.Length != 2) { Status.Text = "Select two rows (Ctrl+click)."; return; }
        // Document queries also allow annotation graphics and hidden layers.
        ShowMeasurement(Viewer.Document.Measure(new(rows[0].Entity.Id), new(rows[1].Entity.Id)));
    }
    public async Task<object> VerifyPackageAsync()
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var document = Viewer.Document ?? throw new InvalidOperationException("No document was loaded.");
        var first = document.Entities.Single(e => e.Handle == "A");
        var second = document.Entities.Single(e => e.Handle == "B");
        var text = document.Entities.Single(e => e.Handle == "T");
        Require(document.Entities.Count == 3 && text.Curves.Count > 0, "Traversal/text preparation failed.");
        Viewer.ShowAll(); Viewer.Select(new(first.Id)); Viewer.Select(new(second.Id));
        Require(Viewer.Measurement is { Status: MeasurementStatus.Success } measurement && Math.Abs(measurement.Distance!.Value - 3) < 1e-9, "Selection/measurement events failed.");
        Require(measurementEvents >= 2, "No measurement notifications were received.");
        Viewer.SetLayerVisible(second.LayerId, false);
        Require(!Viewer.IsLayerVisible(second.LayerId) && Viewer.Selected.Count == 1, "Layer visibility did not clear the hidden selection.");
        Require(document.Measure(new(first.Id), new(second.Id)).Distance == 3, "Hidden source geometry stopped being queryable.");
        Viewer.SetCategoryVisible(EntityCategory.Text, false);
        Require(!Viewer.IsCategoryVisible(EntityCategory.Text), "Category visibility failed.");
        var textPair = document.Measure(new(text.Id), new(first.Id));
        Require(textPair.Status == MeasurementStatus.Success && textPair.IsApproximate, "Prepared text measurement failed.");
        Viewer.ShowAll(); Viewer.FitToDrawing();
        var point = Viewer.ScreenToWorld(Viewer.WorldToScreen(new(3, 4)));
        Require(Math.Abs(point.X - 3) < 1e-8 && Math.Abs(point.Y - 4) < 1e-8, "Camera conversion failed.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(Viewer.ActualWidth > 0 && renderingErrors.Count == 0, "The packaged native viewer failed to initialize/render.");
        Viewer.ClearDocument(); Require(Viewer.Document == null && Viewer.Selected.Count == 0, "Clear failed.");
        await Viewer.SetDocumentAsync(document);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(renderingErrors.Count == 0, "Renderer failed on reassignment.");
        return new { Passed = true, PackageAssembly = typeof(DxfView).Assembly.Location, ModelEntities = document.Entities.Count,
            Distance = 3, TextApproximate = textPair.IsApproximate, MeasurementEvents = measurementEvents, RenderingErrors = renderingErrors.Count };
    }
    public sealed record LayoutRow(string Name, DxfLayoutId? Id);
    public sealed record EntityRow(DxfEntity Entity)
    {
        public string Description => $"{Entity.Kind} · {Entity.Layer} · {Entity.Handle}";
    }
    public sealed class LayerRow(string name, bool enabled, Action<bool> changed) : INotifyPropertyChanged
    {
        private bool value = enabled;
        public string Name { get; } = name;
        public bool Enabled { get => value; set { if (this.value == value) return; this.value = value; changed(value); PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
