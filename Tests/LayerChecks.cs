using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf;
using Xunit;

[Collection("Visual checks")]
public sealed class LayerChecks
{
    private static DxfDocument Public(string file)=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public",file));
    private static DxfEntity Find(DxfDocument doc,string handle)=>doc.Entities.Concat(doc.Layouts.SelectMany(l=>l.Entities)).Single(e=>e.Handle==handle);
    private static uint Rgb(byte r,byte g,byte b)=>DxfColors.Pack(r,g,b);

    [Theory]
    [InlineData(1,255,0,0)] [InlineData(7,255,255,255)] [InlineData(8,128,128,128)] [InlineData(10,255,0,0)]
    [InlineData(11,255,127,127)] [InlineData(13,204,102,102)] [InlineData(20,255,63,0)] [InlineData(30,255,127,0)]
    [InlineData(170,0,0,255)] [InlineData(250,51,51,51)] [InlineData(255,255,255,255)]
    public void AciPalette(int index,byte r,byte g,byte b)=>TestSupport.Run($"ACI palette color {index}",()=>
        Assert.Equal(Rgb(r,g,b),DxfColors.FromAci(index)));

    [Fact]
    public void ColorResolution()=>TestSupport.Run("Layer colors, true color, BYLAYER and BYBLOCK through inserts (colors.dxf)",()=>
    {
        var doc=Public("colors.dxf");
        Assert.Equal(["0","BLUE","RED"],doc.Layers.Select(l=>l.Name));
        var blue=doc.Layers[1].Color;var red=doc.Layers[2].Color;
        // Saturated blue/red are lifted just enough to read on the dark background, keeping their hue.
        Assert.Equal(DxfColors.ForDarkBackground(DxfColors.FromAci(170)),blue);Assert.Equal(DxfColors.ForDarkBackground(DxfColors.FromAci(10)),red);
        var (br,bg,bb)=DxfColors.Unpack(blue);Assert.True(bb==255&&br==bg&&br<100,DxfColors.Hex(blue));
        Assert.Equal(blue,Find(doc,"219").Color);Assert.Equal(red,Find(doc,"343").Color);
        // True color 0xF26722 wins over ACI 30.
        Assert.Equal(Rgb(0xF2,0x67,0x22),Find(doc,"220").Color);Assert.Equal(Rgb(0xF2,0x67,0x22),Find(doc,"346").Color);
        // BYBLOCK at top level is white.
        Assert.Equal(DxfColors.White,Find(doc,"28F").Color);
        // Insert 302 (BLUE layer, yellow): BYBLOCK children take yellow, BYLAYER children on layer 0 take the insert's layer.
        var yellow=DxfColors.FromAci(2);
        var children=doc.Entities.Where(e=>e.Handle.StartsWith("302[")).ToDictionary(e=>e.Handle[(e.Handle.LastIndexOf('/')+1)..]);
        Assert.Equal(yellow,children["2E3"].Color);Assert.Equal(yellow,children["2E4"].Color);
        Assert.Equal(blue,children["2DD"].Color);Assert.Equal(blue,children["2DE"].Color);
        Assert.Equal(DxfColors.ForDarkBackground(DxfColors.FromAci(6)),children["2E0"].Color);Assert.Equal(DxfColors.White,children["2DA"].Color);
        Assert.All(children.Values,e=>Assert.Equal("BLUE",e.Layer));
        // Insert 301 (BLUE, BYLAYER): its BYBLOCK children resolve to the insert's own color, blue.
        Assert.Equal(blue,doc.Entities.Single(e=>e.Handle.StartsWith("301[")&&e.Handle.EndsWith("/2E3")).Color);
        // Groups are distinct layer + category pairs and every entity points at its own.
        Assert.All(doc.Entities,e=>Assert.Equal((doc.Layers.FindIndex(l=>l.Name==e.Layer),e.Category),doc.Groups[e.Group]));
    });

