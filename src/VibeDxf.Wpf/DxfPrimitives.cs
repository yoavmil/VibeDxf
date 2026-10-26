using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace VibeDxf.Wpf;

// Load-time geometry only. No text layout, curve flattening or triangulation occurs during pan/zoom.
internal static class DxfPrimitives
{
    public static List<Curve> Outline(IEnumerable<IReadOnlyList<Point>> contours) => contours.SelectMany(p =>
        p.Select((a,i) => Curve.Line(a,p[(i+1)%p.Count]))).ToList();

    // Contours whose bounding boxes are disjoint can neither contain nor cross each other, so their even/odd
    // fills are independent. Triangulating each overlapping group alone keeps scan strips local to one glyph
    // (with its holes) instead of spanning a whole line of text.
    public static List<Point> Triangulate(IEnumerable<IReadOnlyList<Point>> contours)
    {
        var list=contours.Where(p=>p.Count>0).ToList();
        if(list.Count<2)return TriangulateGroup(list);
        var boxes=list.Select(p=>(MinX:p.Min(v=>v.X),MinY:p.Min(v=>v.Y),MaxX:p.Max(v=>v.X),MaxY:p.Max(v=>v.Y))).ToArray();
        var parent=Enumerable.Range(0,list.Count).ToArray();
        int Root(int i){while(parent[i]!=i)i=parent[i]=parent[parent[i]];return i;}
        var active=new List<int>();
        foreach(var i in Enumerable.Range(0,list.Count).OrderBy(i=>boxes[i].MinX))
        { LoadingContext.Check();
            active.RemoveAll(j=>boxes[j].MaxX<boxes[i].MinX);
            foreach(var j in active)
                if(boxes[j].MinY<=boxes[i].MaxY&&boxes[i].MinY<=boxes[j].MaxY)parent[Root(i)]=Root(j);
            active.Add(i);
        }
        var result=new List<Point>();
        foreach(var group in Enumerable.Range(0,list.Count).GroupBy(Root))result.AddRange(TriangulateGroup(group.Select(i=>list[i])));
        return result;
    }

    // Even/odd scan conversion into trapezoids handles concave outlines and glyph holes.
    // Split at both vertices and edge crossings: overlapping/self-intersecting paths
    // can change edge order between vertices, including after a block rotation.
    private static List<Point> TriangulateGroup(IEnumerable<IReadOnlyList<Point>> contours)
    {
        var edges=contours.SelectMany(p=>p.Select((a,i)=>(A:a,B:p[(i+1)%p.Count])))
            .Where(e=>Math.Abs(e.A.Y-e.B.Y)>1e-14).ToArray();
        var levels=edges.SelectMany(e=>new[]{e.A.Y,e.B.Y}).ToList();
        var activeEdges=new List<(Point A,Point B)>();
        foreach(var edge in edges.OrderBy(e=>Math.Min(e.A.X,e.B.X)))
        { LoadingContext.Check();
            var minX=Math.Min(edge.A.X,edge.B.X);
            activeEdges.RemoveAll(e=>Math.Max(e.A.X,e.B.X)<minX);
            foreach(var other in activeEdges)
            { LoadingContext.Check();
                if(Math.Max(edge.A.Y,edge.B.Y)<Math.Min(other.A.Y,other.B.Y)||
                    Math.Max(other.A.Y,other.B.Y)<Math.Min(edge.A.Y,edge.B.Y))continue;
                var u=edge.B-edge.A;var v=other.B-other.A;var determinant=Vector.CrossProduct(u,v);
                if(Math.Abs(determinant)<1e-20)continue;
                var t=Vector.CrossProduct(other.A-edge.A,v)/determinant;
                var s=Vector.CrossProduct(other.A-edge.A,u)/determinant;
                if(t>0&&t<1&&s>0&&s<1)levels.Add(edge.A.Y+t*u.Y);
            }
            activeEdges.Add(edge);
        }
        var ys=levels.Distinct().Order().ToArray();
        var result=new List<Point>();
        double X((Point A,Point B) e,double y)=>e.A.X+(y-e.A.Y)*(e.B.X-e.A.X)/(e.B.Y-e.A.Y);
        // Sweep upwards keeping only edges spanning the current strip (minY < mid < maxY).
        // Sorting by (x, edge index) matches a stable sort of all edges by x, so output is unchanged.
        var minY=edges.Select(e=>Math.Min(e.A.Y,e.B.Y)).ToArray();var maxY=edges.Select(e=>Math.Max(e.A.Y,e.B.Y)).ToArray();
        var byMinY=Enumerable.Range(0,edges.Length).OrderBy(i=>minY[i]).ToArray();
        var spanning=new List<int>();var strip=new List<(double X,int Edge)>();int next=0;
        for(int i=0;i+1<ys.Length;i++)
        { LoadingContext.Check();
            var y0=ys[i];var y1=ys[i+1];var mid=(y0+y1)/2;
            // Rotated copies of a shared vertex can differ by a handful of ULPs.
            // Such strips have no reliable interior and can pair the wrong edges.
            var ulp=Math.Max(Math.Abs(Math.BitIncrement(y0)-y0),Math.Abs(Math.BitIncrement(y1)-y1));
            if(y1-y0<=8*ulp)continue;
            while(next<byMinY.Length&&minY[byMinY[next]]<mid)spanning.Add(byMinY[next++]);
            spanning.RemoveAll(e=>!(mid<maxY[e]));
            strip.Clear();foreach(var e in spanning)strip.Add((X(edges[e],mid),e));
            CollectionsMarshal.AsSpan(strip).Sort();
            for(int j=0;j+1<strip.Count;j+=2)
            { LoadingContext.Check();
                var left=edges[strip[j].Edge];var right=edges[strip[j+1].Edge];
                var a=new Point(X(left,y0),y0);var b=new Point(X(right,y0),y0);
                var c=new Point(X(right,y1),y1);var d=new Point(X(left,y1),y1);
                result.AddRange([a,b,c,a,c,d]);
            }
        }
        return result;
    }

