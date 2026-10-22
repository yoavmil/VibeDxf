using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VibeDxf.Wpf;
namespace VibeDxf;

/// <summary>
/// Layer and annotation visibility for a <see cref="DxfView"/>. The viewer owns the state; this panel mirrors it
/// and forwards the user's changes, so toggles only rewrite the renderer's visibility table.
/// </summary>
public partial class LayerPanel : UserControl
{
    public abstract class Row : INotifyPropertyChanged
    {
        private bool visible;
        private int count;
        internal bool Syncing;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
        public bool Visible
        {
            get => visible;
            set { if (visible == value) return; visible = value; Changed(); if (!Syncing) Apply(value); }
        }
        public int Count { get => count; set { count = value; Changed(); Changed(nameof(CountText)); Changed(nameof(HasItems)); } }
        public string CountText => $"  {count:N0}";
        public bool HasItems => count > 0;
        protected abstract void Apply(bool value);
    }
    public sealed class LayerRow(LayerPanel panel, int index, DxfLayer layer) : Row
    {
        public int Index { get; } = index;
        public string Name { get; } = layer.Name;
        public Brush Swatch { get; } = Frozen(DxfDisplayColors.ForDarkBackground(layer.Color));
        public string Badges { get; } = string.Join(" · ", new[]
            { layer.OnInFile ? null : "off in file", layer.FrozenInFile ? "frozen" : null, layer.Plot ? null : "no plot" }.OfType<string>());
        public Visibility BadgeVisibility => Badges.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        protected override void Apply(bool value) => panel.viewer?.SetLayerVisible(Index, value);
        private static Brush Frozen(uint color)
        {
            var (r, g, b) = DxfColors.Unpack(color);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
        }
    }
    public sealed class CategoryRow(LayerPanel panel, EntityCategory category, string label) : Row
    {
        public EntityCategory Category { get; } = category;
        public string Label { get; } = label;
        protected override void Apply(bool value) => panel.viewer?.SetCategoryVisible(Category, value);
    }

    private DxfView? viewer;
    private DxfDocument? document;
    private List<LayerRow> layers = [];
    private readonly List<CategoryRow> categories;

    public LayerPanel()
    {
        InitializeComponent();
        categories =
        [
            new(this, EntityCategory.Dimension, "Dimensions"), new(this, EntityCategory.Leader, "Leaders"),
            new(this, EntityCategory.Multileader, "Multileaders"), new(this, EntityCategory.Text, "Text"),
            new(this, EntityCategory.Hatch, "Hatches"), new(this, EntityCategory.ViewportFrame, "Viewport frames"),
        ];
        Categories.ItemsSource = categories;
    }

    /// <summary>True when hidden layers hold drawing content, e.g. layers saved off in the file.</summary>
    public bool HasHiddenContent { get; private set; }

    public void Attach(DxfView target)
    {
        viewer = target;
        viewer.VisibilityChanged += (_, _) => Sync();
        viewer.LayoutChanged += (_, _) => Sync();
        viewer.SelectionChanged += (_, _) =>
        {
            // Point at the layer of the most recently selected element.
            if (viewer.Selected.LastOrDefault()?.Entity.Layer is not { } name) return;
            var row = layers.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            LayerList.SelectedItem = row; LayerList.ScrollIntoView(row);
        };
    }

    private void Sync()
    {
        if (viewer?.Document is not { } doc) return;
        if (!ReferenceEquals(doc, document))
        {
            document = doc;
            layers = [.. doc.Layers.Select((l, i) => new LayerRow(this, i, l))];
            LayerList.ItemsSource = layers;
            ApplyFilter();
            MonochromeBox.IsChecked = viewer.Monochrome;
        }
        // Counts describe the current tab: model space or the active sheet's paper space.
        var active = viewer.ActiveLayout is { } id ? doc.Layouts.First(l => l.Id == id).Entities : doc.Entities;
        var perLayer = active.GroupBy(e => e.LayerId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var row in layers)
        {
            row.Syncing = true; row.Visible = viewer.IsLayerVisible(row.Index); row.Count = perLayer.GetValueOrDefault(row.Index); row.Syncing = false;
        }
        foreach (var row in categories)
        {
            row.Syncing = true; row.Visible = viewer.IsCategoryVisible(row.Category); row.Count = active.Count(e => e.Category == row.Category); row.Syncing = false;
        }
        int visible = layers.Count(r => r.Visible);
        Header.Text = $"Layers · {visible} of {layers.Count} visible";
        // Content anywhere (model or any sheet) on hidden layers.
        var used = doc.Entities.Concat(doc.Layouts.SelectMany(l => l.Entities)).Select(e => e.LayerId).ToHashSet();
        var hidden = layers.Where(r => !r.Visible && used.Contains(r.Index)).ToList();
        HasHiddenContent = hidden.Count > 0;
        bool fileState = layers.Where(r => used.Contains(r.Index)).All(r => r.Visible == doc.Layers[r.Index].VisibleInFile);
        BannerText.Text = $"{hidden.Count} of {layers.Count} layers with content are hidden" + (fileState ? " (saved off or frozen in the file)." : ".");
        Banner.Visibility = HasHiddenContent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        var text = Search.Text.Trim();
        CollectionViewSource.GetDefaultView(layers).Filter = text.Length == 0 ? null :
            item => ((LayerRow)item).Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (layers.Count > 0) ApplyFilter(); }
    private void ShowAllClicked(object sender, RoutedEventArgs e) => viewer?.ShowAll();
    private void AllOnClicked(object sender, RoutedEventArgs e) => SetAll(true);
    private void AllOffClicked(object sender, RoutedEventArgs e) => SetAll(false);
    private void SetAll(bool visible)
    {
        if (viewer == null) return;
        // Respect the search filter: All on/off applies to the listed layers.
        viewer.SetLayersVisible(CollectionViewSource.GetDefaultView(layers).Cast<LayerRow>().Select(r => r.Index), visible);
    }
    private void FileStateClicked(object sender, RoutedEventArgs e) => viewer?.ApplySavedLayerState();
    private void LayerDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (LayerList.SelectedItem is LayerRow row && e.OriginalSource is not CheckBox) viewer?.IsolateLayer(row.Index);
    }
    private void MonochromeChanged(object sender, RoutedEventArgs e)
    {
        if (viewer != null) viewer.Monochrome = MonochromeBox.IsChecked == true;
    }
}