    [Fact]
    public void SavedLayerState()=>TestSupport.Run("Saved layer state: off, frozen and per-viewport frozen layers",()=>
    {
        var api=Public("api-cw750-details.dxf");
        Assert.Equal(22,api.Layers.Count);Assert.Equal(13,api.Layers.Count(l=>!l.OnInFile));
        Assert.False(api.Layers.Single(l=>l.Name=="FG-Dim").VisibleInFile);Assert.False(api.Layers.Single(l=>l.Name=="Defpoints").Plot);
        var doc=Public("viewport_frozen_layers.dxf");
        Assert.True(doc.Layers.Single(l=>l.Name=="Layer3").FrozenInFile);Assert.False(doc.Layers.Single(l=>l.Name=="Layer3").VisibleInFile);
        var viewports=doc.Layouts.Single().Viewports;
        int Layer(string name)=>doc.Layers.FindIndex(l=>l.Name==name);
        Assert.Equal(["B1","B8","BB"],viewports.Select(v=>v.Handle));
        Assert.Empty(viewports[0].FrozenLayers);Assert.Equal([Layer("Layer1")],viewports[1].FrozenLayers);Assert.Equal([Layer("Layer2")],viewports[2].FrozenLayers);
        Assert.Empty(doc.RenderingWarnings);
        // Dimension block children are annotations whatever their own kind, and keep their BYBLOCK color.
        var dims=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","dimensions.dxf"));
        Assert.All(dims.Entities.Where(e=>e.Handle.StartsWith("D0/")),e=>Assert.Equal(EntityCategory.Dimension,e.Category));
    });

    private sealed class Harness:IDisposable
    {
        const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
        public readonly DxfViewport View=new();
        private readonly Window window;
        public readonly DrawingRenderer Renderer;
        public Harness(DxfDocument doc,double width=1000,double height=700)
        {
            window=new Window{Content=View,Width=width,Height=height,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
            window.Show();View.SetDocument(doc);Pump();
            Renderer=(DrawingRenderer)Field(Field(View,"host")!,"<Renderer>k__BackingField")!;
        }
        public static object? Field(object target,string name)=>target.GetType().GetField(name,Flags)!.GetValue(target);
        public object? Call(string name,params object[] args)=>View.GetType().GetMethod(name,Flags)!.Invoke(View,args);
        public void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        public VisualReport.Frame Capture(string title)=>VisualReport.Capture(Renderer,title,()=>Call("RenderNow"));
        public Point Screen(Point world)=>(Point)Call("Screen",world)!;
        public (int X,int Y) Pixel(VisualReport.Frame frame,Point world)
        {var s=Screen(world);return ((int)(s.X*frame.Width/View.ActualWidth),(int)(s.Y*frame.Height/View.ActualHeight));}
        public bool At(VisualReport.Frame frame,Point world,Func<byte,byte,byte,bool> predicate){var (x,y)=Pixel(frame,world);return frame.Near(x,y,predicate);}
        // Counts drawn (non-background) pixels inside a world rectangle.
        public int Ink(VisualReport.Frame frame,Rect world)
        {
            var (x0,y0)=Pixel(frame,world.TopLeft);var (x1,y1)=Pixel(frame,world.BottomRight);int count=0;
            for(int y=Math.Max(0,Math.Min(y0,y1));y<=Math.Min(frame.Height-1,Math.Max(y0,y1));y++)
                for(int x=Math.Max(0,Math.Min(x0,x1));x<=Math.Min(frame.Width-1,Math.Max(x0,x1));x++)
                {int i=(y*frame.Width+x)*4;if(Math.Max(frame.Pixels[i],Math.Max(frame.Pixels[i+1],frame.Pixels[i+2]))>90)count++;}
            return count;
        }
        public void Dispose(){window.Close();Pump();}
    }

    [Fact]
    public void ColorsVisibilityAndMonochromeOnGpu()=>TestSupport.Run("GPU frames: DXF colors, monochrome and hiding a layer without re-upload (colors.dxf)",()=>TestSupport.Sta(()=>
    {
        var doc=Public("colors.dxf");using var h=new Harness(doc);
        var lines=Harness.Field(h.Renderer,"lineBuffer");var arcs=Harness.Field(h.Renderer,"arcBuffer");var fills=Harness.Field(h.Renderer,"fillBuffer");
        var blueCircle=Find(doc,"219").Curves[0].At(.25);var orangeCircle=Find(doc,"220").Curves[0].At(.25);
        bool Blue(byte r,byte g,byte b)=>b>200&&r<140&&g<140;
        bool Orange(byte r,byte g,byte b)=>r>200&&g>60&&g<150&&b<90;
        bool Grey(byte r,byte g,byte b)=>r>150&&Math.Abs(r-g)<12&&Math.Abs(g-b)<12;
        var frame=h.Capture("colors.dxf in DXF colors (BYLAYER, true color, BYBLOCK inserts)");
        Assert.True(h.At(frame,blueCircle,Blue));Assert.True(h.At(frame,orangeCircle,Orange));
        h.View.Monochrome=true;
        frame=h.Capture("colors.dxf in monochrome");
        Assert.True(h.At(frame,blueCircle,Grey));Assert.False(h.At(frame,orangeCircle,Orange));
        h.View.Monochrome=false;h.View.SetLayerVisible(doc.Layers.FindIndex(l=>l.Name=="BLUE"),false);
        frame=h.Capture("colors.dxf with layer BLUE hidden");
        Assert.False(h.At(frame,blueCircle,Blue));Assert.False(h.At(frame,orangeCircle,Orange));
        // Hidden entities can't be picked.
        h.Call("SelectAt",blueCircle,false);Assert.Empty(h.View.Selected);
        h.View.ShowAll();h.Call("SelectAt",blueCircle,false);Assert.Equal("219",h.View.Selected.Single().Entity.Handle);
        Assert.Same(lines,Harness.Field(h.Renderer,"lineBuffer"));Assert.Same(arcs,Harness.Field(h.Renderer,"arcBuffer"));Assert.Same(fills,Harness.Field(h.Renderer,"fillBuffer"));
    }));

