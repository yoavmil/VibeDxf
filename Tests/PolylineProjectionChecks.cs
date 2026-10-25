using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class PolylineProjectionChecks
{
    private static string Entity(string kind, params object[] fields) => "0\n" + kind + "\n" + (fields.Length == 0 ? "" : string.Join("\n", fields.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))) + "\n");
    private static string Header(int flags, params object[] fields) => Entity("POLYLINE", 5, "P", 8, "DETAIL", 62, 1, 70, flags) + string.Join("\n", fields) + (fields.Length > 0 ? "\n" : "");
    private static string Vertex(double x, double y, double z, int flags = 32) => Entity("VERTEX", 10, x, 20, y, 30, z, 70, flags);
    private static string Face(params int[] indices) => Entity("VERTEX", new object[] { 70, 128 }.Concat(indices.SelectMany((v, i) => new object[] { 71 + i, v })).ToArray());
    private static DxfDocument Load(string entities, string blocks = "")
    {
        var text = "0\nSECTION\n2\nBLOCKS\n" + blocks + "0\nENDSEC\n0\nSECTION\n2\nENTITIES\n" +
            Entity("LINE", 5, "KEEP", 10, 100, 20, 100, 11, 101, 21, 100) + entities + "0\nENDSEC\n0\nEOF\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); return DxfReader.Load(stream);
    }
    private static DxfEntity Projected(DxfDocument document) => document.Entities.Single(e => e.Kind == "POLYLINE");

    [Fact]
    public void ThreeDimensionalPolylineUsesExactXYGeometry() => TestSupport.Run("3D POLYLINE: exact XY projection, distance and midpoints", () =>
    {
        var doc = Load(Header(8) + Vertex(0, 0, 0) + Vertex(3, 4, 12) + Entity("SEQEND") + Entity("POINT", 5, "Q", 10, 3, 20, 0));
        var line = Projected(doc); var curve = Assert.Single(line.Curves);
        Assert.Equal(new Point(0, 0), curve.Start); Assert.Equal(new Point(3, 4), curve.End);
        TestSupport.Near(5, (curve.End - curve.Start).Length); Assert.False(line.IsApproximate);
        var result = doc.Measure(new(line.Id), new(doc.Entities.Single(e => e.Handle == "Q").Id));
        Assert.Equal(MeasurementStatus.Success, result.Status); TestSupport.Near(2.4, result.Distance!.Value);
        var midpoint = new MidpointIndex([line]).Find(new(1.5, 2), 1e-8);
        Assert.NotNull(midpoint); Assert.Equal(new Point(1.5, 2), midpoint.Midpoint);
        Assert.Contains(doc.RenderingWarnings.Keys, k => k.Contains("XY wireframe projection") && k.Contains("ignore Z"));
    });

    [Fact]
    public void ClosedAndVerticalPolylinesRemainPickable() => TestSupport.Run("3D POLYLINE: closure and vertical projected point", () =>
    {
        var closed = Projected(Load(Header(9) + Vertex(0, 0, 1) + Vertex(4, 0, 3) + Vertex(4, 4, 5) + Entity("SEQEND")));
        Assert.Equal(3, closed.Curves.Count); Assert.Equal(new Point(0, 0), closed.Curves[^1].End);
        var verticalDoc = Load(Header(8) + Vertex(2, 3, 0) + Vertex(2, 3, 10) + Entity("SEQEND"));
        var vertical = Projected(verticalDoc); Assert.Equal(new Point(2, 3), Assert.Single(vertical.RenderDots));
        Assert.Equal(new Point(2, 3), vertical.Closest(new(5, 3))); Assert.Null(new MidpointIndex([vertical]).Find(new(2, 3), 1));
        TestSupport.Near(3, new MeasurementTarget(vertical).Distance(new(new DxfEntity("POINT", "0", "", [Curve.Line(new(5, 3), new(5, 3))]))).Distance);
    });

    [Fact]
    public void PolyfaceHonorsHiddenEdgesAndAdvisoryCounts() => TestSupport.Run("POLYFACE: signed indices, forward faces and no invented interiors", () =>
    {
        // The header counts are advisory in DXF; a face may precede its coordinate records.
        var doc = Load(Header(64, 71, 999, 72, 999) + Face(1, -2, 3, 4) +
            Vertex(0, 0, 0, 192) + Vertex(4, 0, 2, 192) + Vertex(4, 4, 3, 192) + Vertex(0, 4, 0, 192) + Entity("SEQEND"));
        var face = Projected(doc); Assert.Equal(3, face.Curves.Count);
        Assert.DoesNotContain(face.Curves, c => c.Start == new Point(4, 0) && c.End == new Point(4, 4));
        Assert.Equal(new Point(4, 0), face.Closest(new(4, 2))); Assert.Empty(face.FillContours); Assert.False(face.Contains(new(2, 2)));
        Assert.Equal("DETAIL", face.Layer); Assert.Equal(DxfColors.FromAci(1), face.Color);
        Assert.Equal("P", face.SourceHandle);
    });

    [Fact]
    public void PolygonMeshUsesItsGridAndClosureFlags() => TestSupport.Run("POLYGON MESH: row/column topology and closures", () =>
    {
        var vertices = string.Concat(Enumerable.Range(0, 3).SelectMany(i => Enumerable.Range(0, 3).Select(j => Vertex(i * 3, j * 2, i + j, 64))));
        var open = Projected(Load(Header(16, 71, 3, 72, 3) + vertices + Entity("SEQEND")));
        Assert.Equal(12, open.Curves.Count);
        var closed = Projected(Load(Header(16 | 1 | 32, 71, 3, 72, 3) + vertices + Entity("SEQEND")));
        Assert.Equal(18, closed.Curves.Count);
        Assert.Contains(closed.Curves, c => c.Start == new Point(6, 4) && c.End == new Point(0, 4));
        Assert.Contains(closed.Curves, c => c.Start == new Point(6, 4) && c.End == new Point(6, 0));
    });

    [Theory]
    [InlineData("bad-index")] [InlineData("degenerate-face")] [InlineData("empty-polyface")] [InlineData("nan-z")]
    [InlineData("fitted")] [InlineData("short-grid")] [InlineData("huge-grid")] [InlineData("missing-end")]
    public void MalformedSequencesAreSkippedOnce(string failure) => TestSupport.Run("Projected polyline malformed: " + failure, () =>
    {
        var body = failure switch
        {
            "bad-index" => Header(64) + Vertex(0, 0, 0, 192) + Face(1, -999, 1) + Entity("SEQEND"),
            "degenerate-face" => Header(64) + Vertex(0, 0, 0, 192) + Vertex(1, 0, 0, 192) + Face(1, 2, 1) + Entity("SEQEND"),
            "empty-polyface" => Header(64) + Vertex(0, 0, 0, 192) + Entity("SEQEND"),
            "nan-z" => Header(8) + Entity("VERTEX", 10, 0, 20, 0, 30, "NaN", 70, 32) + Vertex(1, 1, 1) + Entity("SEQEND"),
            "short-grid" => Header(16, 71, 2, 72, 2) + Vertex(0, 0, 0, 64) + Entity("SEQEND"),
            "huge-grid" => Header(16, 71, int.MaxValue, 72, int.MaxValue) + Entity("SEQEND"),
            "fitted" => Header(8 | 4) + Vertex(0, 0, 0) + Vertex(1, 1, 1) + Entity("SEQEND"),
            _ => Header(8) + Vertex(0, 0, 0) + Vertex(1, 1, 1)
        };
        var doc = Load(body); Assert.Single(doc.Entities); Assert.Equal(1, Assert.Single(doc.Skipped).Value);
        Assert.DoesNotContain(doc.Skipped.Keys, k => k is "VERTEX" or "SEQEND");
    });

    [Fact]
    public void ProjectedBlockChildUsesExistingTransformsAndIdentity() => TestSupport.Run("Projected POLYLINE: INSERT transform and layer inheritance", () =>
    {
        var block = Entity("BLOCK", 2, "B", 10, 0, 20, 0) + Header(8).Replace("8\nDETAIL\n", "8\n0\n") +
            Vertex(1, 0, 7) + Vertex(4, 0, 9) + Entity("SEQEND") + Entity("ENDBLK");
        var doc = Load(Entity("INSERT", 5, "I", 2, "B", 8, "PLACED", 10, 10, 20, 20, 41, 2, 42, 2, 50, 90), block);
        var line = Projected(doc); Assert.Equal("I[0,0]/P", line.Handle); Assert.Equal("PLACED", line.Layer);
        TestSupport.Near(10, line.Curves[0].Start.X); TestSupport.Near(22, line.Curves[0].Start.Y);
        TestSupport.Near(28, line.Curves[0].End.Y); Assert.Same(line, doc.FindEntity(line.Id));
    });

    [Fact]
    public void PublicPolyfaceHasEdgesAndEmptyTopologyIsReported() => TestSupport.Run("uncommon.dxf: projected polyface topology", () =>
    {
        var doc = DxfReader.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Public", "uncommon.dxf"));
        var mesh = doc.Entities.Single(e => e.Handle == "4E9"); Assert.Equal("POLYLINE", mesh.Kind); Assert.NotEmpty(mesh.Curves);
        Assert.Equal(1, doc.Skipped["POLYFACE (no visible edges)"]); Assert.DoesNotContain(doc.Entities, e => e.Handle == "4D7");
        Assert.DoesNotContain("3D POLYLINE", doc.Skipped.Keys);
    });

    [Fact]
    public void ProjectedPolylineRendersAndSelectsUsingXY() => TestSupport.Run("Projected POLYLINE: GPU render, pick and XY distance", () => TestSupport.Sta(() =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var doc = Load(Header(8) + Vertex(0, 0, 0) + Vertex(3, 4, 12) + Entity("SEQEND") + Entity("POINT", 5, "Q", 10, 3, 20, 0));
        var view = new DxfViewport(); var window = new Window { Content = view, Width = 900, Height = 600, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); view.SetDocument(doc); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            void Pick(Point point) => typeof(DxfViewport).GetMethod("SelectAt", flags)!.Invoke(view, [point, false]);
            Pick(new(1.5, 2)); Assert.Equal("POLYLINE", Assert.Single(view.Selected).Entity.Kind);
            Pick(new(3, 0)); Assert.Equal(2, view.Selected.Count); TestSupport.Near(2.4, view.Measurement!.Value.Distance);
            var host = (SwapChainHost)typeof(DxfViewport).GetField("host", flags)!.GetValue(view)!;
            Assert.NotNull(host.Renderer); VisualReport.Capture(host.Renderer!, "3D polyline XY projection and distance", () => typeof(DxfViewport).GetMethod("RenderNow", flags)!.Invoke(view, null));
        }
        finally { window.Close(); view.ReleaseResources(); }
    }));
}
