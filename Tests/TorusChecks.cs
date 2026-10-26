using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class TorusChecks
{
    private static string Fixture=>Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","torus_r2000.dxf");
    private static (int Code,string Value)[] Fields(string sat)=>[(100,"AcDbModelerGeometry"),..sat.Split('\n').Select(s=>(1,s))];
    private static string Sat=>"400 0 1 0\n4 test 4 ACIS 4 date\n1 1e-6 1e-10\n"+
        "body $-1 $1 $-1 $-1 #\nlump $-1 $-1 $2 $0 #\nshell $-1 $-1 $-1 $3 $-1 $1 #\n"+
        "face $-1 $-1 $-1 $2 $-1 $4 forward single #\n"+
        "torus-surface $-1 0 0 0 0 0 1 32 10 1 0 0 forward_v I I I I #\nEnd-of-ACIS-data";
    [Fact]
    public void ActualLegacyTorusLoadsAsSurface()=>TestSupport.Run("ACIS 400 actual torus loads its edge-free surface, without invented query edges",()=>
    {
        var doc=DxfReader.Load(Fixture);Assert.Empty(doc.Skipped);var entity=Assert.Single(doc.Entities);
        Assert.Equal("3DSOLID",entity.Kind);Assert.Empty(entity.Curves);Assert.True(entity.IsApproximate);
        Assert.NotEmpty(entity.FillTriangles!);Assert.NotEmpty(entity.SpatialGeometry!.Triangles);Assert.Empty(entity.SpatialGeometry.Lines);
        TestSupport.Near(86,entity.Bounds.Left,1e-8);TestSupport.Near(93,entity.Bounds.Top,1e-8);TestSupport.Near(84,entity.Bounds.Width,1e-8);TestSupport.Near(84,entity.Bounds.Height,1e-8);
        Assert.Equal(MeasurementStatus.NoGeometry,doc.Measure(new(entity.Id),new(entity.Id)).Status);
        Assert.Null(new MidpointIndex(doc.Entities).Find(new(128,135),100));
        Assert.Contains(doc.RenderingWarnings.Keys,k=>k.Contains("display only"));
    });
    [Fact]
    public void SurfaceVerticesFollowAnalyticTorus()=>TestSupport.Run("ACIS torus tessellation follows its analytic surface and XYZ body placement",()=>
    {
        var entity=DxfSolidProjection.ReadEntity(Fields(Sat),null,"0","T");var geometry=entity.SpatialGeometry!;
        Assert.InRange(geometry.Triangles.Count,100,200000);
        foreach(var triangle in geometry.Triangles)
            foreach(var p in new[]{triangle.A,triangle.B,triangle.C})
            {
                double radial=Math.Sqrt(p.X*p.X+p.Y*p.Y);
                TestSupport.Near(100,(radial-32)*(radial-32)+p.Z*p.Z,1e-8);
            }
        // The coarse projected fill must retain the central hole.
        Assert.True(entity.FillTriangles!.All(p=>(p-new Point()).Length>21.9));
    });
    [Theory]
    [InlineData("version")][InlineData("radii")][InlineData("axis")][InlineData("reference")]
    [InlineData("trimmed")][InlineData("pointer")][InlineData("huge")][InlineData("truncated")]
    public void InvalidOrUnsupportedSurfaceIsRejected(string mode)=>TestSupport.Run("ACIS torus validation: "+mode,()=>
    {
        string sat=mode switch
        {
            "version"=>Sat.Replace("400 0 1 0","399 0 1 0"),
            "radii"=>Sat.Replace("32 10 1 0 0","10 32 1 0 0"),
            "axis"=>Sat.Replace("0 0 1 32","0 0 0 32"),
            "reference"=>Sat.Replace("32 10 1 0 0","32 10 0 0 1"),
            "trimmed"=>Sat.Replace("forward_v I I I I","forward_v 0 1 I I"),
            "pointer"=>Sat.Replace("$-1 $4 forward","$-1 $999 forward"),
            "huge"=>Sat.Replace("32 10 1 0 0","320000000 10 1 0 0"),
            _=>Sat.Replace("End-of-ACIS-data","")
        };
        if(mode is "version" or "trimmed" or "huge")Assert.Throws<NotSupportedException>(()=>DxfSolidProjection.ReadEntity(Fields(sat),null,"0","T"));
        else Assert.Throws<InvalidDataException>(()=>DxfSolidProjection.ReadEntity(Fields(sat),null,"0","T"));
    });
    [Fact]
    public void LegacySatEdgesUseTheirVersionedLayout()=>TestSupport.Run("ACIS 400 edge topology uses attribute-only prefixes and absent pattern fields",()=>
    {
        string sat="400 0 1 0\n4 test 4 ACIS 4 date\n1 1e-6 1e-10\n"+string.Join('\n',new[]{
            "body $-1 $1 $-1 $-1 #","lump $-1 $-1 $2 $0 #","shell $-1 $-1 $-1 $3 $-1 $1 #",
            "face $-1 $-1 $4 $2 $-1 $20 forward single #","loop $-1 $-1 $5 $3 #",
            "coedge $-1 $6 $7 $-1 $8 forward $4 $-1 #","coedge $-1 $7 $5 $-1 $9 forward $4 $-1 #","coedge $-1 $5 $6 $-1 $10 forward $4 $-1 #",
            "edge $-1 $11 0 $12 3 $5 $17 forward #","edge $-1 $12 0 $13 5 $6 $18 forward #","edge $-1 $13 0 $11 4 $7 $19 forward #",
            "vertex $-1 $8 $14 #","vertex $-1 $8 $15 #","vertex $-1 $9 $16 #",
            "point $-1 0 0 0 #","point $-1 3 0 0 #","point $-1 0 4 0 #",
            "straight-curve $-1 0 0 0 1 0 0 I I #","straight-curve $-1 3 0 0 -0.6 0.8 0 I I #","straight-curve $-1 0 4 0 0 -1 0 I I #",
            "plane-surface $-1 0 0 0 0 0 1 1 0 0 forward I I I I #","End-of-ACIS-data"});
        var entity=DxfSolidProjection.ReadEntity(Fields(sat),null,"0","L");Assert.Equal(3,entity.Curves.Count);
        Assert.Equal(new Rect(0,0,3,4),entity.Bounds);Assert.Equal(3,entity.SpatialGeometry!.Lines.Count);Assert.Empty(entity.SpatialGeometry.Triangles);
    });    [Fact]
    public void SurfacePlacementAndSideProjection()=>TestSupport.Run("ACIS torus surface triangles survive placement and orthographic side projection",()=>
    {
        var entity=DxfSolidProjection.ReadEntity(Fields(Sat),null,"0","T",p=>new(p.X,p.Z));
        TestSupport.Near(84,entity.Bounds.Width,1e-8);Assert.InRange(entity.Bounds.Height,19.98,20.01);
        var placed=DxfPrimitives.Transform(entity,new System.Windows.Media.Matrix(2,0,0,3,10,20),"PLACED","I/T");
        TestSupport.Near(168,placed.Bounds.Width,1e-8);Assert.NotEmpty(placed.FillTriangles!);
        Assert.Equal(entity.FillTriangles!.Count,placed.FillTriangles!.Count);Assert.Empty(placed.Curves);
        Assert.NotEmpty(placed.SpatialGeometry!.Triangles);
        var mixed=new DxfEntity("3DSOLID","0","M",[Curve.Line(default,new(1,0))]){FillTriangles=entity.FillTriangles,Bounds=entity.Bounds};
        var mixedPlaced=DxfPrimitives.Transform(mixed,new System.Windows.Media.Matrix(2,0,0,3,10,20),"0","M");
        TestSupport.Near(168,mixedPlaced.Bounds.Width,1e-8);
    });    [Fact]
    public void TessellationHonorsCancellation()=>TestSupport.Run("ACIS torus load honors cancellation",()=>
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();LoadingContext.Token=cancel.Token;
        try{Assert.Throws<OperationCanceledException>(()=>DxfSolidProjection.ReadEntity(Fields(Sat),null,"0","T"));}
        finally{LoadingContext.Token=default;}
    });
    [Fact]
    public void SurfaceDisplayKeepsHoleAndRejectsPicking()=>TestSupport.Run("GPU actual torus surface retains central hole and is display-only",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object o,string n)=>o.GetType().GetField(n,flags)!.GetValue(o);
        object? Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,flags)!.Invoke(o,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=800,Height=700,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();view.SetDocument(DxfReader.Load(Fixture));view.ShowAll();Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"Actual ACIS 400 torus: projected surface and central hole",()=>Call(view,"RenderNow"));
            bool Light(byte r,byte g,byte b)=>r>100&&g>100&&b>100;
            bool At(Point p){var s=(Point)Call(view,"Screen",p)!;return frame.Near((int)(s.X*frame.Width/view.ActualWidth),(int)(s.Y*frame.Height/view.ActualHeight),Light);}
            Assert.True(At(new(160,135)));Assert.False(At(new(128,135)));
            Call(view,"SelectAt",new Point(160,135),false);Assert.Empty(view.Selected);
        }
        finally{window.Close();Pump();}
    }));
}
