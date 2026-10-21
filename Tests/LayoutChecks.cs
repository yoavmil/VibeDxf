using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf;
using Xunit;

[Collection("Visual checks")]
public sealed class LayoutChecks
{
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","layout.dxf"));
    private static void PointIs(Point expected,Point actual)
    {TestSupport.Near(expected.X,actual.X,1e-9);TestSupport.Near(expected.Y,actual.Y,1e-9);}

    [Fact]
    public void ViewportsParse()=>TestSupport.Run("VIEWPORT model-to-paper transforms, twist, ID 1, off, 3D and model-space skips",()=>
    {
        var doc=Drawing();
        Assert.Equal(["M1","M2","MC"],doc.Entities.Select(e=>e.Handle));
        Assert.Equal("VIEWPORT (3D view)=1,VIEWPORT (outside paper space)=1",
            string.Join(",",doc.Skipped.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}={p.Value}")));
        // The sheet's own view (ID 1) and the off viewport draw nothing.
        Assert.Equal(["V2","V3"],doc.Layouts.Single().Viewports.Select(v=>v.Handle));
        Assert.Equal(["P1","V2","V3"],doc.Layouts.Single().Entities.Select(e=>e.Handle));
        Assert.All(doc.Layouts.Single().Entities.Where(e=>e.Kind=="VIEWPORT"),e=>Assert.True(e.IsAnnotation));
        var plain=doc.Layouts.Single().Viewports[0];var twisted=doc.Layouts.Single().Viewports[1];
        Assert.Equal(new Rect(1,2,8,6),plain.Paper);Assert.Equal(new Rect(12,2,6,6),twisted.Paper);
        // paper = center + scale·(R(twist)·(model − target) − view center)
        PointIs(new(5,5),plain.ModelToPaper.Transform(new Point(50,50)));
        PointIs(new(0,5),plain.ModelToPaper.Transform(new Point(0,50)));
        PointIs(new(15,5),twisted.ModelToPaper.Transform(new Point(50,50)));
        PointIs(new(15,-5),twisted.ModelToPaper.Transform(new Point(0,50)));
        Assert.Equal(new Rect(0,0,20,8),doc.Layouts.Single().Bounds);
        Assert.Equal(1,doc.RenderingWarnings["VIEWPORT non-rectangular clipping is shown as its rectangle"]);
        Assert.Equal(1,doc.RenderingWarnings["VIEWPORT frozen layers are still shown"]);
    });

    [Fact]
    public void LayoutRendersClippedViewports()=>TestSupport.Run("GPU frames: layout viewports clip model space; switching space reuses buffers",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=1000,Height=520,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();var doc=Drawing();view.SetDocument(doc);Pump();
            Assert.Equal(-1,view.ActiveLayout);
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var lines=Field(renderer,"lineBuffer");var arcs=Field(renderer,"arcBuffer");
            Call(view,"SelectAt",new Point(10,0),false);Assert.Equal("M1",view.Selected.Single().Entity.Handle);

            view.ActiveLayout=0;Pump();
            Assert.Empty(view.Selected);
            var frame=VisualReport.Capture(renderer,"Layout: plain viewport (left) and 90° twisted viewport (right) over model space",()=>Call(view,"RenderNow"));
            Point Screen(Point p)=>(Point)Call(view,"Screen",p)!;
            bool At(Point paper,Func<byte,byte,byte,bool> predicate)
            {
                var s=Screen(paper);
                return frame.Near((int)(s.X*frame.Width/view.ActualWidth),(int)(s.Y*frame.Height/view.ActualHeight),predicate);
            }
            bool Light(byte r,byte g,byte b)=>r>120&&g>120&&b>120;
            bool Dark(byte r,byte g,byte b)=>!Light(r,g,b);
            bool Nothing(Point paper)=>!At(paper,Light);
            // Model line M2 appears horizontally in the plain window and vertically in the twisted one.
            Assert.True(At(new(7,5),Light));Assert.True(At(new(5,6),Light));Assert.True(At(new(15,3.5),Light));
            // The scissor clips it outside both windows.
            Assert.True(Nothing(new(10.5,5)));Assert.True(Nothing(new(15,1)));Assert.True(Nothing(new(19.5,5)));
            Assert.True(At(new(10.5,5),Dark));

            // Layout picking sees paper entities only; model content in viewports and the frames are display-only.
            Call(view,"SelectAt",new Point(10,0),false);Assert.Equal("P1",view.Selected.Single().Entity.Handle);
            view.ClearSelection();Call(view,"SelectAt",new Point(7,5),false);Assert.Empty(view.Selected);
            Call(view,"SelectAt",new Point(9,4),false);Assert.Empty(view.Selected);

            view.ActiveLayout=-1;Pump();Call(view,"RenderNow");
            Assert.Same(lines,Field(renderer,"lineBuffer"));Assert.Same(arcs,Field(renderer,"arcBuffer"));
        }
        finally{window.Close();Pump();}
    }));

    [Fact]
    public void DefaultLayoutsAreDropped()=>TestSupport.Run("Unused default layouts (Layout1 with one bare viewport, empty Layout2) get no tabs",()=>
    {
        // uncommon.dxf declares Layout1 (a single viewport onto model space) and an empty Layout2.
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        Assert.Empty(doc.Layouts);Assert.Equal(147,doc.Entities.Count);
    });

    [Theory]
    [InlineData("api-cw750-details.dxf")]
    public void PublicLayouts(string file)=>TestSupport.Run($"GPU frames: every {file} layout with its viewports",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        var view=new DxfViewport();var window=new Window{Content=view,Width=900,Height=640,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public",file));
            window.Show();view.SetDocument(doc);Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var lines=Field(renderer,"lineBuffer");
            for(int i=0;i<doc.Layouts.Count;i++)
            {
                view.ActiveLayout=i;Pump();Assert.Equal(i,view.ActiveLayout);
                VisualReport.Capture(renderer,$"{file} layout '{doc.Layouts[i].Name}' (paper space and viewports)",()=>view.GetType().GetMethod("RenderNow",flags)!.Invoke(view,null));
                Assert.Same(lines,Field(renderer,"lineBuffer"));
            }
        }
        finally{window.Close();Pump();}
    }));

    [Fact]
    public void PublicSheets()=>TestSupport.Run("api-cw750: four sheets and inactive-layout viewports",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        Assert.Equal(["Sheet 1","Sheet 2","Sheet 3","Sheet 4"],doc.Layouts.Select(l=>l.Name));
        // Inactive layouts store ID/status 0; their first VIEWPORT is the sheet view, the isometric 786B is skipped.
        Assert.Equal(["1B6E","2B75"],doc.Layouts[0].Viewports.Select(v=>v.Handle));
        Assert.Equal(["2007"],doc.Layouts[1].Viewports.Select(v=>v.Handle));
        Assert.Equal(["20BF","21A8"],doc.Layouts[2].Viewports.Select(v=>v.Handle));
        Assert.Equal(["21CB","2E11"],doc.Layouts[3].Viewports.Select(v=>v.Handle));
        Assert.Equal(1,doc.Skipped["VIEWPORT (3D view)"]);
    });
}
