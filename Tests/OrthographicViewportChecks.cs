using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class OrthographicViewportChecks
{
    private const string Lines="0\nLINE\n5\nZ\n8\n0\n10\n0\n20\n0\n30\n0\n11\n0\n21\n0\n31\n4\n0\nLINE\n5\nELEV\n10\n0\n20\n0\n30\n1\n11\n4\n21\n0\n31\n1\n";
    private static string Source(string model=Lines,string direction="0\n26\n-1\n36\n0",string flags="0",string extra="",string blocks="",double viewHeight=20)=>
        blocks+"0\nSECTION\n2\nENTITIES\n"+model+"0\nVIEWPORT\n5\nSHEET\n67\n1\n69\n1\n0\nVIEWPORT\n5\nISO\n67\n1\n69\n2\n68\n1\n10\n0\n20\n0\n40\n20\n41\n20\n45\n"+viewHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\n16\n"+direction+"\n90\n"+flags+"\n"+extra+"0\nLINE\n5\nPAPER\n67\n1\n10\n-12\n20\n-12\n11\n12\n21\n-12\n0\nENDSEC\n0\nEOF\n";
    private static DxfDocument Load(string source){using var stream=new MemoryStream(Encoding.UTF8.GetBytes(source));return DxfReader.Load(stream);}
    private static DxfViewportWindow Window(DxfDocument doc)=>Assert.Single(Assert.Single(doc.Layouts).Viewports);
    private static void Near(Point expected,Point actual){TestSupport.Near(expected.X,actual.X,1e-8);TestSupport.Near(expected.Y,actual.Y,1e-8);}

    [Fact]
    public void DirectionTargetAndTwist()=>TestSupport.Run("Orthographic DCS direction, target, twist and side/back views",()=>
    {
        var project=DxfOrthographicProjection.Create(new(-1,-1,1),new(10,20,30));
        Near(default,project(new(10,20,30)));Near(new(1/Math.Sqrt(2),1/Math.Sqrt(6)),project(new(11,20,30)));
        Near(new(0,2/Math.Sqrt(6)),project(new(10,20,31)));
        var back=DxfOrthographicProjection.Create(new(0,0,-1),default);Near(new(-2,3),back(new(2,3,5)));
        var doc=Load(Source(extra:"17\n10\n27\n20\n37\n30\n12\n2\n22\n3\n51\n90\n"));
        var window=Window(doc);Near(new(-2,-3),window.ModelToPaper.Transform(window.ProjectPoint3D!(new(10,20,30))));
        Near(new(-3,-3),window.ModelToPaper.Transform(window.ProjectPoint3D!(new(10,20,31))));
        Assert.True(window.IsOrthographic3D);Assert.False(window.IsInteractive);
    });
    [Fact]
    public void ActualXYZAndPlanarElevations()=>TestSupport.Run("Orthographic snapshots preserve LINE Z and planar circle elevation",()=>
    {
        var doc=Load(Source(Lines+"0\nCIRCLE\n5\nC\n10\n0\n20\n0\n30\n7\n40\n3\n"));var window=Window(doc);
        var z=window.ProjectedEntities!.Single(e=>e.Handle=="Z");Near(default,z.Curves[0].Start);Near(new(0,4),z.Curves[0].End);
        var elevated=window.ProjectedEntities!.Single(e=>e.Handle=="ELEV");Near(new(0,1),elevated.Curves[0].Start);Near(new(4,1),elevated.Curves[0].End);
        var circle=window.ProjectedEntities!.Single(e=>e.Handle=="C");Assert.All(circle.Curves,c=>{TestSupport.Near(7,c.Start.Y);TestSupport.Near(7,c.End.Y);});
        Assert.All(doc.Entities,e=>Assert.Null(e.Projection3D));
        Assert.Equal(doc.Entities.Select(e=>e.Group),window.ProjectedEntities!.Select(e=>e.Group));
    });
    [Fact]
    public void ReversedOcsElevationAndInsert()=>TestSupport.Run("Orthographic reversed OCS elevation and INSERT Z placement",()=>
    {
        var doc=Load(Source("0\nCIRCLE\n5\nC\n10\n0\n20\n0\n30\n7\n40\n3\n230\n-1\n"));
        Assert.All(Window(doc).ProjectedEntities!.Single().Curves,c=>TestSupport.Near(-7,c.Start.Y));
        string blocks="0\nSECTION\n2\nBLOCKS\n0\nBLOCK\n2\nB\n"+Lines+"0\nENDBLK\n0\nENDSEC\n";
        doc=Load(Source("0\nINSERT\n5\nI\n2\nB\n30\n10\n43\n2\n230\n-1\n",blocks:blocks,viewHeight:40));
        var z=Window(doc).ProjectedEntities!.Single(e=>e.Handle.EndsWith("/Z"));Near(new(0,-10),z.Curves[0].Start);Near(new(0,-18),z.Curves[0].End);
    });
    [Fact]
    public void RayAlongViewDirectionCollapsesSafely()=>TestSupport.Run("Orthographic ray parallel to view becomes a point marker",()=>
    {
        var doc=Load(Source("0\nRAY\n5\nR\n10\n1\n20\n2\n30\n3\n11\n0\n21\n-1\n31\n0\n"));
        var ray=Window(doc).ProjectedEntities!.Single();Assert.False(ray.IsUnbounded);Near(new(1,3),Assert.Single(ray.RenderDots));
    });
    [Fact]
    public void MeshAndPolylineXYZ()=>TestSupport.Run("Orthographic MESH and 3D POLYLINE snapshots retain actual vertex Z",()=>
    {
        string poly="0\nPOLYLINE\n5\nP\n70\n8\n0\nVERTEX\n70\n32\n10\n0\n20\n0\n30\n0\n0\nVERTEX\n70\n32\n10\n0\n20\n0\n30\n5\n0\nSEQEND\n";
        string mesh="0\nMESH\n5\nM\n91\n0\n92\n3\n10\n0\n20\n0\n30\n0\n10\n1\n20\n0\n30\n2\n10\n0\n20\n1\n30\n4\n93\n4\n90\n3\n90\n0\n90\n1\n90\n2\n94\n0\n95\n0\n90\n0\n";
        var entities=Window(Load(Source(poly+mesh))).ProjectedEntities!;
        Near(new(0,5),entities.Single(e=>e.Handle=="P").Curves[0].End);
        Assert.Contains(entities.Single(e=>e.Handle=="M").Curves,c=>c.Start==new Point(0,0)&&c.End==new Point(1,2));
    });
    [Fact]
    public void NestedInsertZ()=>TestSupport.Run("Orthographic nested INSERT Z scale, base point and placement",()=>
    {
        string blocks="0\nSECTION\n2\nBLOCKS\n0\nBLOCK\n2\nB\n30\n2\n"+Lines+"0\nENDBLK\n0\nBLOCK\n2\nA\n0\nINSERT\n5\nINNER\n2\nB\n30\n10\n43\n3\n0\nENDBLK\n0\nENDSEC\n";
        var doc=Load(Source("0\nINSERT\n5\nOUTER\n2\nA\n30\n100\n43\n2\n",blocks:blocks,viewHeight:400));
        var z=Window(doc).ProjectedEntities!.Single(e=>e.Handle.EndsWith("/Z"));Near(new(0,108),z.Curves[0].Start);Near(new(0,132),z.Curves[0].End);
    });
    [Theory]
    [InlineData("1","0\n26\n-1\n36\n0","VIEWPORT (perspective view)")]
    [InlineData("2","0\n26\n-1\n36\n0","VIEWPORT (depth clipping)")]
    [InlineData("4","0\n26\n-1\n36\n0","VIEWPORT (depth clipping)")]
    [InlineData("0","0\n26\n0\n36\n0","VIEWPORT (invalid direction)")]
    [InlineData("0","NaN\n26\n0\n36\n1","VIEWPORT (invalid view)")]
    public void ExplicitUnsupportedModes(string flags,string direction,string diagnostic)=>TestSupport.Run("Orthographic viewport explicit diagnostic: "+diagnostic+flags,()=>
    {
        var doc=Load(Source(flags:flags,direction:direction));
        if(flags!="0"){Assert.Single(doc.Layouts.Single().Viewports);Assert.Empty(doc.Skipped);Assert.False(doc.Layouts.Single().Viewports[0].IsInteractive);}
        else {Assert.Empty(doc.Layouts.Single().Viewports);Assert.Equal(1,doc.Skipped[diagnostic]);}
    });
    [Fact]
    public void PublicIsometricViewport()=>TestSupport.Run("api-cw750 Sheet4 viewport786B uses source isometric direction and scale",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        var window=doc.Layouts.Single(l=>l.Name=="Sheet 4").Viewports.Single(v=>v.Handle=="786B");
        Assert.True(window.IsOrthographic3D);Assert.NotEmpty(window.ProjectedEntities!);Assert.Empty(doc.Skipped);Assert.Equal(3,window.ProjectedEntities!.Count(e=>e.Kind=="3DSOLID"));
        var point=window.ProjectPoint3D!(new(0,0,1));Near(new(0,2/Math.Sqrt(6)),point);
        TestSupport.Near(3.393114570734088/6.786228175805926,window.ModelToPaper.M11);TestSupport.Near(3.393114570734088/6.786228175805926,window.ModelToPaper.M22);
    });
    [Fact]
    public void RenderVisibilityAndNoFalsePicking()=>TestSupport.Run("GPU orthographic XYZ snapshot, visibility, buffer reuse and no false XY picking",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object o,string n)=>o.GetType().GetField(n,flags)!.GetValue(o);
        object? Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,flags)!.Invoke(o,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=800,Height=700,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();var doc=Load(Source());view.SetDocument(doc);view.ActiveLayout=0;Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;var buffer=Field(renderer,"lineBuffer");
            var frame=VisualReport.Capture(renderer,"Orthographic side view: source vertical Z line appears upright; elevated X line lies at z=1",()=>Call(view,"RenderNow"));
            var pixel=(Point)Call(view,"Screen",new Point(0,2))!;
            bool Light(byte r,byte g,byte b)=>r>100&&g>100&&b>100;
            Assert.True(frame.Near((int)(pixel.X*frame.Width/view.ActualWidth),(int)(pixel.Y*frame.Height/view.ActualHeight),Light));
            Call(view,"SelectAt",new Point(0,2),false);Assert.Empty(view.Selected);Assert.Null(Call(view,"HoverAt",new Point(0,2)));
            view.SetLayerVisible(0,false);Pump();var hidden=VisualReport.Capture(renderer,"Orthographic geometry respects live layer visibility",()=>Call(view,"RenderNow"));
            Assert.False(hidden.Near((int)(pixel.X*hidden.Width/view.ActualWidth),(int)(pixel.Y*hidden.Height/view.ActualHeight),Light));
            view.ShowAll();view.ActiveLayout=-1;view.ActiveLayout=0;Pump();Assert.Same(buffer,Field(renderer,"lineBuffer"));
            using var api=new DxfView();LibraryApiChecks.Await(api.SetDocumentAsync(doc));api.ActiveLayout=doc.Layouts[0].Id;
            var frameId=Window(doc).FrameEntityId;
            Assert.Throws<NotSupportedException>(()=>api.WorldToScreen(new(0,2),frameId));
            Assert.Throws<NotSupportedException>(()=>api.ScreenToWorld(new(0,2),frameId));
            Assert.Throws<NotSupportedException>(()=>api.Select(new(doc.Entities[0].Id),frameId));
        }
        finally{window.Close();Pump();}
    }));
}
