using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeDxf;
using Xunit;

[Collection("Visual checks")]
public sealed class PrimitiveChecks
{
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","primitives-and-blocks.dxf"));
    private static DxfEntity Find(DxfDocument doc,string handle)=>doc.Entities.Single(e=>e.Handle==handle);
    private static void PointIs(Point expected,Point actual)
    {TestSupport.Near(expected.X,actual.X,1e-7);TestSupport.Near(expected.Y,actual.Y,1e-7);}

    [Fact]
    public void PointAndSolidDistances()=>TestSupport.Run("POINT and SOLID picking, fill and distances",()=>
    {
        var doc=Drawing();Assert.Empty(doc.Skipped);
        var point=Find(doc,"P0");var solid=Find(doc,"S0");var triangle=Find(doc,"TRI");
        PointIs(new(-10,0),point.Curves.Single().Start);
        Assert.True(solid.Contains(new(2,2)));Assert.False(solid.Contains(new(5,2)));
        Assert.True(triangle.Contains(new(8,1)));Assert.False(triangle.Contains(new(6,3)));
        TestSupport.Near(10,point.Distance(solid).Distance);
        var inside=new DxfEntity("POINT","0","",[Curve.Line(new(2,2),new(2,2))]);
        TestSupport.Near(0,new MeasurementTarget(inside).Distance(new(solid)).Distance);
        TestSupport.Near(0,new MeasurementTarget(solid).Distance(new(inside)).Distance);
        var tris=DxfPrimitives.Triangulate(solid.FillContours);
        double area=0;for(int i=0;i<tris.Count;i+=3)area+=Math.Abs(Vector.CrossProduct(tris[i+1]-tris[i],tris[i+2]-tris[i]))/2;
        TestSupport.Near(16,area);
        Assert.Null(new MidpointIndex([point,Find(doc,"T0")]).Find(new(-10,0),1));
    });

    [Fact]
    public void NestedBlockTransforms()=>TestSupport.Run("Block base point, nested rotation, mirror and layer inheritance",()=>
    {
        var doc=Drawing();
        var line=Find(doc,"I0[0,0]/L");PointIs(new(20,0),line.Curves[0].Start);PointIs(new(20,4),line.Curves[0].End);
        Assert.Equal("ASSEMBLY",line.Layer);
        var circle=Find(doc,"I0[0,0]/C");Assert.Equal("DETAIL",circle.Layer);
        PointIs(new(18,2),circle.Curves[0].Center);TestSupport.Near(2,circle.Curves[0].Radius);
        PointIs(new(28,0),Find(doc,"MIRROR[0,0]/L").Curves[0].End);
        Assert.True(Find(doc,"MIRROR[0,0]/C").Curves[0].Sweep<0);
        PointIs(new(50,0),Find(doc,"NEST[0,0]/N[0,0]/L").Curves[0].Start);
        PointIs(new(46,0),Find(doc,"NEST[0,0]/N[0,0]/L").Curves[0].End);
        Assert.Equal(16,doc.Entities.Count(e=>e.Handle.StartsWith("ARRAY[")));
        PointIs(new(55,10),Find(doc,"ARRAY[1,1]/L").Curves[0].Start);
        TestSupport.Near(8,new MeasurementTarget(Find(doc,"I0[0,0]/L")).Distance(new(Find(doc,"MIRROR[0,0]/L"))).Distance);
    });

    [Fact]
    public void NonuniformCircleIsAnEllipse()=>TestSupport.Run("Nonuniform INSERT ellipse approximation and measurement",()=>
    {
        var entity=Find(Drawing(),"ELLIPSE[0,0]/C");
        Assert.True(entity.IsApproximate);Assert.All(entity.Curves,c=>Assert.True(c.IsLine));
        TestSupport.Near(40,entity.Bounds.Left,0.0002);TestSupport.Near(44,entity.Bounds.Right,0.0002);
        TestSupport.Near(0,entity.Bounds.Top,0.0002);TestSupport.Near(2,entity.Bounds.Bottom,0.0002);
        Assert.Null(new MeasurementTarget(entity).Center);
        Assert.Null(new MidpointIndex([entity]).Find(entity.Curves[0].At(.5),1e-7));
        var point=new DxfEntity("POINT","0","",[Curve.Line(new(45,1),new(45,1))]);
        TestSupport.Near(1,entity.Distance(point).Distance,0.0002);
    });