    [Fact]
    public void AnnotationsAreDimmedAndToggle()=>TestSupport.Run("GPU frames: dimensions drawn dimmed and hidden by category",()=>TestSupport.Sta(()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","dimensions.dxf"));using var h=new Harness(doc);
        var lines=Harness.Field(h.Renderer,"lineBuffer");
        bool Full(byte r,byte g,byte b)=>r>230&&g>230&&b>230;
        bool Dimmed(byte r,byte g,byte b)=>r is >90 and <170;
        var frame=h.Capture("Dimension (dimmed annotation) above an ordinary LINE");
        Assert.True(h.At(frame,new(3,-2),Full));
        Assert.True(h.At(frame,new(3,5),Dimmed));Assert.False(h.At(frame,new(3,5),Full));
        h.View.SetCategoryVisible(EntityCategory.Dimension,false);
        frame=h.Capture("Dimensions hidden by category");
        Assert.Equal(0,h.Ink(frame,new Rect(2,4.5,2,1)));Assert.True(h.At(frame,new(3,-2),Full));
        Assert.Same(lines,Harness.Field(h.Renderer,"lineBuffer"));
    }));

    [Fact]
    public void ViewportFrozenLayersOnGpu()=>TestSupport.Run("GPU frame: per-viewport frozen layers and a globally frozen layer (viewport_frozen_layers.dxf)",()=>TestSupport.Sta(()=>
    {
        var doc=Public("viewport_frozen_layers.dxf");using var h=new Harness(doc);
        var layer1=Find(doc,"A8");var layer3=Find(doc,"A6");
        // Layer3 is frozen in the file: hidden in model space until shown.
        var frame=h.Capture("viewport_frozen_layers.dxf model space: Layer3 frozen");
        Assert.Equal(0,h.Ink(frame,layer3.Bounds));Assert.True(h.Ink(frame,layer1.Bounds)>0);
        h.View.ActiveLayout=0;h.Pump();
        frame=h.Capture("viewport_frozen_layers.dxf layout: Layer1 frozen in the middle viewport, Layer2 in the right one");
        int InkIn(DxfViewportWindow viewport,DxfEntity entity)
        {
            var b=entity.Bounds;var m=viewport.ModelToPaper;
            var r=new Rect(m.Transform(b.TopLeft),m.Transform(b.BottomRight));r.Intersect(viewport.Paper);
            Assert.False(r.IsEmpty,$"{entity.Handle} is outside viewport {viewport.Handle}");
            return h.Ink(frame,r);
        }
        var viewports=doc.Layouts[0].Viewports;
        Assert.True(InkIn(viewports[0],layer1)>0);Assert.Equal(0,InkIn(viewports[1],layer1));Assert.True(InkIn(viewports[2],layer1)>0);
        var layer2=Find(doc,"A7");
        Assert.True(InkIn(viewports[1],layer2)>0);Assert.Equal(0,InkIn(viewports[2],layer2));
        // Picking through a viewport skips layers frozen in that viewport only.
        var glyph=layer1.Curves[0].Start;
        h.Call("SelectAt",viewports[1].ModelToPaper.Transform(glyph),false);Assert.Empty(h.View.Selected);
        h.Call("SelectAt",viewports[0].ModelToPaper.Transform(glyph),false);
        Assert.Equal("A8",h.View.Selected.Single().Entity.Handle);Assert.Equal("B1",h.View.Selected[0].Viewport!.Handle);
    }));
}
