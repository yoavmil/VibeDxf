using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
namespace VibeDxf.Wpf;

/// <summary>
/// Streaming SVG reader (docs/SVG-PLAN.md). The document is walked once with a forward-only XmlReader; only small
/// subtrees (defs, symbols, gradients, text, CSS and elements referenced by &lt;use&gt;) are materialized.
/// SVG y-down is flipped to the viewer's y-up world. Coordinates stay in SVG user units unless the root declares
/// absolute width/height units, which then become the document units.
/// </summary>
internal sealed class SvgReader
{
    private const string InkscapeNs = "http://www.inkscape.org/namespaces/inkscape";
    private const string XlinkNs = "http://www.w3.org/1999/xlink";
    private const int MaxUseDepth = 32, MaxUseInstances = 1_000_000;

    /// <summary>Inherited presentation state. Opacity is the accumulated group opacity (approximation of group compositing).</summary>
    private sealed record Style(string? Fill, string? Stroke, bool EvenOdd, double FillOpacity, double StrokeOpacity, double Opacity,
        string? Color, double FontSize, string FontFamily, bool Bold, bool Italic, double Anchor, bool Visible);
    private sealed record Frame(Style Style, Matrix Ctm, int Layer, int Depth, string Path);
    private sealed record Rule(string? Tag, string? Class, string? Id, int Specificity, int Order, Dictionary<string, string> Declarations);

    private static readonly HashSet<string> StyleProperties = ["fill", "stroke", "fill-rule", "fill-opacity", "stroke-opacity", "opacity",
        "color", "font-size", "font-family", "font-weight", "font-style", "text-anchor", "visibility", "display", "font",
        "clip-path", "mask", "filter", "marker-start", "marker-mid", "marker-end", "stroke-dasharray"];
    private static readonly HashSet<string> Captured = ["defs", "symbol", "linearGradient", "radialGradient", "pattern", "clipPath",
        "mask", "marker", "filter", "style", "title", "desc", "metadata", "namedview"];

    private readonly DrawingBuilder doc = new() { Format = DrawingFormat.Svg, Units = "px (SVG user units)" };
    private readonly Dictionary<string, int> layerIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint?> colorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rule> rules = [];
    private readonly HashSet<string> referenced;
    private readonly Dictionary<string, string> decl = new(StringComparer.Ordinal);
    private readonly Stack<string> useChain = new();
    private double toleranceWorld = SplineGeometry.Tolerance, viewWidth = 100, viewHeight = 100, maxCubicLength = double.PositiveInfinity;
    private long ordinal; private int useInstances;
    private bool rootSeen;

    private SvgReader(HashSet<string> referenced)
    {
        this.referenced = referenced;
        doc.Layers.Add(new DxfLayer("0", DxfColors.White, true, false, true, "Continuous"));
        layerIndex["0"] = 0;
    }