    [Theory]
    [InlineData(0,0)] [InlineData(1,0)] [InlineData(2,0)]
    [InlineData(1,1)] [InlineData(1,2)] [InlineData(1,3)]
    [InlineData(3,0)] [InlineData(4,0)] [InlineData(5,0)]
    public void TextAlignment(int horizontal,int vertical)=>TestSupport.Run($"TEXT alignment {horizontal}/{vertical}",()=>
    {
        var contours=DxfPrimitives.Text("TEST",2,1,0,0,horizontal,vertical,new(0,0),new(10,0),0);
        var entity=new DxfEntity("TEXT","0","",DxfPrimitives.Outline(contours)){FillContours=contours};
        Assert.NotEmpty(entity.Curves);
        if(horizontal==1)TestSupport.Near(10,entity.Bounds.Left+entity.Bounds.Width/2,0.15);
        if(horizontal==2)TestSupport.Near(10,entity.Bounds.Right,0.15);
        if(horizontal is 3 or 5)TestSupport.Near(10,entity.Bounds.Right,0.2);
        if(vertical==1)TestSupport.Near(0,entity.Bounds.Top,1e-8);
        if(vertical==2)TestSupport.Near(0,entity.Bounds.Top+entity.Bounds.Height/2,1e-8);
        if(vertical==3)TestSupport.Near(0,entity.Bounds.Bottom,1e-8);
    });

    [Fact]
    public void TextGlyphHolesAndTransforms()=>TestSupport.Run("TEXT glyph holes, escapes, rotation and mirror",()=>
    {
        Assert.Equal("90° ± Ø Ω",DxfPrimitives.DecodeText(@"90%%d %%p %%c \U+03A9"));
        var contours=DxfPrimitives.Text("O",4,1,0,0,0,0,new(0,0),default,0);
        var entity=new DxfEntity("TEXT","0","",DxfPrimitives.Outline(contours)){FillContours=contours};
        var middle=new Point(entity.Bounds.Left+entity.Bounds.Width/2,entity.Bounds.Top+entity.Bounds.Height/2);
        Assert.False(entity.Contains(middle));
        var tris=DxfPrimitives.Triangulate(contours);
        bool InTriangle(Point a,Point b,Point c,Point p)
        {
            var x=Vector.CrossProduct(b-a,p-a);var y=Vector.CrossProduct(c-b,p-b);var z=Vector.CrossProduct(a-c,p-c);
            return (x>1e-9&&y>1e-9&&z>1e-9)||(x< -1e-9&&y< -1e-9&&z< -1e-9);
        }
        for(int i=0;i<tris.Count;i+=3)Assert.False(InTriangle(tris[i],tris[i+1],tris[i+2],middle));
        var normal=DxfPrimitives.Text("TEST",2,1,0,0,0,0,default,default,0);
        var rotated=DxfPrimitives.Text("TEST",2,1,0,0,0,0,default,default,90);
        var mirrored=DxfPrimitives.Text("TEST",2,1,0,2,0,0,default,default,0);
        PointIs(new(-normal[0][0].Y,normal[0][0].X),rotated[0][0]);
        PointIs(new(-normal[0][0].X,normal[0][0].Y),mirrored[0][0]);
    });

