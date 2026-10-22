using System.Windows;
namespace VibeDxf.Wpf;
/// <summary>One paper layout (sheet tab): its paper-space entities and the viewports that show model space.</summary>
internal sealed class LayoutBuilder(string name)
{
    public string Name { get; } = name;
    public List<DxfEntity> Entities { get; } = [];
    public List<DxfViewportWindow> Viewports { get; } = [];
    public Rect Bounds => Viewports.Aggregate(Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; }),
        (r, v) => { r.Union(v.Paper); return r; });
    // The first VIEWPORT of every layout is the sheet's own view, not a window.
    internal bool SheetViewSeen { get; set; }
}
internal sealed class DrawingBuilder
{
    /// <summary>Model space. Paper-space records belong to <see cref="Layouts"/>.</summary>
    public List<DxfEntity> Entities { get; } = [];
    /// <summary>Paper layouts in tab order.</summary>
    public List<LayoutBuilder> Layouts { get; } = [];
    /// <summary>LAYER table in file order, plus any layer that entities use without a table entry.</summary>
    public List<DxfLayer> Layers { get; } = [];
    /// <summary>Distinct (layer index, category) pairs; <see cref="DxfEntity.Group"/> indexes this list.</summary>
    public List<(int Layer, EntityCategory Category)> Groups { get; } = [];
    public Dictionary<string, int> Skipped { get; } = [];
    public string Units { get; set; } = "drawing units (unspecified)";
    public Dictionary<string,int> RenderingWarnings { get; } = [];
    public string? UnsupportedWarning => Skipped.Count == 0 && RenderingWarnings.Count == 0 ? null :
        (Skipped.Count>0 ? $"This drawing is only partially displayed. {Skipped.Values.Sum():N0} unsupported records were skipped.\n\n" : "Some drawing features are displayed approximately.\n\n") +
        string.Join("\n", Skipped.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {p.Value:N0}")) +
        (RenderingWarnings.Count==0?"":"\n\nRendering notes:\n"+string.Join("\n",RenderingWarnings.OrderBy(p=>p.Key).Select(p=>$"{p.Key}: {p.Value:N0}"))) +
        "\n\nSkipped block inserts may contain additional geometry that is not shown. Measurements apply only to displayed geometry.";
    internal DxfDocument? Published;
    internal DxfDocument Freeze() => Published ??= new DxfDocument(this);
    public static implicit operator DxfDocument(DrawingBuilder builder) => builder.Freeze();
    public Rect Bounds => Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; });
}
