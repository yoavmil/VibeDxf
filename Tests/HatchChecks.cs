using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeDxf;
using Xunit;

[Collection("Visual checks")]
public sealed class HatchChecks
{
    [Theory]
    [InlineData("1474")]
    [InlineData("144D")]
    public void ReportedHatchHighlightsStrokesAndContour(string handle)=>TestSupport.Run($"GPU selected hatch {handle}: strokes and contour",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var source=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        var hatch=source.Entities.Single(e=>e.Handle==handle);
        var doc=new DxfDocument();doc.Entities.Add(hatch); // Isolate the hatch: no separate outline entity.
        var view=new DxfViewport();var window=new Window{Content=view,Width=800,Height=600,Left=-20000,Top=-20000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();view.SetDocument(doc);Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var probe=hatch.RenderCurves!.MaxBy(c=>(c.End-c.Start).LengthSquared)!.At(.5);
            VisualReport.Capture(renderer,$"Hatch {handle} alone, unselected: no separate visible border",()=>Call(view,"RenderNow"));
            Call(view,"SelectAt",probe,false);Assert.Same(hatch,view.Selected.Single().Entity);
            var frame=VisualReport.Capture(renderer,$"Selected hatch {handle}: orange hatch lines and surrounding contour",()=>Call(view,"RenderNow"));
            bool Orange(Point p)
            {
                var screen=(Point)Call(view,"Screen",p)!;
                return frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),
                    (r,g,b)=>r>200&&g>90&&b<70);
            }
            Assert.True(Orange(probe));
            Assert.True(Orange(hatch.Curves.MaxBy(c=>(c.End-c.Start).LengthSquared)!.At(.5)));
            // Both slots use the same highlighted geometry with their own selection color.
            ((List<MeasurementTarget>)Field(view,"selected")!).Insert(0,new(new DxfEntity("POINT","0","",[Curve.Line(hatch.Bounds.TopLeft,hatch.Bounds.TopLeft)])));
            frame=VisualReport.Capture(renderer,$"Selected hatch {handle} in second slot: blue hatch lines and contour",()=>Call(view,"RenderNow"));
            var screen=(Point)Call(view,"Screen",probe)!;
            Assert.True(frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),(r,g,b)=>r<80&&g>120&&b>200));
        }
        finally{window.Close();Pump();}
    }));

    [Fact]
    public void SampleHasAnOffsetStoredBoundary()=>TestSupport.Run("Sample 95F2: stored hatch boundary differs from outline 14C2",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        var hatch=doc.Entities.Single(e=>e.Handle=="95F2");var outline=doc.Entities.Single(e=>e.Handle=="14C2");
        var arc=hatch.Curves.First(c=>!c.IsLine);
        var matching=outline.Curves.Where(c=>!c.IsLine&&Math.Abs(c.Radius-arc.Radius)<1e-8).MinBy(c=>(c.Center-arc.Center).LengthSquared)!;
        // Preserve the file's explicit boundary. This non-associative hatch has no source handles;
        // silently moving it to a nearby outline would invent geometry.
        TestSupport.Near(0.0004747522369,arc.Center.X-matching.Center.X,1e-9);
        TestSupport.Near(-0.0102367039654,arc.Center.Y-matching.Center.Y,1e-9);
        Assert.True((arc.Center-matching.Center).Length>0.01);
    });

    [Fact]
    public void MultiplePatternFamiliesAndRationalSpline()=>TestSupport.Run("HATCH crosshatching and rational spline boundary",()=>
    {
        var tags=Tags(70,0,91,1);tags.AddRange(Box(0,10));
        tags.AddRange(Tags(75,0,78,2,53,0,43,0,44,.5,45,0,46,1,79,0,
            53,90,43,.5,44,0,45,1,46,0,79,2,49,1,49,-1));
        var cross=DxfHatch.Read(tags,"0","");
        Assert.Contains(cross.RenderCurves!,c=>Math.Abs(c.Start.Y-c.End.Y)<1e-8);
        Assert.Contains(cross.RenderCurves!,c=>Math.Abs(c.Start.X-c.End.X)<1e-8);
        var rational=Tags(70,1,91,1,92,1,93,2,72,4,94,2,73,1,74,0,95,6,96,3,
            40,0,40,0,40,0,40,1,40,1,40,1,10,1,20,0,10,1,20,1,10,0,20,1,
            42,1,42,Math.Sqrt(.5),42,1,97,0,72,1,10,0,20,1,11,1,21,0,97,0,75,0);
        var curved=DxfHatch.Read(rational,"0","");Assert.True(curved.IsApproximate);
        Assert.True(curved.Contains(new(.65,.65)));Assert.False(curved.Contains(new(.8,.8)));
        TestSupport.Near(Math.PI/4-.5,TriangleArea(curved.FillContours),.0002);
    });

    private static double TriangleArea(List<Point[]> contours)
    {
        var triangles=DxfPrimitives.Triangulate(contours);double area=0;
        for(int i=0;i<triangles.Count;i+=3)area+=Math.Abs(Vector.CrossProduct(triangles[i+1]-triangles[i],triangles[i+2]-triangles[i]))/2;
        return area;
    }

    [Fact]
    public void PublicSampleVisualAtlas()=>TestSupport.Run("GPU atlas: every one of the 89 public sample hatch instances",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        void Render(DxfViewport view)=>view.GetType().GetMethod("RenderNow",flags)!.Invoke(view,null);
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        // 73 model-space hatches plus a 4-hatch title block on each of the 4 sheets.
        var hatches=doc.Entities.Concat(doc.Layouts.SelectMany(l=>l.Entities)).Where(e=>e.Kind=="HATCH").ToArray();Assert.Equal(89,hatches.Length);
        Assert.All(doc.Layouts,l=>Assert.Equal(4,l.Entities.Count(e=>e.Kind=="HATCH")));
        var view=new DxfViewport();var window=new Window{Content=view,Width=640,Height=480,Left=-20000,Top=-20000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            // Showcase: api-cw750 saves most layers off, including the hatch layer.
            window.Show();view.SetDocument(doc);view.ShowAll();Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var lines=Field(renderer,"lineBuffer");var fill=Field(renderer,"fillBuffer");
            foreach(var hatch in hatches)
            {
                // Switching space changes only the camera and draw ranges, never the uploaded buffers.
                view.ActiveLayout=doc.Layouts.FindIndex(l=>l.Entities.Contains(hatch));
                view.ClearSelection();((List<MeasurementTarget>)Field(view,"selected")!).Add(new(hatch));
                view.GetType().GetField("bounds",flags)!.SetValue(view,hatch.Bounds);view.Fit();Pump();
                VisualReport.Capture(renderer,$"Sample hatch {hatch.Handle} · {(hatch.FillContours.Count>0?"solid":"pattern")} · orange = hatch strokes and contour, white = surrounding drawing",()=>Render(view));
                Assert.Same(lines,Field(renderer,"lineBuffer"));Assert.Same(fill,Field(renderer,"fillBuffer"));
            }
        }
        finally{window.Close();Pump();}
    }));

    [Theory]
    [InlineData(0,1,0)] [InlineData(45,1,0)] [InlineData(135,-1,0)]
    [InlineData(225,1,1000000)] [InlineData(315,-1,-1000000)]
    public void ConcavePatternAngles(double angle,double spacingSign,double origin)=>TestSupport.Run($"HATCH concave clipping angle {angle}, offset sign {spacingSign}, origin {origin}",()=>
    {
        var tags=Tags(70,0,91,2,92,2,72,0,73,1,93,6,
            10,0,20,0,10,10,20,0,10,10,20,3,10,3,20,3,10,3,20,10,10,0,20,10,97,0);
        tags.AddRange(Box(1,2));var theta=angle*Math.PI/180;
        tags.AddRange(Tags(75,0,78,1,53,angle,43,origin,44,origin,45,-Math.Sin(theta)*spacingSign,46,Math.Cos(theta)*spacingSign,79,0));
        var hatch=DxfHatch.Read(tags,"0","");Assert.NotEmpty(hatch.RenderCurves!);
        // Independent WPF even/odd fill supplies the containment oracle.
        var reference=new PathGeometry{FillRule=FillRule.EvenOdd};
        foreach(var polygon in hatch.RegionContours!)
            reference.Figures.Add(new PathFigure(polygon[0],[new PolyLineSegment(polygon.Skip(1),true)],true));
        foreach(var stroke in hatch.RenderCurves!)
        {
            foreach(var t in new[]{.01,.25,.5,.75,.99})Assert.True(reference.FillContains(stroke.At(t)));
            Assert.True(hatch.Curves.Min(c=>(c.Closest(stroke.Start)-stroke.Start).Length)<1e-7);
            Assert.True(hatch.Curves.Min(c=>(c.Closest(stroke.End)-stroke.End).Length)<1e-7);
        }
    });

    [Fact]
    public void CrossingAndRotatedFillContours()=>TestSupport.Run("HATCH triangulation splits crossing edges and skips numerical slivers",()=>
    {
        Point[] bowtie=[new(0,0),new(4,4),new(0,4),new(4,0)];
        foreach(var degrees in new[]{0,17,90,179})
        {
            var matrix=Matrix.Identity;matrix.Rotate(degrees);matrix.Translate(1000,-500);
            var polygon=bowtie.Select(matrix.Transform).ToArray();var entity=new DxfEntity("HATCH","0","",DxfPrimitives.Outline([polygon])){FillContours=[polygon]};
            var triangles=DxfPrimitives.Triangulate([polygon]);double area=0;
            for(int i=0;i<triangles.Count;i+=3)
            {
                area+=Math.Abs(Vector.CrossProduct(triangles[i+1]-triangles[i],triangles[i+2]-triangles[i]))/2;
                var p=new Point((triangles[i].X+triangles[i+1].X+triangles[i+2].X)/3,(triangles[i].Y+triangles[i+1].Y+triangles[i+2].Y)/3);
                Assert.True(entity.Contains(p)||entity.Curves.Any(c=>(c.Closest(p)-p).Length<1e-8));
            }
            TestSupport.Near(8,area,1e-8);
        }
    });

    [Fact]
    public void PublicSampleHatchBoundaryAudit()=>TestSupport.Run("Every public sample hatch stroke stays inside and reaches its boundary",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        // 73 model-space hatches plus a 4-hatch title block on each of the 4 sheets.
        var hatches=doc.Entities.Concat(doc.Layouts.SelectMany(l=>l.Entities)).Where(e=>e.Kind=="HATCH").ToArray();Assert.Equal(89,hatches.Length);
        Assert.All(doc.Layouts,l=>Assert.Equal(4,l.Entities.Count(e=>e.Kind=="HATCH")));
        var problems=new List<string>();
        foreach(var hatch in hatches)
        {
            double BoundaryDistance(Point p)=>hatch.Curves.Min(c=>(c.Closest(p)-p).Length);
            foreach(var stroke in hatch.RenderCurves!)
            {
                if(new[]{.01,.25,.5,.75,.99}.Any(t=>!hatch.Contains(stroke.At(t))&&BoundaryDistance(stroke.At(t))>0.0003))
                    problems.Add($"{hatch.Handle}: stroke outside boundary {stroke.Start} -> {stroke.End}");
                // All pattern definitions in this public drawing are continuous ANSI31 strokes.
                if(BoundaryDistance(stroke.Start)>0.0003||BoundaryDistance(stroke.End)>0.0003)
                    problems.Add($"{hatch.Handle}: endpoint misses boundary ({BoundaryDistance(stroke.Start)}, {BoundaryDistance(stroke.End)})");
            }
            var triangles=DxfPrimitives.Triangulate(hatch.FillContours);
            for(int i=0;i<triangles.Count;i+=3)
            {
                var centroid=new Point((triangles[i].X+triangles[i+1].X+triangles[i+2].X)/3,(triangles[i].Y+triangles[i+1].Y+triangles[i+2].Y)/3);
                if(!hatch.Contains(centroid)&&BoundaryDistance(centroid)>0.0003)problems.Add($"{hatch.Handle}: fill triangle outside boundary: {triangles[i]} / {triangles[i+1]} / {triangles[i+2]}, distance {BoundaryDistance(centroid)}, area {Vector.CrossProduct(triangles[i+1]-triangles[i],triangles[i+2]-triangles[i])}");
            }
        }
        Assert.True(problems.Count==0,string.Join("\n",problems.Take(30)));
    });
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","hatches.dxf"));
    private static DxfEntity Find(string handle)=>Drawing().Entities.Single(e=>e.Handle==handle);
    private static List<(int Code,string Value)> Tags(params object[] data)=>Enumerable.Range(0,data.Length/2)
        .Select(i=>((int)data[2*i],Convert.ToString(data[2*i+1],System.Globalization.CultureInfo.InvariantCulture)!)).ToList();
    private static List<(int Code,string Value)> Box(double a,double b)=>Tags(92,2,72,0,73,1,93,4,10,a,20,a,10,b,20,a,10,b,20,b,10,a,20,b,97,0);

    [Theory]
    [InlineData(0,68)] [InlineData(1,64)] [InlineData(2,100)]
    public void IslandStyles(int style,double expectedArea)=>TestSupport.Run($"HATCH nested island style {style}",()=>
    {
        var tags=Tags(70,1,91,3);tags.AddRange(Box(0,10));tags.AddRange(Box(2,8));tags.AddRange(Box(4,6));tags.AddRange(Tags(75,style));
        var hatch=DxfHatch.Read(tags,"0","test");
        var triangles=DxfPrimitives.Triangulate(hatch.FillContours);double area=0;
        for(int i=0;i<triangles.Count;i+=3)area+=Math.Abs(Vector.CrossProduct(triangles[i+1]-triangles[i],triangles[i+2]-triangles[i]))/2;
        TestSupport.Near(expectedArea,area);
        Assert.True(hatch.Contains(new(1,1)));
        Assert.Equal(style==2,hatch.Contains(new(3,3)));
        Assert.Equal(style!=1,hatch.Contains(new(5,5)));
    });

    [Fact]
    public void SolidHoleAndDistances()=>TestSupport.Run("HATCH hole picking and region distances",()=>
    {
        var hatch=Find("RING");Assert.True(hatch.Contains(new(1,1)));Assert.False(hatch.Contains(new(5,5)));
        var holePoint=new DxfEntity("POINT","0","",[Curve.Line(new(5,5),new(5,5))]);
        TestSupport.Near(2,hatch.Distance(holePoint).Distance);
        var interior=new MeasurementTarget(new DxfEntity("POINT","0","",[Curve.Line(new(1,1),new(1,1))]));
        TestSupport.Near(0,new MeasurementTarget(hatch).Distance(interior).Distance);
        TestSupport.Near(0,interior.Distance(new(hatch)).Distance);
        Assert.Null(new MidpointIndex([hatch]).Find(new(5,0),.1));
        Assert.NotNull(hatch.RenderCurves);Assert.Empty(hatch.RenderCurves);
    });

    [Fact]
    public void PatternClippingAndDashPhase()=>TestSupport.Run("HATCH dash phase, dots and hole clipping",()=>
    {
        var hatch=Find("PATTERN");Assert.Empty(hatch.FillContours);Assert.NotEmpty(hatch.RenderCurves!);Assert.NotEmpty(hatch.RenderDots);
        Assert.True(hatch.Contains(new(15,1)));Assert.False(hatch.Contains(new(19,5)));
        foreach(var c in hatch.RenderCurves!)
        {
            Assert.True(hatch.Contains(c.At(.5)));
            Assert.True(c.Start.X>=14-1e-8&&c.End.X<=24+1e-8);
            // Horizontal dashes start at 14 + multiples of four, retaining phase after hole clipping.
            double phase=((c.At(.5).X-14)%4+4)%4;Assert.InRange(phase,0,2);
            Assert.True(c.End.X-c.Start.X<=2+1e-8);
        }
        Assert.All(hatch.RenderDots,p=>Assert.True(hatch.Contains(p)||hatch.Curves.Any(c=>(c.Closest(p)-p).Length<1e-8)));
    });

    [Theory]
    [InlineData("CIRCLE",26,34,1,9)]
    [InlineData("ELLIPSE",38,46,3,7)]
    [InlineData("BULGE",52,60,-3,5)]
    public void CurvedBoundaries(string handle,double left,double right,double bottom,double top)=>TestSupport.Run($"HATCH curved boundary {handle}",()=>
    {
        var hatch=Find(handle);Assert.True(hatch.IsApproximate);
        TestSupport.Near(left,hatch.Bounds.Left,.0002);TestSupport.Near(right,hatch.Bounds.Right,.0002);
        TestSupport.Near(bottom,hatch.Bounds.Top,.0002);TestSupport.Near(top,hatch.Bounds.Bottom,.0002);
        Assert.True(hatch.Contains(new((left+right)/2,(bottom+top)/2)));
        Assert.NotEmpty(DxfPrimitives.Triangulate(hatch.FillContours));
    });

    [Fact]
    public void ClockwiseArcEdge()=>TestSupport.Run("HATCH clockwise arc uses complementary DXF angles",()=>
    {
        var tags=Tags(70,1,91,1,92,1,93,2,72,2,10,0,20,0,40,2,50,180,51,360,73,0,
            72,1,10,2,20,0,11,-2,21,0,97,0,75,0);
        var hatch=DxfHatch.Read(tags,"0","");Assert.True(hatch.Contains(new(0,1)));Assert.False(hatch.Contains(new(0,-1)));
        TestSupport.Near(2,hatch.Bounds.Bottom);TestSupport.Near(0,hatch.Bounds.Top,1e-8);
    });

    [Fact]
    public void SplineEdgeAndBlockTransform()=>TestSupport.Run("HATCH spline edge and transformed block pattern",()=>
    {
        var hatch=Find("SPLINE");Assert.True(hatch.IsApproximate);Assert.True(hatch.Contains(new(2,1)));Assert.False(hatch.Contains(new(2,3)));
        TestSupport.Near(2.25,hatch.Bounds.Bottom,.0002);
        var nested=Find("INSERT[0,0]/HB");Assert.NotEmpty(nested.RenderCurves!);Assert.Empty(nested.FillContours);
        var matrix=Matrix.Identity;matrix.Scale(2,1);matrix.Rotate(15);matrix.Translate(0,15);
        Assert.True(nested.Contains(matrix.Transform(new Point(1,1))));Assert.False(nested.Contains(matrix.Transform(new Point(4,4))));
        foreach(var c in nested.RenderCurves!)Assert.True(nested.Contains(c.At(.5)));
    });

    [Fact]
    public void UnsupportedHatchesWarn()=>TestSupport.Run("HATCH gradients, malformed boundaries and dense patterns warn safely",()=>
    {
        var path=Path.GetTempFileName();
        try
        {
            var header=Tags(0,"SECTION",2,"ENTITIES",0,"POINT",10,-1,20,-1);
            header.AddRange(Tags(0,"HATCH",70,1,450,1,91,0));
            header.AddRange(Tags(0,"HATCH",70,1,91,1,92,2,72,0,73,1,93,4,10,0));
            header.AddRange(Tags(0,"HATCH",70,0,91,1));header.AddRange(Box(0,10));
            header.AddRange(Tags(75,0,78,1,53,0,43,0,44,0,45,0,46,1e-8,79,0,0,"ENDSEC",0,"EOF"));
            File.WriteAllLines(path,header.SelectMany(t=>new[]{t.Code.ToString(),t.Value}));
            var doc=DxfReader.Load(path);Assert.Single(doc.Entities);
            Assert.Equal(1,doc.Skipped["HATCH (gradient)"]);
            Assert.Equal(1,doc.Skipped["HATCH (invalid or unsupported boundary/pattern)"]);
            Assert.Equal(1,doc.Skipped["HATCH (pattern density limit)"]);Assert.NotNull(doc.UnsupportedWarning);
        }
        finally{File.Delete(path);}
    });

    [Fact]
    public void GpuFillPatternHoleAndSelection()=>TestSupport.Run("GPU HATCH fill, pattern, holes, selection and buffer reuse",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=1200,Height=800,Left=-20000,Top=-20000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            var doc=Drawing();Assert.Empty(doc.Skipped);Assert.Equal(7,doc.Entities.Count);
            window.Show();view.SetDocument(doc);Pump();var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"Solid and patterned HATCH with holes, curved boundaries and a block insert",()=>Call(view,"RenderNow"));
            bool Ink(Point p,Func<byte,byte,byte,bool>? predicate=null)
            {
                var screen=(Point)Call(view,"Screen",p)!;
                return frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),predicate??((r,g,b)=>r>120&&g>120&&b>120));
            }
            Assert.True(Ink(new(1,1)));Assert.False(Ink(new(5,5)));
            Assert.True(Ink(new(15,.5)));Assert.False(Ink(new(19,5.5)));
            Assert.True((int)Field(renderer,"dotVertexCount")!>0);
            Call(view,"SelectAt",new Point(1,1),true);Assert.Equal("RING",view.Selected.Single().Entity.Handle);
            frame=VisualReport.Capture(renderer,"Selected HATCH region highlights its boundary and hole",()=>Call(view,"RenderNow"));
            Assert.True(Ink(new(0,5),(r,g,b)=>r>200&&g>90&&b<70));
            Assert.True(Ink(new(3,5),(r,g,b)=>r>200&&g>90&&b<70));
            view.ClearSelection();Call(view,"SelectAt",new Point(5,5),false);Assert.Empty(view.Selected);
            Call(view,"SelectAt",new Point(15,1),false);Assert.Equal("PATTERN",view.Selected.Single().Entity.Handle);
            frame=VisualReport.Capture(renderer,"Selected patterned HATCH highlights strokes, dots, outer contour and hole",()=>Call(view,"RenderNow"));
            Assert.True(Ink(new(15,.5),(r,g,b)=>r>200&&g>90&&b<70));
            Assert.True(Ink(new(14,5),(r,g,b)=>r>200&&g>90&&b<70));
            Assert.True(Ink(new(17,5),(r,g,b)=>r>200&&g>90&&b<70));
            Assert.True(Ink(new(17,.5),(r,g,b)=>r>200&&g>90&&b<70));
            var fill=Field(renderer,"fillBuffer");var lines=Field(renderer,"lineBuffer");var dots=Field(renderer,"dotBuffer");
            view.GetType().GetField("scale",flags)!.SetValue(view,(double)Field(view,"scale")!*2);
            view.GetType().GetField("offset",flags)!.SetValue(view,(Vector)Field(view,"offset")!+new Vector(20,-10));
            Call(view,"RenderNow");Assert.Same(fill,Field(renderer,"fillBuffer"));Assert.Same(lines,Field(renderer,"lineBuffer"));Assert.Same(dots,Field(renderer,"dotBuffer"));
        }
        finally{window.Close();Pump();}
    }));
}
