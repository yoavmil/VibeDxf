using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class DimensionChecks
{
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","dimensions.dxf"));
    private static DxfEntity Find(DxfDocument doc,string handle)=>doc.Entities.Single(e=>e.Handle==handle);
    private static void PointIs(Point expected,Point actual)
    {TestSupport.Near(expected.X,actual.X,1e-7);TestSupport.Near(expected.Y,actual.Y,1e-7);}

    [Fact]
    public void DimensionBlocksExpand()=>TestSupport.Run("DIMENSION block expansion, defpoints, layers, transforms and skips",()=>
    {
        var doc=Drawing();
        // Four expansions of *D1 (3 lines, 2 arrowhead solids, 1 MTEXT) plus one ordinary LINE.
        Assert.Equal(25,doc.Entities.Count);
        Assert.Equal("DIMENSION (3D extrusion)=1,DIMENSION (empty block)=1,DIMENSION (missing block)=2",
            string.Join(",",doc.Skipped.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}={p.Value}")));
        Assert.DoesNotContain(doc.Entities,e=>e.Kind=="POINT");
        Assert.All(doc.Entities.Where(e=>e.Handle!="L0"),e=>Assert.True(e.IsAnnotation,e.Handle));
        Assert.False(Find(doc,"L0").IsAnnotation);
        Assert.Equal("DIMS",Find(doc,"D0/DL").Layer);Assert.Equal("DIMS-EXT",Find(doc,"D0/E1").Layer);
        Assert.Equal("DIMS",Find(doc,"D0/A1[0,0]/AS").Layer);Assert.Equal("MTEXT",Find(doc,"D0/TX").Kind);
        PointIs(new(0,5),Find(doc,"D0/DL").Curves[0].Start);PointIs(new(10,5),Find(doc,"D0/DL").Curves[0].End);
        PointIs(new(1,4.75),Find(doc,"D0/A2[0,0]/AS").FillContours[0][1]);
        PointIs(new(-10,5),Find(doc,"DM/DL").Curves[0].End);
        PointIs(new(0,25),Find(doc,"DC/DL").Curves[0].Start);
        var nested=Find(doc,"I0[0,0]/ND/DL");Assert.Equal("ASSEMBLY",nested.Layer);
        PointIs(new(20,0),nested.Curves[0].Start);PointIs(new(20,20),nested.Curves[0].End);
        var index=new MidpointIndex(doc.Entities);
        Assert.Null(index.Find(new(5,5),1));
        Assert.Equal("L0",index.Find(new(5,-2),1)!.Entity.Handle);
    });

    [Fact]
    public void DimensionsRenderButAreNotSelectable()=>TestSupport.Run("GPU frames: dimensions are drawn but not selectable",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=1000,Height=700,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();view.SetDocument(Drawing());Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"DIMENSION blocks: plain, mirrored, offset and nested in a rotated INSERT",()=>Call(view,"RenderNow"));
            Point Screen(Point p)=>(Point)Call(view,"Screen",p)!;
            bool At(Point world,Func<byte,byte,byte,bool> predicate)
            {
                var s=Screen(world);
                return frame.Near((int)(s.X*frame.Width/view.ActualWidth),(int)(s.Y*frame.Height/view.ActualHeight),predicate);
            }
            bool Light(byte r,byte g,byte b)=>r>120&&g>120&&b>120;
            Assert.True(At(new(3,5),Light));Assert.True(At(new(-3,5),Light));Assert.True(At(new(20,15),Light));
            var lines=Field(renderer,"lineBuffer");var arcs=Field(renderer,"arcBuffer");
            Call(view,"SelectAt",new Point(3,5),true);Assert.Empty(view.Selected);
            Call(view,"SelectAt",new Point(3,5),false);Assert.Empty(view.Selected);
            Call(view,"SelectAt",new Point(3,-2),false);Assert.Equal("L0",view.Selected.Single().Entity.Handle);
            VisualReport.Capture(renderer,"Selected LINE below a dimension",()=>Call(view,"RenderNow"));
            Assert.Same(lines,Field(renderer,"lineBuffer"));Assert.Same(arcs,Field(renderer,"arcBuffer"));
        }
        finally{window.Close();Pump();}
    }));
}
