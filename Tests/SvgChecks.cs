using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

/// <summary>SVG import: geometry mapping, units, styles, fills, text, &lt;use&gt;, security limits and GPU display.</summary>
[Collection("Visual checks")]
public sealed class SvgChecks
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "svg", "shapes.svg");
    private static DxfDocument Drawing() => SvgReader.Load(FixturePath);
    private static DxfDocument Parse(string svg) => SvgReader.Load(new MemoryStream(Encoding.UTF8.GetBytes(svg)));
    private static DxfEntity Find(DxfDocument doc, string handle) => doc.Entities.Single(e => e.Handle == handle);
    private static void PointIs(Point expected, Point actual, double tolerance = 1e-9)
    { TestSupport.Near(expected.X, actual.X, tolerance); TestSupport.Near(expected.Y, actual.Y, tolerance); }

    [Fact]
    public void ShapesUnitsAndExactGeometry() => TestSupport.Run("SVG shapes: units, y-flip, exact lines/arcs and approximations", () =>
    {
        var doc = Drawing();
        Assert.Equal(DrawingFormat.Svg, doc.SourceFormat); Assert.Equal("mm", doc.Units);
        var line = Find(doc, "L"); PointIs(new(10, -10), line.Curves.Single().Start); PointIs(new(50, -10), line.Curves.Single().End);
        Assert.False(line.IsApproximate); Assert.Equal(DxfColors.Pack(255, 0, 0), line.Color);
        var circle = Find(doc, "C"); Assert.True(circle.FillDisc); Assert.Empty(circle.FillContours);
        PointIs(new(80, -10), circle.Curves.Single().Center); TestSupport.Near(5, circle.Curves.Single().Radius);
        Assert.True(circle.Contains(new(82, -11))); Assert.False(circle.Contains(new(86, -10)));
        Assert.Equal(4, Find(doc, "R").Curves.Count); Assert.All(Find(doc, "R").Curves, c => Assert.True(c.IsLine));
        var rounded = Find(doc, "RR"); Assert.False(rounded.IsApproximate); Assert.Equal(4, rounded.Curves.Count(c => !c.IsLine));
        Assert.True(rounded.Contains(new(50, -35))); Assert.NotNull(rounded.FillTriangles);
        var ellipse = Find(doc, "E"); Assert.True(ellipse.IsApproximate);
        // Chord vertices lie on the ellipse; the document bound is 1e-5 of the 200-unit view (0.002).
        TestSupport.Near(72, ellipse.Bounds.Left, 2e-3); TestSupport.Near(88, ellipse.Bounds.Right, 2e-3);
        var path = Find(doc, "P"); Assert.True(path.IsApproximate);
        Assert.Contains(path.Curves, c => !c.IsLine && Math.Abs(c.Radius - 5) < 1e-9);
        PointIs(new(120, -15), path.Curves.Single(c => !c.IsLine).Center);
        // Rotated + translated group: local (10,0) maps to svg (150,20), world (150,-20).
        var turned = Find(doc, "TL"); PointIs(new(150, -10), turned.Curves[0].Start, 1e-9); PointIs(new(150, -20), turned.Curves[0].End, 1e-9);
        Assert.Equal("G", turned.Layer); Assert.Equal(DxfColors.Pack(255, 0, 0), turned.Color); // #TL CSS rule beats inherited black
        Assert.Empty(doc.Entities.Where(e => e.Handle == "HIDDEN"));
    });

    [Fact]
    public void MeasurementAndMidpoints() => TestSupport.Run("SVG measurement: circle center strategy and midpoint eligibility", () =>
    {
        var doc = Drawing();
        var line = Find(doc, "L"); var circle = Find(doc, "C");
        var closest = doc.Measure(new(line.Id), new(circle.Id));
        Assert.Equal(MeasurementStatus.Success, closest.Status); TestSupport.Near(25, closest.Distance!.Value);
        var center = doc.Measure(new(line.Id), new(circle.Id), MeasurementStrategy.Closest, MeasurementStrategy.Center);
        TestSupport.Near(30, center.Distance!.Value); Assert.False(center.IsApproximate);
        TestSupport.Near(-20, doc.Measure(new(line.Id, 0), new(Find(doc, "R").Id, 0)).DeltaY!.Value);
        Assert.Equal(MeasurementStatus.Success, doc.Measure(new(line.Id, 0), new(circle.Id)).Status);
        Assert.Equal(MeasurementStatus.InvalidAnchor, doc.Measure(new(Find(doc, "P").Id, 0), new(line.Id)).Status);
        var index = new MidpointIndex([line, Find(doc, "P"), Find(doc, "E")]);
        Assert.NotNull(index.Find(new(30, -10), 1e-6));
        Assert.Null(index.Find(Find(doc, "P").Curves[0].At(.5), 1e-6));
    });

    [Fact]
    public void StylesFillsTextAndLayers() => TestSupport.Run("SVG styles: CSS, gradients, fill rules, text anchor, use and Inkscape layers", () =>
    {
        var doc = Drawing();
        var poly = Find(doc, "PL"); Assert.Equal(DxfColors.Pack(0, 255, 0), poly.Color); Assert.Empty(poly.FillContours);
        var gradient = Find(doc, "PG"); Assert.Equal(DxfColors.Pack(0x33, 0x66, 0x99), gradient.FillColor);
        Assert.Contains(doc.RenderingWarnings.Keys, k => k.Contains("gradient"));
        var nonzero = Find(doc, "NZ"); Assert.True(nonzero.Contains(new(70, -65))); Assert.True(nonzero.Contains(new(62, -57)));
        var evenOdd = Find(doc, "EO"); Assert.False(evenOdd.Contains(new(100, -65))); Assert.True(evenOdd.Contains(new(92, -57)));
        Assert.Equal(0x80u, evenOdd.FillColor!.Value >> 24);
        var dot = Find(doc, "dot"); Assert.True(dot.FillDisc); PointIs(new(150, -40), dot.Curves[0].Center); Assert.Equal("#dot", dot.InstancePath);
        var text = doc.Entities.Where(e => e.Kind == "TEXT").ToList();
        Assert.Equal(["Hi", "there"], text.Select(t => t.Text!).ToArray());
        Assert.Equal(DxfColors.Pack(255, 0, 0), text[1].FillColor); Assert.Equal(EntityCategory.Text, text[0].Category);
        // text-anchor end applies to the whole chunk: its right edge is at x = 140.
        TestSupport.Near(140, text.Max(t => t.Bounds.Right), 0.8); Assert.True(text[0].Bounds.Right < text[1].Bounds.Left + 0.5);
        var hidden = doc.Layers.Single(l => l.Name == "Hidden layer"); Assert.False(hidden.OnInFile);
        Assert.Equal("Hidden layer", Find(doc, "HL").Layer);
    });

    [Fact]
    public void PathGrammarAndTransforms() => TestSupport.Run("SVG path grammar, implicit commands, compact arc flags and nonuniform transforms", () =>
    {
        var subpaths = SvgPathData.Parse("m10-10 20,0 0 5zM0 0L1e1.5h-1v-.5a5 5 0 1010 0", 1e-4, out var approximate, out var error);
        Assert.Null(error); Assert.False(approximate); Assert.Equal(2, subpaths.Count);
        PointIs(new(30, -10), subpaths[0][0].End); PointIs(new(10, -10), subpaths[0][2].End);
        Assert.Equal(new Point(10, .5), subpaths[1][0].End);
        var arc = subpaths[1].Last(); Assert.False(arc.IsLine); TestSupport.Near(5, arc.Radius);
        SvgPathData.Parse("M0 0 L 1", 1e-4, out _, out error); Assert.NotNull(error);
        var doc = Parse("""<svg xmlns="http://www.w3.org/2000/svg"><circle id="c" cx="0" cy="0" r="10" transform="scale(2 1)" fill="none" stroke="black"/><rect id="s" width="10" height="10" transform="skewX(45)" stroke="black" fill="none"/></svg>""");
        var stretched = Find(doc, "c"); Assert.True(stretched.IsApproximate); Assert.All(stretched.Curves, c => Assert.True(c.IsLine));
        TestSupport.Near(-20, stretched.Bounds.Left, 1e-3); TestSupport.Near(20, stretched.Bounds.Right, 1e-3);
        PointIs(new(20, -10), Find(doc, "s").Curves[1].End, 1e-9);
        Assert.Equal("px (SVG user units)", doc.Units);
    });

    [Fact]
    public void SecurityAndLimits() => TestSupport.Run("SVG security: DTD entities ignored, external references never fetched, use cycles stopped", () =>
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => Parse("""<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><svg xmlns="http://www.w3.org/2000/svg"><text x="0" y="0">&x;</text></svg>"""));
        var doc = Parse("""<svg xmlns="http://www.w3.org/2000/svg"><defs><g id="a"><use href="#b"/></g><g id="b"><use href="#a"/></g></defs><use href="#a"/><line x1="0" y1="0" x2="1" y2="0" stroke="black"/><use href="other.svg#x"/><image href="http://example.invalid/x.png"/></svg>""");
        Assert.Contains(doc.Skipped.Keys, k => k.StartsWith("use (cycle"));
        Assert.Contains(doc.Skipped.Keys, k => k.StartsWith("use (missing or external"));
        Assert.Contains(doc.Skipped.Keys, k => k.StartsWith("image"));
        Assert.Throws<InvalidDataException>(() => Parse("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="0" height="0"/></svg>"""));
        Assert.Throws<InvalidDataException>(() => Parse("""<html><svg/></html>"""));
        var zipped = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(zipped, System.IO.Compression.CompressionLevel.Fastest, true))
            gzip.Write(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><line x1="0" y1="0" x2="3" y2="4" stroke="black"/></svg>"""));
        zipped.Position = 0;
        TestSupport.Near(5, (SvgReader.Load(zipped).Entities.Single().Curves[0].End - default(Point)).Length);
    });

    // Deep zoom on a cubic: the drawn ink must follow the exact curve within a pixel and be antialiased.
    [Fact]
    public void ZoomedBezierIsSmoothAndAntialiased() => TestSupport.Run("GPU frames: deep zoom on a cubic Bézier stays on the exact curve and is antialiased", () => TestSupport.Sta(() =>
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target);
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        Point[] c = [new(1000, 5000), new(3000, 1000), new(7000, 9000), new(9000, 5000)];
        Point Bezier(double t) { double u = 1 - t; return new(u * u * u * c[0].X + 3 * u * u * t * c[1].X + 3 * u * t * t * c[2].X + t * t * t * c[3].X,
            -(u * u * u * c[0].Y + 3 * u * u * t * c[1].Y + 3 * u * t * t * c[2].Y + t * t * t * c[3].Y)); }
        double YAt(double x) { double lo = 0, hi = 1; for (int i = 0; i < 60; i++) { var mid = (lo + hi) / 2; if (Bezier(mid).X < x) lo = mid; else hi = mid; } return Bezier(lo).Y; }
        var doc = Parse($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10000 10000" width="10000" height="10000"><path d="M{c[0].X} {c[0].Y} C{c[1].X} {c[1].Y} {c[2].X} {c[2].Y} {c[3].X} {c[3].Y}" fill="none" stroke="black"/></svg>""");
        var view = new DxfViewport(); var window = new Window { Content = view, Width = 900, Height = 600, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show(); view.SetDocument(doc); Pump();
            var renderer = (DrawingRenderer)Field(Field(view, "host")!, "<Renderer>k__BackingField")!;
            foreach (var (zoom, t) in new[] { (100.0, 0.3), (2000.0, 0.62) })
            {
                var focus = Bezier(t);
                view.GetType().GetField("scale", flags)!.SetValue(view, zoom);
                view.GetType().GetField("offset", flags)!.SetValue(view, new Vector(view.ActualWidth / 2 - focus.X * zoom, view.ActualHeight / 2 + focus.Y * zoom));
                var frame = VisualReport.Capture(renderer, $"Cubic Bézier at {zoom:0} px per unit", () => Call(view, "RenderNow"));
                double sx = frame.Width / view.ActualWidth, sy = frame.Height / view.ActualHeight, worst = 0; int columns = 0, smooth = 0;
                var offset = (Vector)Field(view, "offset")!;
                for (int x = frame.Width / 10; x < frame.Width * 9 / 10; x++)
                {
                    double expected = (-YAt((x / sx - offset.X) / zoom) * zoom + offset.Y) * sy;
                    if (expected < 3 || expected > frame.Height - 3) continue;
                    double weight = 0, sum = 0; bool partial = false;
                    for (int y = Math.Max(0, (int)expected - 40); y < Math.Min(frame.Height, (int)expected + 40); y++)
                    {
                        int i = (y * frame.Width + x) * 4; double ink = 255 - (frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2]) / 3.0;
                        if (ink < 8) continue;
                        weight += ink; sum += ink * (y + 0.5); partial |= ink is > 25 and < 200;
                    }
                    if (weight == 0) { worst = double.PositiveInfinity; break; }
                    worst = Math.Max(worst, Math.Abs(sum / weight - expected)); columns++; if (partial) smooth++;
                }
                Assert.True(columns > 100, $"zoom {zoom}: only {columns} columns checked");
                Assert.True(worst <= 1.0, $"zoom {zoom}: drawn line deviates {worst:0.00} px from the exact curve");
                Assert.True(smooth > columns / 2, $"zoom {zoom}: only {smooth} of {columns} columns show antialiased edge pixels");
            }
        }
        finally { window.Close(); Pump(); }
    }));

    // Plot files repeat every point: a black dot first, then a colored dot at the same place. Document order must win.
    [Fact]
    public void LaterDotsPaintOverEarlierOnes() => TestSupport.Run("GPU frames: 400 overlapping SVG point markers keep document paint order", () => TestSupport.Sta(() =>
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target);
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        var svg = new StringBuilder("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="200" height="200">""");
        var spots = new List<Point>();
        for (int i = 0; i < 20; i++) for (int j = 0; j < 20; j++) spots.Add(new(5 + i * 10, 5 + j * 10));
        foreach (var p in spots) svg.Append($"""<circle cx="{p.X}" cy="{p.Y}" r="1" fill="black"/>""");
        foreach (var p in spots) svg.Append($"""<circle cx="{p.X}" cy="{p.Y}" r="1" fill="red"/>""");
        var doc = Parse(svg.Append("</svg>").ToString());
        var view = new DxfViewport(); var window = new Window { Content = view, Width = 900, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show(); view.SetDocument(doc); Pump();
            var renderer = (DrawingRenderer)Field(Field(view, "host")!, "<Renderer>k__BackingField")!;
            var frame = VisualReport.Capture(renderer, "400 black-then-red point markers", () => Call(view, "RenderNow"));
            int black = 0;
            foreach (var p in spots)
            {
                var s = (Point)Call(view, "Screen", new Point(p.X, -p.Y))!;
                int i = ((int)(s.Y * frame.Height / view.ActualHeight) * frame.Width + (int)(s.X * frame.Width / view.ActualWidth)) * 4;
                byte b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2];
                if (!(r > g + 60 && r > b + 60)) black++;
            }
            Assert.True(black == 0, $"{black} of {spots.Count} dots show the earlier black dot instead of the later red one");
        }
        finally { window.Close(); Pump(); }
    }));

    [Fact]
    public void BackgroundToggleAndDiscsOnGpu() => TestSupport.Run("GPU frames: SVG on white, filled discs, dark toggle without re-upload", () => TestSupport.Sta(() =>
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target);
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        var view = new DxfViewport(); var window = new Window { Content = view, Width = 1000, Height = 560, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show(); var doc = Drawing(); view.SetDocument(doc); Pump();
            var renderer = (DrawingRenderer)Field(Field(view, "host")!, "<Renderer>k__BackingField")!;
            Assert.True(view.IsLightBackground);
            var frame = VisualReport.Capture(renderer, "SVG fixture on the default white background", () => Call(view, "RenderNow"));
            bool Pixel(Point p, Func<byte, byte, byte, bool> predicate)
            {
                var screen = (Point)Call(view, "Screen", p)!;
                return frame.Near((int)(screen.X * frame.Width / view.ActualWidth), (int)(screen.Y * frame.Height / view.ActualHeight), predicate);
            }
            Assert.True(Pixel(new(80, -10), (r, g, b) => b > 200 && r < 80 && g < 80));      // blue disc interior
            Assert.True(Pixel(new(150, -40), (r, g, b) => r < 60 && g < 60 && b < 60));      // black <use> disc
            Assert.True(Pixel(new(130, -60), (r, g, b) => r > 240 && g > 240 && b > 240));   // white paper
            Assert.True(Pixel(new(50, -35), (r, g, b) => g > 100 && r < 60 && b < 60));      // green rounded rect fill
            // Document order: the later red dot covers the earlier black one, including its rim.
            var dot = (Point)Call(view, "Screen", new Point(180, -60))!; int dx = (int)(dot.X * frame.Width / view.ActualWidth), dy = (int)(dot.Y * frame.Height / view.ActualHeight);
            double rim = 3 * (double)Field(view, "scale")! * frame.Width / view.ActualWidth;
            for (int k = -(int)rim; k <= (int)rim; k++)
            {
                int i = (dy * frame.Width + dx + k) * 4; byte b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2];
                Assert.False(r < 120 && g < 120 && b < 120, $"black ink at offset {k} px inside the red dot (r={r} g={g} b={b})");
            }
            // Zoomed out so the dot is ~3 px wide (like plot point markers): its pixels must still read red, not black.
            var savedScale = Field(view, "scale")!; var savedOffset = Field(view, "offset")!;
            view.GetType().GetField("scale", flags)!.SetValue(view, 0.5);
            view.GetType().GetField("offset", flags)!.SetValue(view, new Vector(view.ActualWidth / 2 - 180 * 0.5, view.ActualHeight / 2 - 60 * 0.5));
            var small = VisualReport.Capture(renderer, "Overlapping SVG point markers at 3 px", () => Call(view, "RenderNow"));
            int cx = small.Width / 2, cy = small.Height / 2, red = 0, dark = 0;
            for (int y = cy - 3; y <= cy + 3; y++) for (int x = cx - 3; x <= cx + 3; x++)
            {
                int i = (y * small.Width + x) * 4; byte b = small.Pixels[i], g = small.Pixels[i + 1], r = small.Pixels[i + 2];
                if (r > g + 60 && r > b + 60) red++; if (r < 110 && g < 110 && b < 110) dark++;
            }
            Assert.True(red > 0 && dark == 0, $"small overlapping dots: {red} red and {dark} dark pixels");
            view.GetType().GetField("scale", flags)!.SetValue(view, savedScale); view.GetType().GetField("offset", flags)!.SetValue(view, savedOffset);
            var discs = Field(renderer, "discBuffer"); var fills = Field(renderer, "fillBuffer"); Assert.NotNull(discs);
            view.BackgroundMode = DrawingBackground.Dark; Assert.False(view.IsLightBackground);
            frame = VisualReport.Capture(renderer, "SVG fixture toggled to the dark background", () => Call(view, "RenderNow"));
            Assert.True(Pixel(new(130, -60), (r, g, b) => r < 40 && g < 40 && b < 50));
            Assert.True(Pixel(new(150, -40), (r, g, b) => r > 60 && g > 60 && b > 60));      // black lifted on dark
            Assert.Same(discs, Field(renderer, "discBuffer")); Assert.Same(fills, Field(renderer, "fillBuffer"));
            Call(view, "SelectAt", new Point(80, -10), true); Assert.Equal("CIRCLE", view.Selected.Single().Entity.Kind);
        }
        finally { window.Close(); Pump(); }
    }));
}