    public static DxfDocument Load(string path)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        // <use> may reference any element, including later ones; a cheap prescan finds the ids worth materializing.
        if (MayContainUse(path)) { using var scan = Open(path); refs = ScanUseReferences(scan); }
        using var source = Open(path);
        return new SvgReader(refs).Read(source);
    }

    /// <summary>Loads from a stream in one pass; &lt;use&gt; then resolves only definitions that precede it.</summary>
    internal static DxfDocument Load(Stream source)
    {
        var buffered = new BufferedStream(source);
        Stream input = IsGzip(buffered) ? new GZipStream(buffered, CompressionMode.Decompress, true) : buffered;
        return new SvgReader([]).Read(input);
    }

    private static Stream Open(string path)
    {
        var file = new BufferedStream(File.OpenRead(path), 1 << 16);
        return IsGzip(file) ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    private static bool IsGzip(BufferedStream stream)
    {
        if (!stream.CanSeek) return false;
        long position = stream.Position; int a = stream.ReadByte(), b = stream.ReadByte(); stream.Position = position;
        return a == 0x1F && b == 0x8B;
    }

    private static bool MayContainUse(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[2];
        if (file.Read(head) == 2 && head[0] == 0x1F && head[1] == 0x8B) return true;
        file.Position = 0;
        var buffer = new byte[1 << 20]; int carry = 0, read;
        while ((read = file.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            LoadingContext.Check();
            int length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf("<use"u8) >= 0) return true;
            carry = Math.Min(3, length); Array.Copy(buffer, length - carry, buffer, 0, carry);
        }
        return false;
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true,
        IgnoreProcessingInstructions = true, CloseInput = false
    };

    private static HashSet<string> ScanUseReferences(Stream source)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var xml = XmlReader.Create(source, Settings());
        long n = 0;
        while (xml.Read())
        {
            if ((++n & 4095) == 0) LoadingContext.Check();
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "use" &&
                (xml.GetAttribute("href", XlinkNs) ?? xml.GetAttribute("href")) is { Length: > 1 } href && href[0] == '#')
                result.Add(href[1..]);
        }
        return result;
    }

    // ---------------------------------------------------------------- main pass
    private DxfDocument Read(Stream source)
    {
        using var xml = XmlReader.Create(source, Settings());
        var frames = new Stack<Frame>();
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        bool advance = true;
        while (advance ? xml.Read() : !xml.EOF)
        {
            advance = true;
            if ((++ordinal & 4095) == 0) LoadingContext.Check();
            if (xml.NodeType == XmlNodeType.EndElement) { if (frames.Count > 0) frames.Pop(); continue; }
            if (xml.NodeType != XmlNodeType.Element) continue;
            var name = xml.LocalName;
            if (!rootSeen && name != "svg") throw new InvalidDataException("The file is not an SVG document.");
            var parent = frames.Count > 0 ? frames.Peek() : null;
            var id = xml.GetAttribute("id");
            if (Captured.Contains(name) || name == "text" || (id != null && referenced.Contains(id)))
            {
                var element = (XElement)XNode.ReadFrom(xml); advance = false;
                Materialized(element, parent);
                continue;
            }
            ReadAttributes(xml, attrs);
            bool empty = xml.IsEmptyElement;
            var frame = Visit(name, attrs, parent);
            if (frame != null && !empty) frames.Push(frame);
            else { xml.Skip(); advance = false; }
        }
        if (!rootSeen) throw new InvalidDataException("The file is not an SVG document.");
        return Finish();
    }

    private static void ReadAttributes(XmlReader xml, Dictionary<string, string> attrs)
    {
        attrs.Clear();
        if (!xml.MoveToFirstAttribute()) return;
        do
        {
            var key = xml.NamespaceURI switch { InkscapeNs => "inkscape:" + xml.LocalName, XlinkNs => xml.LocalName, "" => xml.LocalName, _ => null };
            if (key != null) attrs[key] = xml.Value;
        } while (xml.MoveToNextAttribute());
        xml.MoveToElement();
    }

    private static Dictionary<string, string> Attributes(XElement e)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in e.Attributes())
        {
            var ns = a.Name.NamespaceName;
            var key = ns switch { InkscapeNs => "inkscape:" + a.Name.LocalName, XlinkNs => a.Name.LocalName, "" => a.Name.LocalName, _ => null };
            if (key != null && !a.IsNamespaceDeclaration) attrs[key] = a.Value;
        }
        return attrs;
    }

    private void Register(XElement element)
    {
        foreach (var e in element.DescendantsAndSelf())
            if (e.Attribute("id")?.Value is { Length: > 0 } id) byId.TryAdd(id, e);
    }

    // Subtrees read as XElement: definitions are registered, CSS parsed, text and referenced elements rendered.
    private void Materialized(XElement element, Frame? parent)
    {
        Register(element);
        var name = element.Name.LocalName;
        if (name == "style") { ParseCss(element.Value); return; }
        if (parent == null || Captured.Contains(name))
        {
            if (name is "clipPath" or "mask" or "filter" or "pattern" or "marker") return; // referenced from elsewhere; warned there
            return;
        }
        Walk(element, parent);
    }

    private void Walk(XElement element, Frame parent)
    {
        if ((++ordinal & 4095) == 0) LoadingContext.Check();
        var name = element.Name.LocalName;
        if (Captured.Contains(name)) { Register(element); if (name == "style") ParseCss(element.Value); return; }
        if (name == "text") { Text(element, parent); return; }
        var frame = Visit(name, Attributes(element), parent);
        if (frame == null) return;
        foreach (var child in element.Elements()) Walk(child, frame);
    }

    // ---------------------------------------------------------------- elements
    /// <summary>Handles one element from its attributes. Returns the frame for children, or null for leaves/skipped subtrees.</summary>
    private Frame? Visit(string name, Dictionary<string, string> attrs, Frame? parent)
    {
        var handle = attrs.GetValueOrDefault("id") is { Length: > 0 } id ? id : "e" + ordinal.ToString(CultureInfo.InvariantCulture);
        if (parent == null)
        {
            if (name != "svg") return null;
            rootSeen = true;
            return Root(attrs);
        }
        var style = Compute(parent.Style, name, attrs);
        if (decl.GetValueOrDefault("display") == "none" && !IsLayer(attrs)) { Note("hidden element (display:none)"); return null; }
        var local = Transform(attrs.GetValueOrDefault("transform"));
        var ctm = local * parent.Ctm;
        var path = parent.Path;
        switch (name)
        {
            case "g" or "a" or "switch":
            {
                if (name == "switch") Note("switch (all children shown)");
                int layer = parent.Layer;
                if (IsLayer(attrs) || (parent.Depth == 1 && name == "g" && attrs.ContainsKey("id")))
                    layer = Layer(attrs.GetValueOrDefault("inkscape:label") ?? attrs.GetValueOrDefault("id") ?? "Layer " + doc.Layers.Count, decl.GetValueOrDefault("display") != "none");
                Warn(attrs);
                return new Frame(style, ctm, layer, parent.Depth + 1, path);
            }
            case "svg":
            {
                // Nested viewport: x/y offset and viewBox-to-size scale; clipping to its bounds is not applied.
                var m = Matrix.Identity;
                if (ViewBox(attrs.GetValueOrDefault("viewBox")) is { } box && Length(attrs.GetValueOrDefault("width"), box.Width) is > 0 and var w)
                { m.Translate(-box.X, -box.Y); m.Scale(w / box.Width, w / box.Width); }
                m.Translate(Length(attrs.GetValueOrDefault("x"), viewWidth), Length(attrs.GetValueOrDefault("y"), viewHeight));
                return new Frame(style, m * parent.Ctm, parent.Layer, parent.Depth + 1, path);
            }
            case "use":
            {
                Use(attrs, style, ctm, parent, handle);
                return null;
            }
            case "line" or "rect" or "circle" or "ellipse" or "polyline" or "polygon" or "path":
                Warn(attrs);
                Shape(name, attrs, style, ctm, parent.Layer, handle);
                return null;
            default:
                Skip(name + " (not supported)");
                return null;
        }
    }

    private static bool IsLayer(Dictionary<string, string> attrs) => attrs.GetValueOrDefault("inkscape:groupmode") == "layer";

    private int Layer(string name, bool visible)
    {
        if (layerIndex.TryGetValue(name, out var index)) return index;
        layerIndex[name] = doc.Layers.Count;
        doc.Layers.Add(new DxfLayer(name, DxfColors.White, visible, false, true, "Continuous"));
        return doc.Layers.Count - 1;
    }

    private Frame Root(Dictionary<string, string> attrs)
    {
        var box = ViewBox(attrs.GetValueOrDefault("viewBox"));
        var (w, unit) = Physical(attrs.GetValueOrDefault("width"));
        var m = Matrix.Identity; double scale = 1;
        if (unit != null)
        {
            // Declared physical size: coordinates become that unit. Without a viewBox one user unit is one CSS px.
            scale = box is { } b ? w / b.Width : UnitsPerPixel(unit);
            if (box is { } v) m.Translate(-v.X, -v.Y);
            m.Scale(scale, scale);
            doc.Units = unit;
        }
        if (box is { } view) { viewWidth = view.Width; viewHeight = view.Height; }
        else { viewWidth = Length(attrs.GetValueOrDefault("width"), 100) is > 0 and var pw ? pw : 100; viewHeight = Length(attrs.GetValueOrDefault("height"), 100) is > 0 and var ph ? ph : 100; }
        m.Scale(1, -1);
        // Bézier/ellipse chords: a bound relative to the drawing size; 0.0001 absolute is far too fine for large plots.
        toleranceWorld = Math.Max(SplineGeometry.Tolerance, 1e-5 * Math.Max(viewWidth, viewHeight) * scale);
        maxCubicLength = Math.Max(viewWidth, viewHeight) * scale / 256;
        var style = new Style("black", "none", false, 1, 1, 1, "black", 16, "Arial", false, false, 0, true);
        style = Compute(style, "svg", attrs);
        return new Frame(style, m, 0, 1, "");
    }

    private void Use(Dictionary<string, string> attrs, Style style, Matrix ctm, Frame parent, string handle)
    {
        var href = attrs.GetValueOrDefault("href");
        if (href is not { Length: > 1 } || href[0] != '#' || !byId.TryGetValue(href[1..], out var target)) { Skip("use (missing or external reference)"); return; }
        if (useChain.Count >= MaxUseDepth || useChain.Contains(href) || ++useInstances > MaxUseInstances) { Skip("use (cycle, depth or instance limit)"); return; }
        var offset = Matrix.Identity; offset.Translate(Length(attrs.GetValueOrDefault("x"), viewWidth), Length(attrs.GetValueOrDefault("y"), viewHeight));
        var frame = new Frame(style, offset * ctm, parent.Layer, parent.Depth + 1, parent.Path + handle + "/");
        useChain.Push(href);
        try
        {
            if (target.Name.LocalName == "symbol")
            {
                if (target.Attribute("viewBox") != null) Note("symbol viewBox (not applied)");
                foreach (var child in target.Elements()) Walk(child, frame);
            }
            else Walk(target, frame);
        }
        finally { useChain.Pop(); }
    }

    private void Warn(Dictionary<string, string> attrs)
    {
        if (decl.ContainsKey("clip-path") && decl["clip-path"] != "none") Note("clip-path (not applied)");
        if (decl.ContainsKey("mask") && decl["mask"] != "none") Note("mask (not applied)");
        if (decl.ContainsKey("filter") && decl["filter"] != "none") Note("filter (not applied)");
        if (decl.Keys.Any(k => k.StartsWith("marker", StringComparison.Ordinal)) ) Note("markers (not drawn)");
        if (decl.GetValueOrDefault("stroke-dasharray") is { } dash && dash != "none") Note("dashed strokes (drawn solid)");
    }

    // ---------------------------------------------------------------- shapes
    private void Shape(string name, Dictionary<string, string> attrs, Style style, Matrix ctm, int layer, string handle)
    {
        if (!style.Visible) { Note("hidden element (visibility:hidden)"); return; }
        double N(string key, double reference) => Length(attrs.GetValueOrDefault(key), reference);
        double diagonal = Math.Sqrt((viewWidth * viewWidth + viewHeight * viewHeight) / 2);
        // Chords are flattened locally: divide by the largest stretch (max singular value) so the world bound holds.
        double sum = ctm.M11 * ctm.M11 + ctm.M12 * ctm.M12 + ctm.M21 * ctm.M21 + ctm.M22 * ctm.M22, det = ctm.Determinant;
        double stretch = Math.Sqrt((sum + Math.Sqrt(Math.Max(0, sum * sum - 4 * det * det))) / 2);
        double localTolerance = toleranceWorld / Math.Max(stretch, 1e-300);
        var subpaths = new List<List<Curve>>(); bool approximate = false;
        // Display form: exact lines/arcs plus cubics for Béziers and ellipses, so deep zoom never shows measurement chords.
        var exactLocal = new List<Curve>(); var cubicsLocal = new List<CubicSegment>();
        string kind = name.ToUpperInvariant(); bool disc = false;
        switch (name)
        {
            case "line":
                subpaths.Add([Curve.Line(new(N("x1", viewWidth), N("y1", viewHeight)), new(N("x2", viewWidth), N("y2", viewHeight)))]);
                break;
            case "rect":
            {
                double x = N("x", viewWidth), y = N("y", viewHeight), w = N("width", viewWidth), h = N("height", viewHeight);
                if (w <= 0 || h <= 0) { Note("rect (zero size)"); return; }
                double rx = attrs.ContainsKey("rx") ? N("rx", viewWidth) : -1, ry = attrs.ContainsKey("ry") ? N("ry", viewHeight) : -1;
                if (rx < 0) rx = Math.Max(ry, 0); if (ry < 0) ry = rx;
                rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
                var c = new List<Curve>();
                if (rx <= 0 || ry <= 0)
                {
                    Point a = new(x, y), b = new(x + w, y), d = new(x + w, y + h), e = new(x, y + h);
                    c.AddRange([Curve.Line(a, b), Curve.Line(b, d), Curve.Line(d, e), Curve.Line(e, a)]);
                }
                else
                {
                    void Corner(double cx, double cy, double from)
                    {
                        if (rx == ry) { var arc = Curve.Arc(new(cx, cy), rx, from, Math.PI / 2); c.Add(arc); exactLocal.Add(arc); }
                        else
                        {
                            SvgPathData.Ellipse(c, new(cx, cy), rx, ry, 0, from, Math.PI / 2, localTolerance);
                            SvgPathData.EllipseCubics(cubicsLocal, new(cx, cy), rx, ry, 0, from, Math.PI / 2); approximate = true;
                        }
                    }
                    void Edge(Point a, Point b) { if (a != b) { var line = Curve.Line(a, b); c.Add(line); exactLocal.Add(line); } }
                    Edge(new(x + rx, y), new(x + w - rx, y)); Corner(x + w - rx, y + ry, -Math.PI / 2);
                    Edge(new(x + w, y + ry), new(x + w, y + h - ry)); Corner(x + w - rx, y + h - ry, 0);
                    Edge(new(x + w - rx, y + h), new(x + rx, y + h)); Corner(x + rx, y + h - ry, Math.PI / 2);
                    Edge(new(x, y + h - ry), new(x, y + ry)); Corner(x + rx, y + ry, Math.PI);
                }
                subpaths.Add(c);
                break;
            }
            case "circle" or "ellipse":
            {
                var center = new Point(N("cx", viewWidth), N("cy", viewHeight));
                double rx = name == "circle" ? N("r", diagonal) : N("rx", viewWidth), ry = name == "circle" ? rx : N("ry", viewHeight);
                if (name == "ellipse" && !attrs.ContainsKey("ry")) ry = rx; else if (name == "ellipse" && !attrs.ContainsKey("rx")) rx = ry;
                if (rx <= 0 || ry <= 0) { Note(name + " (zero radius)"); return; }
                if (Math.Abs(rx - ry) <= 1e-12 * rx) { subpaths.Add([Curve.Arc(center, rx, 0, 2 * Math.PI)]); disc = true; }
                else
                {
                    var c = new List<Curve>(); SvgPathData.Ellipse(c, center, rx, ry, 0, 0, 2 * Math.PI, localTolerance); subpaths.Add(c); approximate = true;
                    SvgPathData.EllipseCubics(cubicsLocal, center, rx, ry, 0, 0, 2 * Math.PI);
                }
                break;
            }
            case "polyline" or "polygon":
            {
                var values = Numbers(attrs.GetValueOrDefault("points"));
                var points = new List<Point>();
                for (int i = 0; i + 1 < values.Count; i += 2) points.Add(new(values[i], values[i + 1]));
                if (name == "polygon" && points.Count > 2) points.Add(points[0]);
                var c = new List<Curve>();
                for (int i = 0; i + 1 < points.Count; i++) if (points[i] != points[i + 1]) c.Add(Curve.Line(points[i], points[i + 1]));
                subpaths.Add(c);
                break;
            }
            case "path":
            {
                subpaths = SvgPathData.Parse(attrs.GetValueOrDefault("d") ?? "", localTolerance, out approximate, out var error, exactLocal, cubicsLocal);
                if (error != null) Skip("path data (" + error + "; drawn up to the error)");
                break;
            }
        }
        var world = subpaths.Select(s => Place(s, ctm, localTolerance, ref approximate)).Where(s => s.Count > 0).ToList();
        if (world.Count == 0) return;
        List<Curve>? renderCurves = null; List<CubicSegment>? renderCubics = null;
        if (approximate)
        {
            // Shapes without explicit exact parts (polylines, lines, circles) already are exact: reuse their measurement curves.
            if (exactLocal.Count == 0 && cubicsLocal.Count == 0) exactLocal.AddRange(subpaths.SelectMany(s => s));
            renderCubics = [];
            bool ignored = false;
            renderCurves = Place(exactLocal, ctm, localTolerance, ref ignored, renderCubics);
            foreach (var cubic in cubicsLocal) Split(cubic.Map(ctm), renderCubics);
        }
        Emit(kind, world, approximate, style, layer, handle, disc && world is [[{ IsLine: false }]], name != "line", renderCurves, renderCubics);
    }

    // Display pieces stay short relative to the drawing, so the GPU's 1024-segment cap still resolves them under deep zoom.
    private void Split(CubicSegment cubic, List<CubicSegment> output, int depth = 0)
    {
        if (depth >= 12 || cubic.PolygonLength <= maxCubicLength) { output.Add(cubic); return; }
        var (a, b) = cubic.Split(); Split(a, output, depth + 1); Split(b, output, depth + 1);
    }

    /// <summary>Maps local curves into the world. Arcs stay exact under similarity transforms and are flattened otherwise.</summary>
    private static List<Curve> Place(List<Curve> local, Matrix m, double localTolerance, ref bool approximate, List<CubicSegment>? displayCubics = null)
    {
        double a = m.M11, b = m.M12, c = m.M21, d = m.M22, scale = Math.Sqrt(Math.Abs(m.Determinant));
        double eps = 1e-9 * Math.Max(scale, 1e-300);
        bool similar = (Math.Abs(a - d) <= eps && Math.Abs(b + c) <= eps) || (Math.Abs(a + d) <= eps && Math.Abs(b - c) <= eps);
        var result = new List<Curve>(local.Count);
        foreach (var k in local)
        {
            if (k.IsLine) { var s = m.Transform(k.Start); var e = m.Transform(k.End); if (s != e) result.Add(Curve.Line(s, e)); continue; }
            if (similar && scale > 0)
            {
                var center = m.Transform(k.Center);
                var start = m.Transform(k.Center + k.Radius * new Vector(Math.Cos(k.Angle), Math.Sin(k.Angle)));
                double sweep = m.Determinant < 0 ? -k.Sweep : k.Sweep;
                result.Add(Curve.Arc(center, k.Radius * scale, Math.Atan2(start.Y - center.Y, start.X - center.X), sweep));
                continue;
            }
            approximate = true;
            if (displayCubics != null)
            {
                var pieces = new List<CubicSegment>();
                SvgPathData.EllipseCubics(pieces, k.Center, k.Radius, k.Radius, 0, k.Angle, k.Sweep);
                displayCubics.AddRange(pieces.Select(p => p.Map(m)));
                continue;
            }
            var chords = new List<Curve>();
            SvgPathData.Ellipse(chords, k.Center, k.Radius, k.Radius, 0, k.Angle, k.Sweep, localTolerance);
            foreach (var chord in chords) result.Add(Curve.Line(m.Transform(chord.Start), m.Transform(chord.End)));
        }
        return result;
    }

    private void Emit(string kind, List<List<Curve>> world, bool approximate, Style style, int layer, string handle, bool disc, bool fillable,
        List<Curve>? renderCurves = null, List<CubicSegment>? renderCubics = null)
    {
        uint? stroke = Paint(style.Stroke, style.Color, style.StrokeOpacity * style.Opacity);
        uint? fill = fillable ? Paint(style.Fill, style.Color, style.FillOpacity * style.Opacity) : null;
        if (stroke == null && fill == null) { Note("invisible element (no fill or stroke)"); return; }
        var curves = world.SelectMany(s => s).ToList();
        List<IReadOnlyList<Point>> contours = [];
        bool asDisc = disc && fill != null;
        if (fill != null && !asDisc) contours = FillContours(world, style.EvenOdd);
        doc.Entities.Add(new DxfEntity(kind, doc.Layers[layer].Name, handle, curves)
        {
            Color = stroke ?? fill!.Value, FillColor = fill, FillDisc = asDisc, FillContours = contours, IsApproximate = approximate,
            // stroke="none" draws no outline (SVG semantics); an outline in a later pass would cover later shapes' fills.
            RenderCurves = stroke == null ? [] : renderCubics is { Count: > 0 } ? renderCurves : null,
            RenderCubics = stroke != null && renderCubics is { Count: > 0 } ? renderCubics : null,
            InstancePath = useChain.Count == 0 ? null : string.Join("/", useChain.Reverse())
        });
    }

    private List<IReadOnlyList<Point>> FillContours(List<List<Curve>> world, bool evenOdd)
    {
        var polygons = world.Select(Polygon).Where(p => p.Count >= 3).ToList();
        if (polygons.Count == 0) return [];
        if (evenOdd || polygons.Count == 1) return [.. polygons];
        // Nonzero: WPF outlines the union so the even/odd triangulator produces the same coverage.
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
            foreach (var p in polygons) { context.BeginFigure(p[0], true, true); context.PolyLineTo(p.Skip(1).ToList(), true, false); }
        return Polygons(geometry.GetOutlinedPathGeometry(toleranceWorld, ToleranceType.Absolute), toleranceWorld);
    }

    private List<Point> Polygon(List<Curve> curves)
    {
        var points = new List<Point>();
        foreach (var c in curves)
        {
            if (c.IsLine) { points.Add(c.Start); continue; }
            var chords = new List<Curve>();
            SvgPathData.Ellipse(chords, c.Center, c.Radius, c.Radius, 0, c.Angle, c.Sweep, toleranceWorld);
            points.AddRange(chords.Select(k => k.Start));
        }
        return points;
    }

    private static List<IReadOnlyList<Point>> Polygons(Geometry geometry, double tolerance)
    {
        var result = new List<IReadOnlyList<Point>>();
        foreach (var figure in geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute).Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                else if (segment is LineSegment line) points.Add(line.Point);
            if (points.Count >= 3) result.Add(points);
        }
        return result;
    }

    // ---------------------------------------------------------------- text
    private void Text(XElement element, Frame parent)
    {
        var attrs = Attributes(element);
        var style = Compute(parent.Style, "text", attrs);
        if (decl.GetValueOrDefault("display") == "none") return;
        var ctm = Transform(attrs.GetValueOrDefault("transform")) * parent.Ctm;
        var handle = attrs.GetValueOrDefault("id") is { Length: > 0 } id ? id : "e" + ordinal.ToString(CultureInfo.InvariantCulture);
        var pen = new Point(First(attrs, "x", viewWidth) ?? 0, First(attrs, "y", viewHeight) ?? 0);
        pen.Offset(First(attrs, "dx", viewWidth) ?? 0, First(attrs, "dy", viewHeight) ?? 0);
        int run = 0; bool first = true;
        // A text chunk (until the next absolute x/y) is anchored as a whole.
        var pending = new List<(FormattedText Text, Point Origin, Style Style, string Value)>(); double chunkWidth = 0;
        void Flush()
        {
            double shift = pending.Count == 0 ? 0 : pending[0].Style.Anchor * chunkWidth;
            foreach (var (formatted, origin, s, value) in pending) Emit(formatted, new Point(origin.X - shift, origin.Y), s, value);
            pending.Clear(); chunkWidth = 0;
        }
        void Emit(FormattedText formatted, Point origin, Style s, string value)
        {
            if (!s.Visible || value.Trim().Length == 0) return;
            uint? fill = Paint(s.Fill, s.Color, s.FillOpacity * s.Opacity) ?? Paint(s.Stroke, s.Color, s.StrokeOpacity * s.Opacity);
            if (fill == null) return;
            var local = Polygons(formatted.BuildGeometry(origin), Math.Max(s.FontSize * 0.002, 1e-9));
            var contours = local.Select(p => (IReadOnlyList<Point>)p.Select(ctm.Transform).ToList()).ToList();
            if (contours.Count == 0) return;
            doc.Entities.Add(new DxfEntity("TEXT", doc.Layers[parent.Layer].Name, run++ == 0 ? handle : handle + "#" + run, DxfPrimitives.Outline(contours))
            {
                Color = fill.Value, FillColor = fill, FillContours = contours, Text = value.Trim(), IsApproximate = true,
                InstancePath = useChain.Count == 0 ? null : string.Join("/", useChain.Reverse())
            });
        }
        void Nodes(XElement container, Style current)
        {
            foreach (var node in container.Nodes())
            {
                if (node is XText text) { Run(text.Value, current); continue; }
                if (node is not XElement child) continue;
                if (child.Name.LocalName is not ("tspan" or "a")) { Note("text child " + child.Name.LocalName + " (not supported)"); continue; }
                var a = Attributes(child);
                var s = Compute(current, child.Name.LocalName, a);
                if (decl.GetValueOrDefault("display") == "none") continue;
                double? x = First(a, "x", viewWidth), y = First(a, "y", viewHeight);
                if (x != null || y != null) Flush();
                if (x != null) pen.X = x.Value; if (y != null) pen.Y = y.Value;
                pen.Offset(First(a, "dx", viewWidth) ?? 0, First(a, "dy", viewHeight) ?? 0);
                Nodes(child, s);
            }
        }
        void Run(string raw, Style s)
        {
            var value = string.Join(' ', raw.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]) && !first) value = " " + value;
            if (raw.Length > 0 && char.IsWhiteSpace(raw[^1]) && value.Trim().Length > 0) value += " ";
            if (first) value = value.TrimStart();
            if (value.Length == 0) return;
            first = false;
            var family = s.FontFamily;
            var typeface = new Typeface(new FontFamily(family), s.Italic ? FontStyles.Italic : FontStyles.Normal,
                s.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(s.FontSize, 1e-6), Brushes.Black, 1);
            double width = formatted.WidthIncludingTrailingWhitespace;
            pending.Add((formatted, new Point(pen.X, pen.Y - formatted.Baseline), s, value));
            chunkWidth += width; pen.X += width;
        }
        Nodes(element, style);
        Flush();
        if (element.Descendants().Any(e => e.Name.LocalName == "textPath")) Note("textPath (not supported)");
    }

    private double? First(Dictionary<string, string> attrs, string key, double reference)
    {
        if (attrs.GetValueOrDefault(key) is not { } text) return null;
        var parts = text.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) Note("per-glyph text positions (first value used)");
        return parts.Length == 0 ? null : Length(parts[0], reference);
    }

    // ---------------------------------------------------------------- style cascade
    private Style Compute(Style parent, string tag, Dictionary<string, string> attrs)
    {
        decl.Clear();
        foreach (var (key, value) in attrs) if (StyleProperties.Contains(key)) decl[key] = value.Trim();
        if (rules.Count > 0)
        {
            var id = attrs.GetValueOrDefault("id"); var classes = attrs.GetValueOrDefault("class")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            foreach (var rule in rules.Where(r => (r.Tag == null || r.Tag == tag) && (r.Id == null || r.Id == id) && (r.Class == null || classes.Contains(r.Class)))
                         .OrderBy(r => r.Specificity).ThenBy(r => r.Order))
                foreach (var (key, value) in rule.Declarations) decl[key] = value;
        }
        if (attrs.GetValueOrDefault("style") is { } inline) foreach (var (key, value) in Declarations(inline)) decl[key] = value;
        if (decl.Count == 0) return parent;
        string? Get(string key) => decl.TryGetValue(key, out var v) && v != "inherit" ? v : null;
        double Fraction(string key, double fallback) => Get(key) is { } v && double.TryParse(v.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(v.EndsWith('%') ? n / 100 : n, 0, 1) : fallback;
        double fontSize = parent.FontSize;
        if (Get("font-size") is { } size)
            fontSize = size switch { "smaller" => fontSize / 1.2, "larger" => fontSize * 1.2, _ when size.EndsWith('%') => fontSize * Length(size, 100) / 100,
                _ when size.EndsWith("em") => fontSize * Length(size[..^2], 1), _ => Length(size, fontSize) is > 0 and var px ? px : fontSize };
        if (Get("font") != null) Note("font shorthand (not parsed)");
        var weight = Get("font-weight");
        return parent with
        {
            Fill = Get("fill") ?? parent.Fill, Stroke = Get("stroke") ?? parent.Stroke,
            EvenOdd = Get("fill-rule") is { } fillRule ? fillRule == "evenodd" : parent.EvenOdd,
            FillOpacity = Fraction("fill-opacity", parent.FillOpacity), StrokeOpacity = Fraction("stroke-opacity", parent.StrokeOpacity),
            Opacity = parent.Opacity * Fraction("opacity", 1), Color = Get("color") ?? parent.Color, FontSize = fontSize,
            FontFamily = Get("font-family") is { } family ? Family(family) : parent.FontFamily,
            Bold = weight == null ? parent.Bold : weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600),
            Italic = Get("font-style") is { } italic ? italic is "italic" or "oblique" : parent.Italic,
            Anchor = Get("text-anchor") switch { "middle" => 0.5, "end" => 1, "start" => 0, _ => parent.Anchor },
            Visible = Get("visibility") is { } visibility ? visibility == "visible" : parent.Visible
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> families = new(StringComparer.OrdinalIgnoreCase);
    private static string Family(string list) => families.GetOrAdd(list, ResolveFamily);
    private static string ResolveFamily(string list)
    {
        foreach (var raw in list.Split(','))
        {
            var name = raw.Trim().Trim('\'', '"');
            var mapped = name switch { "serif" => "Times New Roman", "sans-serif" => "Arial", "monospace" => "Courier New", "cursive" => "Comic Sans MS", "fantasy" => "Impact", _ => name };
            if (Fonts.SystemFontFamilies.Any(f => f.Source.Equals(mapped, StringComparison.OrdinalIgnoreCase))) return mapped;
        }
        return "Arial";
    }

    private static IEnumerable<(string, string)> Declarations(string text)
    {
        foreach (var part in text.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var value = part[(colon + 1)..].Replace("!important", "").Trim();
            yield return (part[..colon].Trim().ToLowerInvariant(), value);
        }
    }

    private void ParseCss(string css)
    {
        while (css.IndexOf("/*", StringComparison.Ordinal) is var a and >= 0)
        {
            int b = css.IndexOf("*/", a + 2, StringComparison.Ordinal);
            css = b < 0 ? css[..a] : css[..a] + css[(b + 2)..];
        }
        foreach (var block in css.Split('}'))
        {
            int open = block.IndexOf('{');
            if (open < 0) continue;
            var declarations = Declarations(block[(open + 1)..]).ToDictionary(p => p.Item1, p => p.Item2);
            foreach (var raw in block[..open].Split(','))
            {
                var selector = raw.Trim();
                if (selector.Length == 0) continue;
                if (selector.StartsWith('@') || selector.IndexOfAny([' ', '>', '+', '~', ':', '[']) >= 0) { Note("CSS selector " + selector + " (not supported)"); continue; }
                string? tag = null, cls = null, id = null;
                foreach (var token in System.Text.RegularExpressions.Regex.Matches(selector, @"[.#]?[^.#]+").Select(m => m.Value))
                    if (token[0] == '.') cls = token[1..]; else if (token[0] == '#') id = token[1..]; else if (token != "*") tag = token;
                rules.Add(new Rule(tag, cls, id, (id != null ? 100 : 0) + (cls != null ? 10 : 0) + (tag != null ? 1 : 0), rules.Count, declarations));
            }
        }
    }

    // ---------------------------------------------------------------- paint
    private uint? Paint(string? paint, string? currentColor, double opacity)
    {
        if (paint == null || paint == "none" || paint == "transparent") return null;
        if (paint == "currentColor") paint = currentColor ?? "black";
        if (paint.StartsWith("url(", StringComparison.Ordinal))
        {
            int close = paint.IndexOf(')');
            var id = close > 5 ? paint[4..close].Trim().Trim('\'', '"').TrimStart('#') : "";
            var fallback = close >= 0 ? paint[(close + 1)..].Trim() : "";
            if (byId.TryGetValue(id, out var server) && GradientColor(server, 0) is { } stop)
            { Note("gradient fill (shown as its first stop color)"); return WithAlpha(stop, opacity); }
            Note("pattern or unresolved paint server (fallback color)");
            return fallback.Length > 0 && fallback != "none" ? Paint(fallback, currentColor, opacity) : null;
        }
        return Color(paint) is { } color ? WithAlpha(color, opacity) : null;
    }

    private static uint WithAlpha(uint color, double opacity) =>
        (color & 0x00FFFFFF) | (uint)Math.Round(((color >> 24) / 255.0) * Math.Clamp(opacity, 0, 1) * 255) << 24;

    private uint? GradientColor(XElement server, int depth)
    {
        if (server.Name.LocalName is not ("linearGradient" or "radialGradient") || depth > 8) return null;
        var stop = server.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
        if (stop != null)
        {
            var a = Attributes(stop);
            var props = a.GetValueOrDefault("style") is { } s ? Declarations(s).ToDictionary(p => p.Item1, p => p.Item2) : [];
            var color = props.GetValueOrDefault("stop-color") ?? a.GetValueOrDefault("stop-color") ?? "black";
            var opacityText = props.GetValueOrDefault("stop-opacity") ?? a.GetValueOrDefault("stop-opacity");
            double opacity = double.TryParse(opacityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1;
            return Color(color) is { } c ? WithAlpha(c, opacity) : null;
        }
        var href = (string?)server.Attribute(XName.Get("href", XlinkNs)) ?? (string?)server.Attribute("href");
        return href is { Length: > 1 } && byId.TryGetValue(href[1..], out var parent) ? GradientColor(parent, depth + 1) : null;
    }

    private uint? Color(string text)
    {
        if (colorCache.TryGetValue(text, out var known)) return known;
        uint? result = null; var t = text.Trim();
        if (t.StartsWith('#'))
        {
            var hex = t[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            {
                uint rgb = hex.Length == 8 ? v >> 8 : v, alpha = hex.Length == 8 ? v & 0xFF : 0xFF;
                result = DxfColors.Pack((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) & 0x00FFFFFF | alpha << 24;
            }
        }
        else if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && t.IndexOf('(') is var open and > 0 && t.EndsWith(')'))
        {
            var parts = t[(open + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            double Channel(string p) => p.EndsWith('%') ? Length(p[..^1], 0) * 2.55 : Length(p, 0);
            if (parts.Length >= 3)
            {
                var c = DxfColors.Pack((byte)Math.Clamp(Math.Round(Channel(parts[0])), 0, 255), (byte)Math.Clamp(Math.Round(Channel(parts[1])), 0, 255), (byte)Math.Clamp(Math.Round(Channel(parts[2])), 0, 255));
                double alpha = parts.Length > 3 ? (parts[3].EndsWith('%') ? Length(parts[3][..^1], 0) / 100 : Length(parts[3], 1)) : 1;
                result = WithAlpha(c, alpha);
            }
        }
        else
        {
            try { if (ColorConverter.ConvertFromString(t) is System.Windows.Media.Color c) result = DxfColors.Pack(c.R, c.G, c.B); }
            catch (FormatException) { }
        }
        if (result == null) Note("unrecognized color (element not painted)");
        return colorCache[text] = result;
    }

    // ---------------------------------------------------------------- numbers, units, transforms
    private static List<double> Numbers(string? text)
    {
        var result = new List<double>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"))
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) result.Add(v);
        return result;
    }

    /// <summary>A length in user units (CSS px); percentages use <paramref name="reference"/>.</summary>
    private static double Length(string? text, double reference)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim(); double factor = 1;
        foreach (var (suffix, f) in new[] { ("px", 1.0), ("pt", 96 / 72.0), ("pc", 16.0), ("mm", 96 / 25.4), ("cm", 96 / 2.54), ("in", 96.0), ("%", -1.0) })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { t = t[..^suffix.Length]; factor = f < 0 ? reference / 100 : f; break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v * factor : 0;
    }

    private static (double Value, string? Unit) Physical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, null);
        var t = text.Trim();
        foreach (var (suffix, unit) in new[] { ("mm", "mm"), ("cm", "cm"), ("in", "inches"), ("pt", "pt"), ("pc", "pc") })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) return (v, unit);
        return (0, null);
    }

    private static double UnitsPerPixel(string unit) => unit switch { "mm" => 25.4 / 96, "cm" => 2.54 / 96, "inches" => 1 / 96.0, "pt" => 72 / 96.0, "pc" => 6 / 96.0, _ => 1 };

    private static Rect? ViewBox(string? text)
    {
        var v = Numbers(text);
        return v.Count == 4 && v[2] > 0 && v[3] > 0 ? new Rect(v[0], v[1], v[2], v[3]) : null;
    }

    /// <summary>Parses an SVG transform list; the result maps local coordinates into the parent's (WPF row-vector order).</summary>
    private static Matrix Transform(string? text)
    {
        var result = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var v = Numbers(m.Groups[2].Value); var f = Matrix.Identity;
            double At(int i, double fallback = 0) => i < v.Count ? v[i] : fallback;
            switch (m.Groups[1].Value)
            {
                case "matrix" when v.Count == 6: f = new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]); break;
                case "translate": f.Translate(At(0), At(1)); break;
                case "scale": f.Scale(At(0, 1), At(1, At(0, 1))); break;
                case "rotate": f.RotateAt(At(0), At(1), At(2)); break;
                case "skewX": f.Skew(At(0), 0); break;
                case "skewY": f.Skew(0, At(0)); break;
            }
            result = f * result;
        }
        return result;
    }

    // ---------------------------------------------------------------- diagnostics and publication
    private void Skip(string what) => doc.Skipped[what] = doc.Skipped.GetValueOrDefault(what) + 1;
    private void Note(string what) => doc.RenderingWarnings["SVG " + what] = doc.RenderingWarnings.GetValueOrDefault("SVG " + what) + 1;

    private DxfDocument Finish()
    {
        if (doc.Entities.Count == 0)
            throw new InvalidDataException("No supported SVG geometry was found. Supported: line, rect, circle, ellipse, polyline, polygon, path, text, use and groups.");
        // Drop the default layer when every element sits on a named layer.
        if (doc.Layers.Count > 1 && !doc.Entities.Any(e => e.Layer == "0")) doc.Layers.RemoveAt(0);
        var layers = doc.Layers.Select((l, i) => (l.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        var groups = new Dictionary<(int, EntityCategory), int>();
        foreach (var entity in doc.Entities)
        {
            var key = (layers[entity.Layer], entity.Category);
            if (!groups.TryGetValue(key, out var group)) { group = groups[key] = doc.Groups.Count; doc.Groups.Add(key); }
            entity.Group = group;
        }
        Parallel.ForEach(doc.Entities.Where(e => e.FillContours.Count > 0), new ParallelOptions { CancellationToken = LoadingContext.Token },
            e => e.FillTriangles = Array.AsReadOnly(DxfPrimitives.Triangulate(e.FillContours).ToArray()));
        return doc.Freeze();
    }
}