    // DXF TEXT control codes: \U+XXXX, %%d/%%p/%%c symbols, %%nnn character codes, %%% percent,
    // %%u/%%o underline/overline toggles (not drawn) and caret-encoded control characters (^I tab, "^ " caret).
    public static string DecodeText(string text) => Regex.Replace(Regex.Replace(text,@"\\U\+([0-9a-fA-F]{4})",
        m=>((char)int.Parse(m.Groups[1].Value,NumberStyles.HexNumber)).ToString()),
        @"%%(\d{3}|[dpcuo%])|\^(.)",m=>m.Groups[2].Success
            ? m.Groups[2].Value==" "?"^":m.Groups[2].Value[0] is >='@' and <='_'?" ":m.Value
            : char.ToLowerInvariant(m.Groups[1].Value[0]) switch
            {
                'd'=>"°",'p'=>"±",'c'=>"Ø",'%'=>"%",'u' or 'o'=>"",
                _=>((char)int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)).ToString(),
            },RegexOptions.IgnoreCase);

    public static List<Point[]> Text(string value,double height,double width,double oblique,int flags,
        int horizontal,int vertical,Point first,Point second,double rotation)
    {
        if(height<=0||width<=0||Math.Abs(oblique)>=85||horizontal is <0 or >5||vertical is <0 or >3)
            throw new System.IO.InvalidDataException("Invalid text dimensions or alignment.");
        // DXF SHX fonts are not available to WPF. Use a consistent installed TrueType substitute.
        var typeface=new Typeface("Arial");
        var text=new FormattedText(DecodeText(value),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,100,Brushes.White,1);
        var geometry=text.BuildGeometry(default).GetFlattenedPathGeometry(0.02,ToleranceType.Absolute);
        double cap=typeface.TryGetGlyphTypeface(out var glyph)?glyph.CapsHeight*100:72;
        var contours=new List<Point[]>();
        foreach(var figure in geometry.Figures)
        { LoadingContext.Check();
            var pts=new List<Point>{figure.StartPoint};
            foreach(var segment in figure.Segments)
                if(segment is PolyLineSegment poly)pts.AddRange(poly.Points);
                else if(segment is LineSegment line)pts.Add(line.Point);
            if(pts.Count>2)contours.Add(pts.Select(p=>new Point(p.X*height/cap,(text.Baseline-p.Y)*height/cap)).ToArray());
        }
        if(contours.Count==0)return [];
        var advance=text.WidthIncludingTrailingWhitespace*height/cap;
        var sx=width;var sy=1d;
        if(horizontal is 3 or 5 && vertical==0)
        {
            var delta=second-first;
            if(advance<=0||delta.Length<=0)throw new System.IO.InvalidDataException("Invalid aligned text.");
            sx=delta.Length/advance;sy=horizontal==3?sx:1;
            rotation=Math.Atan2(delta.Y,delta.X)*180/Math.PI;
        }
        var bounds=contours.SelectMany(p=>p).Aggregate(Rect.Empty,(r,p)=>{r.Union(p);return r;});
        var dx=horizontal switch{1 or 4=>-advance/2,2=>-advance,_=>0};
        var dy=vertical switch{1=>-bounds.Top,2=>-(bounds.Top+bounds.Bottom)/2,3=>-bounds.Bottom,_=>0};
        if(horizontal==4){dy=-(bounds.Top+bounds.Bottom)/2;}
        var anchor=horizontal is 0 or 3 or 5 && vertical==0?first:second;
        var matrix=Matrix.Identity;matrix.Rotate(rotation);matrix.Translate(anchor.X,anchor.Y);
        var shear=Math.Tan(oblique*Math.PI/180);
        return contours.Select(p=>p.Select(v=>
        {
            var x=(v.X+dx)*sx;var y=(v.Y+dy)*sy;
            x+=shear*y;
            if((flags&2)!=0)x=-x;if((flags&4)!=0)y=-y;
            return matrix.Transform(new Point(x,y));
        }).ToArray()).ToList();
    }

    /// <summary>Places an entity; <paramref name="category"/> (dimension/leader block content) overrides its own category.</summary>
    public static DxfEntity Transform(DxfEntity entity,Matrix matrix,string layer,string handle,EntityCategory? category=null,uint? color=null)
    {
        var u=matrix.Transform(new Vector(1,0));var v=matrix.Transform(new Vector(0,1));
        bool similarity=Math.Abs(u.Length-v.Length)<1e-10*Math.Max(1,u.Length)&&
            Math.Abs(Vector.Multiply(u,v))<1e-10*Math.Max(1,u.Length*v.Length);
        bool approximate=entity.IsApproximate;
        var curves=new List<Curve>();
        foreach(var c in entity.Curves)
        { LoadingContext.Check();
            if(c.IsUnbounded)
            {
                var origin=matrix.Transform(c.Start);var direction=matrix.Transform(c.End-c.Start);
                curves.Add(c.Extent==LineExtent.Ray?Curve.Ray(origin,direction):Curve.XLine(origin,direction));
            }
            else if(c.IsLine)curves.Add(Curve.Line(matrix.Transform(c.Start),matrix.Transform(c.End)));
            else if(similarity)
            {
                var center=matrix.Transform(c.Center);var start=matrix.Transform(c.Start)-center;
                curves.Add(Curve.Arc(center,c.Radius*u.Length,Math.Atan2(start.Y,start.X),c.Sweep*(matrix.Determinant<0?-1:1)));
            }
            else
            {
                // The largest singular value bounds transformed chord error, including shear.
                var stretch=Math.Sqrt(u.LengthSquared+v.LengthSquared);
                var step=2*Math.Acos(Math.Clamp(1-SplineGeometry.Tolerance/Math.Max(c.Radius*stretch,1e-20),-1,1));
                var required=Math.Max(1,Math.Ceiling(Math.Abs(c.Sweep)/Math.Max(step,1e-12)));
                if(required>200000)throw new System.IO.InvalidDataException("Transformed curve exceeds segment limit.");
                var count=(int)required;
                for(int i=0;i<count;i++)curves.Add(Curve.Line(matrix.Transform(c.At((double)i/count)),matrix.Transform(c.At((double)(i+1)/count))));
                approximate=true;
            }
        }
        var projectedFill=entity.FillTriangles==null?null:Array.AsReadOnly(entity.FillTriangles.Select(matrix.Transform).ToArray());
        var bounds=curves.Aggregate(Rect.Empty,(r,c)=>{r.Union(c.Bounds);return r;});
        foreach(var point in projectedFill??[])bounds.Union(point);
        if(bounds.IsEmpty&&!entity.Bounds.IsEmpty)bounds=Rect.Transform(entity.Bounds,matrix);
        DxfSpatialGeometry? spatial=null;string? spatialError=entity.SpatialGeometryError;
        try{spatial=entity.SpatialGeometry?.Transform(matrix,1,0);}catch(System.IO.InvalidDataException ex){spatialError=ex.Message;}
        return new DxfEntity(entity.Kind,layer,handle,curves)
        {
            FillContours=entity.FillContours.Select(p=>p.Select(matrix.Transform).ToArray()).ToList(),
            FillTriangles=projectedFill,
            Bounds=bounds,
            SpatialGeometry=spatial,SpatialGeometryError=spatialError,
            RegionContours=entity.RegionContours?.Select(p=>p.Select(matrix.Transform).ToArray()).ToList(),
            RenderCurves=entity.RenderCurves?.Select(c=>Curve.Line(matrix.Transform(c.Start),matrix.Transform(c.End))).ToList(),
            RenderDots=entity.RenderDots.Select(matrix.Transform).ToList(),
            Text=entity.Text,IsApproximate=approximate,InstancePath=handle,SourceHandle=entity.SourceHandle,ColorSource=entity.ColorSource,
            Category=category??entity.Category,Color=color??entity.Color
        };
    }
}