    [Fact]
    public void BrokenInsertsAreReported()=>TestSupport.Run("Missing, recursive and 3D inserts warn without discarding valid geometry",()=>
    {
        var path=Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,["0","SECTION","2","BLOCKS","0","BLOCK","2","LOOP","0","INSERT","2","LOOP","0","ENDBLK","0","ENDSEC",
                "0","SECTION","2","ENTITIES","0","POINT","10","0","20","0","0","INSERT","2","LOOP","0","INSERT","2","MISSING",
                "0","INSERT","2","LOOP","210","1","0","ENDSEC","0","EOF"]);
            var doc=DxfReader.Load(path);Assert.Single(doc.Entities);
            Assert.Equal(1,doc.Skipped["INSERT (recursive or too deep)"]);
            Assert.Equal(1,doc.Skipped["INSERT (missing block)"]);
            Assert.Equal(1,doc.Skipped["INSERT (3D extrusion)"]);Assert.NotNull(doc.UnsupportedWarning);
        }
        finally{File.Delete(path);}
    });

    [Fact]
    public void PrimitivesRenderAndSelectOnGpu()=>TestSupport.Run("GPU frames: point marker, solid fill, text hole and block selection",()=>TestSupport.Sta(()=>
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
            var frame=VisualReport.Capture(renderer,"POINT, filled SOLID, TEXT and nested block INSERTs",()=>Call(view,"RenderNow"));
            bool Pixel(Point p,Func<byte,byte,byte,bool> predicate)
            {
                var screen=(Point)Call(view,"Screen",p)!;
                return frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),predicate);
            }
            Assert.True(Pixel(new(-10,0),(r,g,b)=>r>120&&g>120&&b>120));
            Assert.True(Pixel(new(2,2),(r,g,b)=>r>120&&g>120&&b>120));
            Assert.True((int)Field(renderer,"fillVertexCount")!>0);Assert.True((int)Field(renderer,"pointVertexCount")!>0);
            var glyphTriangle=DxfPrimitives.Triangulate(Find(doc,"T0").FillContours);
            var ink=new Point((glyphTriangle[0].X+glyphTriangle[1].X+glyphTriangle[2].X)/3,
                (glyphTriangle[0].Y+glyphTriangle[1].Y+glyphTriangle[2].Y)/3);
            Assert.True(Pixel(ink,(r,g,b)=>r>120&&g>120&&b>120));
            var text=Find(doc,"HOLE");var hole=new Point(text.Bounds.Left+text.Bounds.Width/2,text.Bounds.Top+text.Bounds.Height/2);
            Assert.False(Pixel(hole,(r,g,b)=>r>120&&g>120&&b>120));
            var triangles=Field(renderer,"fillBuffer");var points=Field(renderer,"pointBuffer");
            Call(view,"SelectAt",new Point(-10,0),true);Assert.Equal("POINT",view.Selected.Single().Entity.Kind);
            frame=VisualReport.Capture(renderer,"Selected POINT marker",()=>Call(view,"RenderNow"));
            Assert.True(Pixel(new(-10,0),(r,g,b)=>r>200&&g>90&&b<70));
            Call(view,"SelectAt",new Point(2,2),false);Assert.Equal("SOLID",view.Selected[1].Entity.Kind);
            TestSupport.Near(10,view.Measurement!.Value.Distance);
            view.ClearSelection();Call(view,"SelectAt",new Point(20,1),false);
            Assert.Equal("I0[0,0]/L",view.Selected.Single().Entity.Handle);
            frame=VisualReport.Capture(renderer,"Selected transformed block child",()=>Call(view,"RenderNow"));
            Assert.True(Pixel(new(20,1),(r,g,b)=>r>200&&g>90&&b<70));
            view.GetType().GetField("scale",flags)!.SetValue(view,(double)Field(view,"scale")!*2);
            view.GetType().GetField("offset",flags)!.SetValue(view,(Vector)Field(view,"offset")!+new Vector(30,-20));
            Call(view,"RenderNow");Assert.Same(triangles,Field(renderer,"fillBuffer"));Assert.Same(points,Field(renderer,"pointBuffer"));
        }
        finally{window.Close();Pump();}
    }));
}
