using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class MeshProjectionChecks
{
    private const string Mesh = "0\nMESH\n5\nM1\n8\n0\n100\nAcDbEntity\n100\nAcDbSubDMesh\n71\n2\n72\n0\n91\n0\n92\n4\n10\n0\n20\n0\n30\n100\n10\n4\n20\n0\n30\n0\n10\n4\n20\n3\n30\n-5\n10\n0\n20\n3\n30\n0\n93\n5\n90\n4\n90\n0\n90\n1\n90\n2\n90\n3\n94\n0\n95\n0\n90\n0\n";
    private const string PointEntity = "0\nPOINT\n5\nP\n10\n2\n20\n-2\n";
    private static DxfDocument Load(string entities, string blocks = "")
    {
        var text = "0\nSECTION\n2\nBLOCKS\n" + blocks + "0\nENDSEC\n0\nSECTION\n2\nENTITIES\n" + entities + "0\nENDSEC\n0\nEOF\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return DxfReader.Load(input);
    }
    [Fact]
    public void ProjectedEdgesAreSelectableAndMeasureInXY() => TestSupport.Run("MESH: exact XY wireframe, midpoint and planar distance", () =>
    {
        var doc = Load(Mesh + PointEntity); var mesh = doc.Entities.Single(e => e.Kind == "MESH");
        Assert.Empty(doc.Skipped); Assert.Equal(4, mesh.Curves.Count); Assert.Empty(mesh.FillContours);
        Assert.False(mesh.IsApproximate); Assert.False(mesh.Contains(new(2, 1)));
        TestSupport.Near(0, mesh.Bounds.Left); TestSupport.Near(4, mesh.Bounds.Right);
        TestSupport.Near(0, mesh.Bounds.Top); TestSupport.Near(3, mesh.Bounds.Bottom);
        TestSupport.Near(14, mesh.Curves.Sum(c => (c.End - c.Start).Length));
        Assert.Equal(mesh, new MidpointIndex([mesh]).Find(new(2, 0), 1e-8)!.Entity);
        var result = doc.Measure(new(mesh.Id), new(doc.Entities.Single(e => e.Kind == "POINT").Id));
        Assert.Equal(MeasurementStatus.Success, result.Status); TestSupport.Near(2, result.Distance!.Value);
        Assert.Contains(doc.RenderingWarnings.Keys, text => text.Contains("measurements ignore Z"));
    });
    [Fact]
    public void BlockPlacementAndSourceMetadataArePreserved() => TestSupport.Run("MESH: nested placement, effective layer and source color", () =>
    {
        var block = "0\nBLOCK\n2\nB\n10\n0\n20\n0\n" + Mesh + "0\nENDBLK\n";
        var doc = Load("0\nINSERT\n5\nI\n2\nB\n8\nDETAIL\n62\n1\n10\n10\n20\n20\n41\n2\n42\n2\n50\n90\n", block);
        var mesh = Assert.Single(doc.Entities); Assert.Equal("I[0,0]/M1", mesh.Handle); Assert.Equal("M1", mesh.SourceHandle);
        Assert.Equal("DETAIL", mesh.Layer); TestSupport.Near(4, mesh.Bounds.Left); TestSupport.Near(10, mesh.Bounds.Right);
        TestSupport.Near(20, mesh.Bounds.Top); TestSupport.Near(28, mesh.Bounds.Bottom);
    });
    [Fact]
    public void CreaseEdgesAreDeduplicatedAndCollapsedEdgesRemainMeasurable() => TestSupport.Run("MESH: explicit crease topology and collapsed projected edge", () =>
    {
        var edges = Mesh.Replace("94\n0\n95\n0\n", "94\n2\n90\n0\n90\n1\n90\n1\n90\n0\n95\n2\n140\n0\n140\n-1\n");
        Assert.Equal(4, Load(edges).Entities.Single().Curves.Count);
        var vertical = Mesh.Replace("10\n4\n20\n0\n30\n0", "10\n0\n20\n0\n30\n0");
        var mesh = Load(vertical).Entities.Single(); Assert.Contains(mesh.Curves, c => c.Start == c.End); Assert.Contains(new Point(0,0),mesh.RenderDots);
        Assert.True(mesh.Curves.All(c => double.IsFinite(c.Bounds.Left)));
    });
    [Fact]
    public void SubdivisionReportsControlCageApproximation() => TestSupport.Run("MESH: subdivision smoothing is an explicit control-cage approximation", () =>
    {
        var doc = Load(Mesh.Replace("91\n0", "91\n2")); var mesh = Assert.Single(doc.Entities);
        Assert.True(mesh.IsApproximate); Assert.Null(new MidpointIndex([mesh]).Find(new(2,0),1e-8));
        Assert.Contains(doc.RenderingWarnings.Keys, s => s.Contains("subdivision smoothing"));
    });
    [Theory]
    [InlineData("92\n4", "92\n1000001")]
    [InlineData("10\n4\n20\n3", "10\nNaN\n20\n3")]
    [InlineData("30\n100", "30\nInfinity")]
    [InlineData("93\n5", "93\n6")]
    [InlineData("90\n3\n94", "90\n4\n94")]
    [InlineData("90\n3\n94", "90\n-1\n94")]
    [InlineData("90\n4\n90\n0", "90\n2\n90\n0")]
    [InlineData("94\n0\n95\n0", "94\n1\n90\n0\n90\n4\n95\n0")]
    [InlineData("94\n0\n95\n0", "94\n1\n90\n0\n90\n1\n95\n2\n140\n0\n140\n0")]
    public void MalformedTopologyIsSkippedWithoutLosingOtherGeometry(string oldValue, string newValue) => TestSupport.Run("MESH malformed: " + newValue.Replace('\n','/'), () =>
    {
        var doc = Load(Mesh.Replace(oldValue, newValue) + PointEntity);
        Assert.Equal("POINT", Assert.Single(doc.Entities).Kind); Assert.Equal(1, doc.Skipped["MESH (invalid topology)"]);
    });
    [Fact]
    public void PublicMeshIsLoadedAndProjected() => TestSupport.Run("MESH: uncommon.dxf 4CB projection", () =>
    {
        var doc = DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        var mesh = doc.Entities.Single(e => e.Handle == "4CB"); Assert.Equal("MESH",mesh.Kind);
        Assert.Equal(108,mesh.Curves.Count); Assert.False(mesh.IsApproximate); Assert.Empty(mesh.FillContours); Assert.NotEmpty(mesh.RenderDots);
        TestSupport.Near(33.60505098766978,mesh.Bounds.Left); TestSupport.Near(40.49726999266695,mesh.Bounds.Right);
        TestSupport.Near(21.81551669891939,mesh.Bounds.Top); TestSupport.Near(27.37254596790581,mesh.Bounds.Bottom);
        Assert.DoesNotContain(doc.Skipped.Keys,s => s.StartsWith("MESH"));
    });
    [Fact]
    public void MeshRendersAndCanBePicked() => TestSupport.Run("GPU: projected MESH wireframe rendering and picking", () => TestSupport.Sta(() =>
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(object o,string name) => o.GetType().GetField(name,flags)!.GetValue(o);
        object? Call(object o,string name,params object[] args) => o.GetType().GetMethod(name,flags)!.Invoke(o,args);
        var view = new DxfViewport(); var window = new Window {Content=view,Width=800,Height=600,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show(); view.SetDocument(Load(Mesh+PointEntity)); Pump();
            var renderer = (DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame = VisualReport.Capture(renderer,"XY-projected MESH wireframe",() => Call(view,"RenderNow"));
            var screen = (Point)Call(view,"Screen",new Point(2,0))!;
            Assert.True(frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),(r,g,b)=>r>120&&g>120&&b>120));
            Call(view,"SelectAt",new Point(1,0),false); Assert.Equal("MESH",Assert.Single(view.Selected).Entity.Kind);
            Call(view,"SelectAt",new Point(2,-2),false); TestSupport.Near(2,view.Measurement!.Value.Distance);
        }
        finally {window.Close();Pump();}
    }));
}
