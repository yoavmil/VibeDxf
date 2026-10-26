using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class WashbasinSolidChecks
{
    private readonly record struct Ptr(int Value);
    private readonly record struct Tag(byte Value);
    private static Ptr P(int n)=>new(n);
    private static byte[] SplineSolid(string mode="ordinary")
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
        void Text(string s){var b=Encoding.UTF8.GetBytes(s);w.Write((byte)b.Length);w.Write(b);}
        void Token(object x)
        {
            switch(x)
            {
                case Ptr p:w.Write((byte)12);w.Write(p.Value);break;
                case Tag t:w.Write(t.Value);break;
                case int n:w.Write((byte)4);w.Write(n);break;
                case double d:w.Write((byte)6);w.Write(d);break;
                case bool b:w.Write((byte)(b?10:11));break;
                case string s:w.Write((byte)13);Text(s);break;
                case Point3D p:w.Write((byte)19);w.Write(p.X);w.Write(p.Y);w.Write(p.Z);break;
                default:throw new Exception();
            }
        }
        void Node(string name,params object[] data)
        {w.Write((byte)13);Text(name);Token(P(-1));Token(-1);foreach(var x in data)Token(x);w.Write((byte)17);}
        w.Write(Encoding.ASCII.GetBytes("ACIS BinaryFile"));w.Write(23000);w.Write(13);w.Write(1);w.Write(0);
        foreach(var s in new[]{"test","ACIS","date"}){w.Write((byte)7);Text(s);}
        foreach(double d in new[]{1d,1e-6,1e-10})Token(d);
        bool tolerant=mode is "tolerant" or "underreported";bool reversed=mode=="reversed";double last=reversed?-1:1;
        Node("body",P(-1),P(1),P(-1),P(-1));Node("lump",P(-1),P(-1),P(2),P(0));
        Node("shell",P(-1),P(-1),P(-1),P(3),P(-1),P(1));
        Node("face",P(-1),P(-1),P(4),P(2),P(-1),P(12),false,false);
        Node("loop",P(-1),P(-1),P(5),P(3));
        Node("coedge",P(-1),P(5),P(5),P(-1),P(6),false,P(4),0,P(-1));
        Node("edge",P(-1),P(7),0d,P(8),last,P(5),P(11),false,"unknown");
        if(tolerant)Node("tvertex-vertex",P(-1),P(6),2,P(9),-1d,mode=="underreported"?.001:.02,mode=="underreported"?.001:.02);
        else Node("vertex",P(-1),P(6),2,P(9));
        Node("vertex",P(-1),P(6),2,P(10));
        Node("point",P(-1),new Point3D(0,tolerant?.01:0,mode=="endpoint"?1:0));
        Node("point",P(-1),new Point3D(3,0,6));
        var spline=new List<object>{P(-1),reversed,new Tag(15),"exact_int_cur",mode=="version"?20800:20900,0,"nubs",3,0,2,0d,3,1d,3};
        foreach(var p in new[]{new Point3D(0,0,0),new(0,3,2),new(3,3,4),new(3,0,6)})spline.AddRange(new object[]{p.X,p.Y,p.Z});
        spline.AddRange(new object[]{mode=="fit"?.001:0d,"null_surface","null_surface","nullbs","nullbs",false,false,0,0,0,0,true,mode=="mapping"?2d:1d,true,0d,0,0,new Tag(16),false,false});
        Node("intcurve-curve",spline.ToArray());Node("plane-surface",P(-1));
        w.Write((byte)13);Text("End-of-ACIS-data");w.Flush();return stream.ToArray();
    }
    private static DxfEntity Read(byte[] data)=>DxfSolidProjection.ReadEntity([],new (int,string)[]{(94,data.Length.ToString(CultureInfo.InvariantCulture)),(310,Convert.ToHexString(data))},"0","S");

    [Theory][InlineData("ordinary")][InlineData("reversed")][InlineData("tolerant")]
    public void ExplicitSplineKeepsItsLocus(string mode)=>TestSupport.Run("ACIS explicit XYZ spline boundary: "+mode,()=>
    {
        var e=Read(SplineSolid(mode));Assert.True(e.IsApproximate);Assert.True(e.Curves.Count>30);Assert.Empty(e.FillTriangles!);
        var lines=e.SpatialGeometry!.Lines;Assert.Equal(new Point3D(),lines[0].Start);Assert.Equal(new Point3D(3,0,6),lines[^1].End);
        Assert.Contains(lines,l=>l.Start.Y>2);Assert.Null(new MidpointIndex([e]).Find(e.Curves[0].At(.5),1));
        for(int i=0;i<=300;i++)
        {
            double t=i/300d;var p=new Point(9*t*t-6*t*t*t,9*t*(1-t));
            Assert.True(e.Curves.Min(c=>(c.Closest(p)-p).Length)<=SplineGeometry.Tolerance+1e-10);
        }
    });
    [Theory][InlineData("version")][InlineData("fit")][InlineData("mapping")][InlineData("endpoint")][InlineData("underreported")]
    public void UnsupportedSplineIsNeverAnEndpointChord(string mode)=>TestSupport.Run("ACIS explicit spline rejection: "+mode,()=>
    {
        if(mode is "endpoint" or "underreported")Assert.Throws<InvalidDataException>(()=>Read(SplineSolid(mode)));
        else Assert.Throws<NotSupportedException>(()=>Read(SplineSolid(mode)));
    });
    [Fact]
    public void RationalTrimAndPlacementHaveBoundedSpatialError()=>TestSupport.Run("XYZ rational spline trims, reverse traversal and placement retain a circle",()=>
    {
        var s=new DxfSpatialSpline(2,new Point3D[]{new(1,0,0),new(1,1,0),new(0,1,0)},new double[]{0,0,0,1,1,1},new[]{1d,Math.Sqrt(.5),1});
        Point3D Place(Point3D p)=>new(10+2*p.X,20,30+2*p.Y);
        var lines=s.Flatten(.8,.2,Place);Assert.Equal(Place(s.Evaluate(.8)),lines[0].Start);Assert.Equal(Place(s.Evaluate(.2)),lines[^1].End);
        Assert.All(lines,l=>{TestSupport.Near(4,Math.Pow(l.Start.X-10,2)+Math.Pow(l.Start.Z-30,2),1e-10);TestSupport.Near(20,l.Start.Y);});
        for(int i=0;i<=500;i++)
        {
            var p=Place(s.Evaluate(.2+.6*i/500));
            double Distance(DxfSpatialLine l){var d=l.End-l.Start;double t=Math.Clamp(Vector3D.DotProduct(p-l.Start,d)/d.LengthSquared,0,1);return (p-(l.Start+t*d)).Length;}
            Assert.True(lines.Min(Distance)<=SplineGeometry.Tolerance+1e-10);
        }
        Assert.Throws<InvalidDataException>(()=>s.Flatten(-.1,1,Place));
    });
    [Fact]
    public void SplineErrorRemainsBoundedAfterProjection()=>TestSupport.Run("ACIS spline projection magnification keeps the XY chord bound",()=>
    {
        var data=SplineSolid();var e=DxfSolidProjection.ReadEntity([],new (int,string)[]{(94,data.Length.ToString()),(310,Convert.ToHexString(data))},"0","S",p=>new(p.X*1000,p.Y*10));
        for(int i=0;i<=300;i++)
        {double t=i/300d;var p=new Point((9*t*t-6*t*t*t)*1000,90*t*(1-t));Assert.True(e.Curves.Min(c=>(c.Closest(p)-p).Length)<=SplineGeometry.Tolerance+1e-9);}
    });
    [Fact]
    public void MultipleBezierSpansPreserveArbitraryTrims()=>TestSupport.Run("XYZ spline knot insertion across multiple spans and immutable input",()=>
    {
        var points=new Point3D[]{new(0,0,0),new(0,3,2),new(3,3,4),new(3,0,6),new(3,-3,8),new(6,-3,10),new(6,0,12)};
        var s=new DxfSpatialSpline(3,points,new double[]{0,0,0,0,.5,.5,.5,1,1,1,1},Enumerable.Repeat(1d,7).ToArray());
        points[0]=new(100,100,100);Assert.Equal(new Point3D(),s.Evaluate(0));
        var lines=s.Flatten(.1,.9,p=>p);
        Assert.Equal(s.Evaluate(.1),lines[0].Start);Assert.Equal(s.Evaluate(.9),lines[^1].End);
        for(int i=0;i<=500;i++)
        {
            double t=.1+.8*i/500;double u=t<=.5?t*2:(t-.5)*2;double x=9*u*u-6*u*u*u+(t<=.5?0:3),y=9*u*(1-u)*(t<=.5?1:-1),z=12*t;
            var p=new Point3D(x,y,z);
            double Distance(DxfSpatialLine l){var d=l.End-l.Start;double f=Math.Clamp(Vector3D.DotProduct(p-l.Start,d)/d.LengthSquared,0,1);return (p-(l.Start+f*d)).Length;}
            Assert.True(lines.Min(Distance)<=SplineGeometry.Tolerance+1e-10);
        }
    });
    [Fact]
    public void InvalidWeightsAndCancellationAreRejected()=>TestSupport.Run("XYZ spline invalid rational weights and cancellation",()=>
    {
        var points=new Point3D[]{new(),new(1,1,1)};var knots=new double[]{0,0,1,1};
        Assert.Throws<InvalidDataException>(()=>new DxfSpatialSpline(1,points,knots,new[]{1d,0}));
        var s=new DxfSpatialSpline(1,points,knots,new[]{1d,1});using var cts=new CancellationTokenSource();cts.Cancel();LoadingContext.Token=cts.Token;
        try{Assert.Throws<OperationCanceledException>(()=>s.Flatten(0,1,p=>p));}
        finally{LoadingContext.Token=default;}
    });
    [Fact]
    public void ActualWashbasinLoadsAllThirteenSolids()=>TestSupport.Run("GPU actual Waschbecken: all 13 ACIS solids and saved camera",()=>TestSupport.Sta(()=>
    {
        using var archive=ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","Waschbecken-2018.zip"));
        using var source=archive.GetEntry("Waschbecken-2018.dxf")!.Open();using var bytes=new MemoryStream();source.CopyTo(bytes);
        Assert.Equal("99D2DC2F185FDA5FE27EAC8813E738D96E2C942D66E8CFA98C144145CFDB13D7",Convert.ToHexString(SHA256.HashData(bytes.ToArray())));bytes.Position=0;
        var doc=DxfReader.Load(bytes);Assert.Empty(doc.Skipped);Assert.Equal(13,doc.Entities.Count);
        Assert.Equal(Enumerable.Range(0x346,13).Select(n=>n.ToString("X")),doc.Entities.Select(e=>e.SourceHandle).Order());
        Assert.All(doc.Entities,e=>{Assert.Equal("3DSOLID",e.Kind);Assert.NotEmpty(e.Curves);Assert.NotEmpty(e.SpatialGeometry!.Lines);});
        Assert.NotNull(doc.DefaultModelView);Assert.True(doc.ModelViews[doc.DefaultModelView!.Value.Value].IsSupported,doc.UnsupportedWarning);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var view=new DxfViewport();var window=new Window{Content=view,Width=1200,Height=800,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Capture(string title)
        {
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var host=typeof(DxfViewport).GetField("host",flags)!.GetValue(view)!;var renderer=(DrawingRenderer)host.GetType().GetProperty("Renderer")!.GetValue(host)!;
            VisualReport.Capture(renderer,title,()=>typeof(DxfViewport).GetMethod("RenderNow",flags)!.Invoke(view,null));
        }
        try{window.Show();view.SetDocument(doc);view.ShowAll();view.Fit();Capture("Waschbecken all 13 solids original XY");view.RestoreSavedModelView();view.Fit();Capture("Waschbecken all 13 solids saved camera");}
        finally{window.Close();}
    }));
}


