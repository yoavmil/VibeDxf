using System.Globalization;
using System.IO;
using System.Windows;
namespace VibeDxf;
// A paper-space window onto model space; ModelToPaper follows ezdxf (top views only).
public sealed record DxfViewportWindow(string Handle, Rect Paper, System.Windows.Media.Matrix ModelToPaper)
{
    /// <summary>Indices into <see cref="DxfDocument.Layers"/> frozen in this viewport only (group 331).</summary>
    public int[] FrozenLayers { get; init; } = [];
}
/// <summary>A LAYER table entry. On/frozen describe the state saved in the file, not the viewer's current state.</summary>
public sealed record DxfLayer(string Name, uint Color, bool OnInFile, bool FrozenInFile, bool Plot, string Linetype)
{
    public bool VisibleInFile => OnInFile && !FrozenInFile;
}
/// <summary>One paper layout (sheet tab): its paper-space entities and the viewports that show model space.</summary>
public sealed class DxfLayout(string name)
{
    public string Name { get; } = name;
    public List<DxfEntity> Entities { get; } = [];
    public List<DxfViewportWindow> Viewports { get; } = [];
    public Rect Bounds => Viewports.Aggregate(Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; }),
        (r, v) => { r.Union(v.Paper); return r; });
    // The first VIEWPORT of every layout is the sheet's own view, not a window.
    internal bool SheetViewSeen { get; set; }
}
public sealed class DxfDocument
{
    /// <summary>Model space. Paper-space records belong to <see cref="Layouts"/>.</summary>
    public List<DxfEntity> Entities { get; } = [];
    /// <summary>Paper layouts in tab order.</summary>
    public List<DxfLayout> Layouts { get; } = [];
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
    public Rect Bounds => Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; });
}
// Temporary Stage 2 adapter. Stage 3 removes this WPF document projection.
public static class DxfReader
{
    public static DxfDocument Load(string path) => CoreDocumentAdapter.Load(path);
}
