using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class CurvedSolidChecks
{
    private static string F(double v)=>v.ToString("R",CultureInfo.InvariantCulture);
    private static string V(Point3D p)=>$"{F(p.X)} {F(p.Y)} {F(p.Z)}";
    private static (int Code,string Value)[] Ellipse(double ratio=1,double first=0,double last=Math.Tau,
        bool tilted=false,bool transform=false)
    {
        Point3D Eval(double t)=>tilted?new(3*Math.Cos(t),0,3*ratio*Math.Sin(t)):new(3*Math.Cos(t),3*ratio*Math.Sin(t),0);
        string[] sat={"700 0 1 0","4 test 4 ACIS 4 date","1 1e-6 1e-10",
            $"body $-1 -1 $-1 $1 $-1 ${(transform?13:-1)} #",
            "lump $-1 -1 $-1 $-1 $2 $0 #","shell $-1 -1 $-1 $-1 $-1 $3 $-1 $1 #",
            "face $-1 -1 $-1 $-1 $4 $2 $-1 $12 forward single #",
            "loop $-1 -1 $-1 $-1 $5 $3 #","coedge $-1 -1 $-1 $5 $5 $-1 $6 forward $4 $-1 #",
            $"edge $-1 -1 $-1 $7 {F(first)} $8 {F(last)} $5 $11 forward @7 unknown #",
            "vertex $-1 -1 $-1 $6 $9 #","vertex $-1 -1 $-1 $6 $10 #",
            $"point $-1 -1 $-1 {V(Eval(first))} #",$"point $-1 -1 $-1 {V(Eval(last))} #",
            $"ellipse-curve $-1 -1 $-1 0 0 0 {(tilted?"0 -1 0":"0 0 1")} 3 0 0 {F(ratio)} I I #",
            "plane-surface $-1 -1 $-1 0 0 0 0 0 1 1 0 0 forward I I I I #",
            "transform $-1 -1 0 0 -1 0 1 0 1 0 0 10 20 30 1 rotate no_reflect no_shear #",
            "End-of-ACIS-data"};
        var fields=new List<(int,string)>{(100,"AcDbModelerGeometry")};fields.AddRange(sat.Select(s=>(1,s)));return fields.ToArray();
    }
    private static DxfDocument Document((int Code,string Value)[] fields)
    {
        string dxf="0\nSECTION\n2\nENTITIES\n0\n3DSOLID\n5\nE\n"+string.Concat(fields.Select(p=>$"{p.Code}\n{p.Value}\n"))+"0\nPOINT\n5\nP\n10\n5\n20\n0\n0\nENDSEC\n0\nEOF\n";
        using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(dxf));return DxfReader.Load(stream);
    }
    [Fact]
    public void CircularBoundaryIsAnalyticAndMeasurable()=>TestSupport.Run("3DSOLID circular boundary remains analytic",()=>
    {
        var doc=Document(Ellipse());Assert.Empty(doc.Skipped);var solid=doc.Entities.Single(e=>e.Kind=="3DSOLID");
        var c=Assert.Single(solid.Curves);Assert.False(c.IsLine);Assert.False(solid.IsApproximate);
        TestSupport.Near(3,c.Radius);TestSupport.Near(Math.Tau,c.Sweep);Assert.Equal(new Rect(-3,-3,6,6),solid.Bounds);
        TestSupport.Near(2,solid.Distance(doc.Entities.Single(e=>e.Kind=="POINT")).Distance);
        Assert.Empty(solid.FillContours);Assert.Null(new MidpointIndex([solid]).Find(new(0,0),10));
    });
    [Theory]
    [InlineData(0,1.5707963267948966)]
    [InlineData(1.5707963267948966,0)]
    [InlineData(5.5,6.7)]
    public void TrimmedCircularEdgesKeepParameterDirection(double first,double last)=>TestSupport.Run("3DSOLID trimmed circular parameters "+first+"/"+last,()=>
    {
        var curves=DxfSolidProjection.Read(Ellipse(first:first,last:last),null,out bool approximate);
        var c=Assert.Single(curves);Assert.False(approximate);TestSupport.Near(last-first,c.Sweep);
        TestSupport.Near(3*Math.Cos(first),c.Start.X);TestSupport.Near(3*Math.Sin(first),c.Start.Y);
        TestSupport.Near(3*Math.Cos(last),c.End.X);TestSupport.Near(3*Math.Sin(last),c.End.Y);
    });
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void EllipticalAndEdgeOnCurvesHaveBoundedError(bool tilted)=>TestSupport.Run("3DSOLID bounded projected ellipse, edge-on="+tilted,()=>
    {
        var fields=Ellipse(ratio:0.5,tilted:tilted);var doc=Document(fields);var solid=doc.Entities.Single(e=>e.Kind=="3DSOLID");
        Assert.True(solid.IsApproximate);Assert.True(solid.Curves.Count>10);Assert.All(solid.Curves,c=>Assert.True(c.IsLine));
        Assert.Null(new MidpointIndex([solid]).Find(solid.Curves[0].At(0.5),1e-8));
        Assert.Contains(doc.RenderingWarnings.Keys,n=>n.Contains("0.0001"));
        for(int i=0;i<=1000;i++)
        {
            double t=Math.Tau*i/1000;var p=new Point(3*Math.Cos(t),tilted?0:1.5*Math.Sin(t));
            Assert.True(solid.Curves.Min(c=>(c.Closest(p)-p).Length)<=SplineGeometry.Tolerance+1e-10);
        }
        // A closed edge must not collapse to its coincident endpoint.
        Assert.True(solid.Bounds.Width>5.99);
    });
    [Fact]
    public void FullBodyTransformPrecedesCustomProjection()=>TestSupport.Run("3DSOLID curved XYZ body transform precedes view projection",()=>
    {
        var curves=DxfSolidProjection.ReadProjected(Ellipse(transform:true),null,p=>new Point(p.X+p.Z,p.Y),out bool approximate);
        Assert.False(approximate);var arc=Assert.Single(curves);Assert.False(arc.IsLine);
        TestSupport.Near(40,arc.Center.X);TestSupport.Near(20,arc.Center.Y);TestSupport.Near(3,arc.Radius);
    });
    [Theory]
    [InlineData("ratio")][InlineData("nan-ratio")][InlineData("normal")][InlineData("offcurve")][InlineData("multiturn")][InlineData("limit")]
    public void InvalidCurvesNeverPartiallyLoad(string mode)=>TestSupport.Run("3DSOLID curved validation: "+mode,()=>
    {
        var fields=mode switch
        {
            "multiturn"=>Ellipse(last:Math.Tau*2),
            "limit"=>Ellipse(ratio:0.5),
            _=>Ellipse()
        };
        if(mode=="ratio")fields=fields.Select(p=>(p.Code,p.Value.StartsWith("ellipse-curve")?p.Value.Replace("3 0 0 1 I I","3 0 0 -1 I I"):p.Value)).ToArray();
        if(mode=="nan-ratio")fields=fields.Select(p=>(p.Code,p.Value.StartsWith("ellipse-curve")?p.Value.Replace("3 0 0 1 I I","3 0 0 NaN I I"):p.Value)).ToArray();
        if(mode=="normal")fields=fields.Select(p=>(p.Code,p.Value.StartsWith("ellipse-curve")?p.Value.Replace("0 0 1 3 0 0","1 0 0 3 0 0"):p.Value)).ToArray();
        if(mode=="offcurve")fields=fields.Select(p=>(p.Code,p.Value.StartsWith("point ")?p.Value.Replace("3 0 0 #","30 0 0 #"):p.Value)).ToArray();
        if(mode=="limit")Assert.Throws<NotSupportedException>(()=>DxfSolidProjection.ReadProjected(fields,null,p=>new(p.X*1e15,p.Y*1e15),out _));
        else Assert.Throws<InvalidDataException>(()=>DxfSolidProjection.Read(fields,null));
    });
    [Fact]
    public void SameVertexClosedEdgeRetainsWholeCircle()=>TestSupport.Run("3DSOLID closed edge shares one vertex and keeps 2pi range",()=>
    {
        var fields=Ellipse().Select(p=>(p.Code,p.Value.StartsWith("edge ")?p.Value.Replace("$8 ","$7 "):p.Value)).ToArray();
        var curve=Assert.Single(DxfSolidProjection.Read(fields,null));TestSupport.Near(Math.Tau,curve.Sweep);TestSupport.Near(6,curve.Bounds.Width);
    });
    [Fact]
    public void GeneralTiltedEllipseProjectionHasBoundedError()=>TestSupport.Run("3DSOLID tilted ellipse affine projection bounded error",()=>
    {
        Point Project(Point3D p)=>new(p.X+0.4*p.Z,0.6*p.Y+0.8*p.Z);
        var curves=DxfSolidProjection.ReadProjected(Ellipse(tilted:true),null,Project,out bool approximate);Assert.True(approximate);
        for(int i=0;i<=4000;i++)
        {
            double t=Math.Tau*i/4000;var p=Project(new(3*Math.Cos(t),0,3*Math.Sin(t)));
            Assert.True(curves.Min(c=>(c.Closest(p)-p).Length)<=SplineGeometry.Tolerance+1e-10);
        }
    });
    [Fact]
    public void HugeAlmostCircularProjectionCannotBypassErrorBound()=>TestSupport.Run("3DSOLID huge near-circle does not bypass absolute approximation bound",()=>
    {
        Assert.Throws<NotSupportedException>(()=>DxfSolidProjection.ReadProjected(Ellipse(),null,p=>new(p.X*1e8,p.Y*(1e8+1e-3)),out _));
    });
    [Fact]
    public void PublicCurvedSolidsLoad()=>TestSupport.Run("3DSOLID three curved api-cw750 solids load with actual boundaries",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        var solids=doc.Entities.Where(e=>e.Kind=="3DSOLID").OrderBy(e=>e.SourceHandle).ToArray();
        Assert.Equal(new[]{"7813","781F","7843"},solids.Select(e=>e.SourceHandle));
        Assert.DoesNotContain(doc.Skipped.Keys,k=>k.StartsWith("3DSOLID"));
        Assert.All(solids,s=>{Assert.NotEmpty(s.Curves);Assert.Empty(s.FillContours);Assert.True(s.Bounds.Width>0.1);Assert.True(s.Bounds.Height>0.1);});
        Assert.Contains(solids,s=>s.IsApproximate);Assert.Contains(solids.SelectMany(s=>s.Curves),c=>!c.IsLine);
    });
    [Fact]
    public void CurvedSolidRendersAndCanBePicked()=>TestSupport.Run("GPU: curved solid boundary rendering picking and exact distance",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object o,string name)=>o.GetType().GetField(name,flags)!.GetValue(o);
        object? Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,flags)!.Invoke(o,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=800,Height=600,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();view.SetDocument(Document(Ellipse()));Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"Curved ACIS solid: exact circular boundary",()=>Call(view,"RenderNow"));
            var screen=(Point)Call(view,"Screen",new Point(0,3))!;
            Assert.True(frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),(r,g,b)=>r>120&&g>120&&b>120));
            Call(view,"SelectAt",new Point(0,3),false);Assert.Equal("3DSOLID",Assert.Single(view.Selected).Entity.Kind);
            Call(view,"SelectAt",new Point(5,0),false);TestSupport.Near(2,view.Measurement!.Value.Distance);
            var source=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
            foreach(var solid in source.Entities.Where(e=>e.Kind=="3DSOLID"))
            {
                var drawing=new DrawingBuilder();drawing.Entities.Add(new DxfEntity("3DSOLID","0",solid.SourceHandle,solid.Curves){IsApproximate=solid.IsApproximate,RenderDots=solid.RenderDots});
                view.SetDocument(drawing);Pump();
                VisualReport.Capture(renderer,"api-cw750 curved solid "+solid.SourceHandle+" (XY boundary close-up)",()=>Call(view,"RenderNow"));
                Call(view,"SelectAt",solid.Curves[0].At(0.5),false);Assert.Equal(solid.SourceHandle,Assert.Single(view.Selected).Entity.Handle);
            }
        }
        finally{window.Close();Pump();}
    }));
}
