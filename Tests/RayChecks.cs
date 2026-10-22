using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class RayChecks
{
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","rays-and-xlines.dxf"));
    private static DxfEntity Find(DxfDocument doc,string handle)=>doc.Entities.Single(e=>e.Handle==handle);
    private static void PointIs(Point expected,Point actual)
    {TestSupport.Near(expected.X,actual.X,1e-7);TestSupport.Near(expected.Y,actual.Y,1e-7);}
    private static double Distance(Curve a,Curve b)=>Curve.Distance(a,b).Distance;

    [Fact]
    public void RaysAndXLinesParse()=>TestSupport.Run("RAY and XLINE parsing, block transforms and skipped 3D directions",()=>
    {
        var doc=Drawing();
        Assert.Equal(6,doc.Entities.Count);
        Assert.Equal("RAY (3D direction)=1,XLINE (invalid direction)=1",
            string.Join(",",doc.Skipped.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}={p.Value}")));
        var xline=Find(doc,"XH").Curves.Single();Assert.Equal("XLINE",Find(doc,"XH").Kind);
        Assert.Equal(LineExtent.Infinite,xline.Extent);Assert.Equal("CONSTRUCTION",Find(doc,"XH").Layer);
        var ray=Find(doc,"RD").Curves.Single();Assert.Equal(LineExtent.Ray,ray.Extent);
        PointIs(new(10,0),ray.Start);TestSupport.Near(1,(ray.End-ray.Start).Length);
        var blockRay=Find(doc,"I0[0,0]/BR");Assert.Equal("BLOCKED",blockRay.Layer);
        Assert.Equal(LineExtent.Ray,blockRay.Curves[0].Extent);
        PointIs(new(-20,-20),blockRay.Curves[0].Start);PointIs(new(-20,-19),blockRay.Curves[0].End);
        var blockXLine=Find(doc,"I0[0,0]/BX").Curves[0];
        Assert.Equal(LineExtent.Infinite,blockXLine.Extent);PointIs(new(-21,-20),blockXLine.End);
        // Fit extents use only ray/xline base points.
        TestSupport.Near(-20,doc.Bounds.Left);TestSupport.Near(32,doc.Bounds.Right);
        TestSupport.Near(-20,doc.Bounds.Top);TestSupport.Near(5,doc.Bounds.Bottom);
        Assert.Null(new MidpointIndex(doc.Entities).Find(xline.At(0.5),1));
        Assert.Null(new MidpointIndex(doc.Entities).Find(ray.At(0.5),1));
    });

    [Fact]
    public void RayAndXLineDistances()=>TestSupport.Run("RAY and XLINE closest points and distances",()=>
    {
        var doc=Drawing();
        var xline=Find(doc,"XH");var ray=Find(doc,"RD");var line=Find(doc,"L0");var circle=Find(doc,"C0");
        PointIs(new(10,0),ray.Closest(new(0,0)));PointIs(new(-1000,5),xline.Closest(new(-1000,0)));
        TestSupport.Near(10,xline.Distance(line).Distance);
        TestSupport.Near(0,xline.Distance(ray).Distance);PointIs(new(15,5),xline.Distance(ray).A);
        TestSupport.Near(30/Math.Sqrt(2)-2,ray.Distance(circle).Distance);
        TestSupport.Near(8,Find(doc,"I0[0,0]/BX").Distance(circle).Distance);
        TestSupport.Near(0,Find(doc,"I0[0,0]/BR").Distance(xline).Distance);
        TestSupport.Near(20,Find(doc,"I0[0,0]/BR").Distance(line).Distance);
        var right=Curve.Ray(new(0,0),new(1,0));
        TestSupport.Near(Math.Sqrt(34),Distance(right,Curve.Ray(new(-5,3),new(-1,0))));
        TestSupport.Near(3,Distance(right,Curve.Ray(new(5,3),new(1,0))));
        TestSupport.Near(0,Distance(right,Curve.Line(new(7,-1),new(7,1))));
        TestSupport.Near(Math.Sqrt(2),Distance(Curve.XLine(new(0,0),new(1,1)),Curve.XLine(new(0,2),new(-1,-1))));
        TestSupport.Near(1,Distance(Curve.XLine(new(0,0),new(1,0)),Curve.Line(new(-500,1),new(-400,3))));
        var unit=Curve.Arc(new(0,0),2,0,Math.Tau);
        TestSupport.Near(0,Distance(Curve.XLine(new(0,2),new(1,0)),unit));
        TestSupport.Near(3,Distance(Curve.XLine(new(9,5),new(1,0)),unit));
        TestSupport.Near(0,Distance(Curve.Ray(new(-100,0),new(1,0)),unit));
        TestSupport.Near(98,Distance(unit,Curve.Ray(new(-100,0),new(-1,0))));
        var upper=Curve.Arc(new(0,0),1,0,Math.PI);
        TestSupport.Near(3,Distance(Curve.XLine(new(0,-3),new(1,0)),upper));
        Assert.Throws<ArgumentException>(()=>Curve.Ray(new(0,0),new(0,0)));
    });

    [Fact]
    public void RaysRenderAndSelectOnGpu()=>TestSupport.Run("GPU frames: RAY and XLINE extend to the viewport edges",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=1100,Height=600,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();var doc=Drawing();view.SetDocument(doc);Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            Assert.Equal(4,(int)Field(renderer,"rayInstanceCount")!);Assert.Equal(2,(int)Field(renderer,"lineVertexCount")!);
            var frame=VisualReport.Capture(renderer,"RAY and XLINE construction geometry",()=>Call(view,"RenderNow"));
            Point Screen(Point p)=>(Point)Call(view,"Screen",p)!;
            Point World(Point p)=>(Point)Call(view,"World",p)!;
            bool At(Point screen,Func<byte,byte,byte,bool> predicate)=>
                frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),predicate);
            bool Light(byte r,byte g,byte b)=>r>120&&g>120&&b>120;
            bool Orange(byte r,byte g,byte b)=>r>200&&g>90&&b<70;
            var xlineY=Screen(new(0,5)).Y;
            Assert.True(At(new(4,xlineY),Light));Assert.True(At(new(view.ActualWidth-4,xlineY),Light));
            // The ray reaches the top edge but nothing is drawn behind its base point.
            var top=World(new(0,4)).Y;Assert.True(At(Screen(new(top+10,top)),Light));
            Assert.False(At(Screen(new(6,-4)),Light));
            var lines=Field(renderer,"lineBuffer");var arcs=Field(renderer,"arcBuffer");var rays=Field(renderer,"rayBuffer");
            Assert.NotNull(rays);
            Call(view,"SelectAt",new Point(5000,5),true);Assert.Equal("XH",view.Selected.Single().Entity.Handle);
            Call(view,"SelectAt",new Point(2,-5),false);Assert.Equal("L0",view.Selected[1].Entity.Handle);
            TestSupport.Near(10,view.Measurement!.Value.Distance);
            frame=VisualReport.Capture(renderer,"Selected XLINE measured to a LINE",()=>Call(view,"RenderNow"));
            Assert.True(At(new(4,xlineY),Orange));Assert.True(At(new(view.ActualWidth-4,xlineY),Orange));
            // Zoom in and pan far along the xline, away from its base point.
            var scale=(double)Field(view,"scale")!*50;view.GetType().GetField("scale",flags)!.SetValue(view,scale);
            view.GetType().GetField("offset",flags)!.SetValue(view,new Vector(view.ActualWidth/2-1000*scale,view.ActualHeight/2+5*scale-100));
            frame=VisualReport.Capture(renderer,"XLINE selection zoomed in 1000 units from its base point",()=>Call(view,"RenderNow"));
            xlineY=Screen(new(0,5)).Y;TestSupport.Near(view.ActualHeight/2-100,xlineY,1e-6);
            Assert.True(At(new(4,xlineY),Orange));Assert.True(At(new(view.ActualWidth-4,xlineY),Orange));
            Assert.Same(lines,Field(renderer,"lineBuffer"));Assert.Same(arcs,Field(renderer,"arcBuffer"));Assert.Same(rays,Field(renderer,"rayBuffer"));
        }
        finally{window.Close();Pump();}
    }));
}
