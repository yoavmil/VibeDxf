using System.Globalization;
using System.IO;


namespace VibeDxf.Core;

// All boundary flattening and pattern clipping happens once at load time.
internal static class DxfHatch
{
    private const int Limit=250000;
    private sealed class Cursor(List<(int Code,string Value)> tags,CancellationToken cancellationToken)
    {
        public int Index;
        public int Code=>Index<tags.Count?tags[Index].Code:-1;
        public double Take(int code)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(Code!=code)throw new InvalidDataException($"Expected hatch group {code}, found {Code}.");
            if(!double.TryParse(tags[Index++].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))
                throw new InvalidDataException("Invalid hatch number.");
            return n;
        }
        public int Count(int code)
        {
            var n=Take(code);if(n<0||n>Limit||n!=Math.Floor(n))throw new InvalidDataException("Invalid hatch count.");return (int)n;
        }
        public Point Point(int x=10,int y=20)=>new(Take(x),Take(y));
        public void Seek(int code){while(Index<tags.Count&&Code!=code)Index++;if(Code!=code)throw new InvalidDataException("Missing hatch data.");}
        public void References(){if(Code==97){var count=Count(97);for(int i=0;i<count;i++){if(Code!=330)throw new InvalidDataException("Invalid hatch source handles.");Index++;}}}
    }
    private static Point[] Flatten(List<Curve> curves,CancellationToken cancellationToken)
    {
        var pts=new List<Point>();
        foreach(var c in curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count=1;
            if(!c.IsLine)
            {
                var step=2*Math.Acos(Math.Clamp(1-SplineGeometry.Tolerance/c.Radius,-1,1));
                var required=Math.Ceiling(Math.Abs(c.Sweep)/Math.Max(step,1e-12));
                if(required>Limit)throw new InvalidDataException("Hatch boundary limit.");count=Math.Max(1,(int)required);
            }
            if(pts.Count>0&&(pts[^1]-c.Start).Length>1e-6)throw new InvalidDataException("Open hatch edge path.");
            if(pts.Count==0)pts.Add(c.Start);
            for(int i=1;i<=count;i++)pts.Add(c.At((double)i/count));
            if(pts.Count>Limit)throw new InvalidDataException("Hatch boundary limit.");
        }
        if(pts.Count<4||(pts[0]-pts[^1]).Length>1e-6)throw new InvalidDataException("Open or degenerate hatch boundary.");
        pts.RemoveAt(pts.Count-1);return pts.ToArray();
    }
    private static Curve Bulge(Point a,Point b,double bulge)
    {
        if(Math.Abs(bulge)<1e-12||a==b)return Curve.Line(a,b);
        var chord=b-a;var center=a+chord/2+new Vector(-chord.Y,chord.X)*((1-bulge*bulge)/(4*bulge));
        return Curve.Arc(center,(a-center).Length,Math.Atan2(a.Y-center.Y,a.X-center.X),4*Math.Atan(bulge));
    }
    public static DxfEntity Read(List<(int Code,string Value)> tags,string layer,string handle,CancellationToken cancellationToken=default)
    {
        double N(int code,double fallback=0)=>tags.FirstOrDefault(t=>t.Code==code) is var t&&t.Value!=null?
            double.Parse(t.Value,CultureInfo.InvariantCulture):fallback;
        if(N(450)!=0)throw new NotSupportedException("HATCH (gradient)");
        var cursor=new Cursor(tags,cancellationToken);cursor.Seek(91);var count=cursor.Count(91);
        if(count==0)throw new InvalidDataException("Empty hatch.");
        var paths=new List<(List<Curve> Curves,Point[] Polygon)>();bool approximate=false;
        for(int path=0;path<count;path++)
        {
            var flags=cursor.Count(92);var curves=new List<Curve>();
            if((flags&2)!=0)
            {
                cursor.Take(72);cursor.Take(73);var vertices=cursor.Count(93);
                var points=new List<(Point P,double Bulge)>();
                for(int i=0;i<vertices;i++){var p=cursor.Point();points.Add((p,cursor.Code==42?cursor.Take(42):0));}
                for(int i=0;i<points.Count;i++)curves.Add(Bulge(points[i].P,points[(i+1)%points.Count].P,points[i].Bulge));
            }
            else
            {
                var edges=cursor.Count(93);
                for(int i=0;i<edges;i++)
                {
                    int type=cursor.Count(72);
                    if(type==1)curves.Add(Curve.Line(cursor.Point(),cursor.Point(11,21)));
                    else if(type is 2 or 3)
                    {
                        var center=cursor.Point();var major=type==3?(Vector)cursor.Point(11,21):default;
                        var radiusOrRatio=cursor.Take(40);var start=cursor.Take(50);var end=cursor.Take(51);var ccw=cursor.Take(73)!=0;
                        if(!ccw){start=360-start;end=360-end;}
                        var sweep=ccw?Curve.Wrap((end-start)*Math.PI/180):-Curve.Wrap((start-end)*Math.PI/180);
                        if(Math.Abs(sweep)<1e-12)sweep=ccw?Math.Tau:-Math.Tau;
                        if(type==2)
                        {
                            if(radiusOrRatio<=0)throw new InvalidDataException("Invalid hatch arc radius.");
                            curves.Add(Curve.Arc(center,radiusOrRatio,start*Math.PI/180,sweep));
                        }
                        else
                        {
                            if(major.Length==0||radiusOrRatio<=0||radiusOrRatio>1)throw new InvalidDataException("Invalid hatch ellipse.");
                            // Ellipse edge angles are geometric angles, not ellipse parameters.
                            double Param(double degrees)=>Math.Atan2(Math.Sin(degrees*Math.PI/180)/radiusOrRatio,Math.Cos(degrees*Math.PI/180));
                            var t0=Param(start);var t1=Param(end);var span=ccw?Curve.Wrap(t1-t0):-Curve.Wrap(t0-t1);
                            if(Math.Abs(span)<1e-12)span=ccw?Math.Tau:-Math.Tau;
                            var unit=Curve.Arc(default,major.Length,t0,span);
                            var flattened=FlattenOpen(unit);
                            var u=major/major.Length;var v=new Vector(-u.Y,u.X)*radiusOrRatio;
                            Point Map(Point p)=>center+p.X*u+p.Y*v;
                            curves.AddRange(flattened.Select(c=>Curve.Line(Map(c.Start),Map(c.End))));approximate=true;
                        }
                    }
                    else if(type==4)
                    {
                        int degree=cursor.Count(94);cursor.Take(73);cursor.Take(74);
                        int nk=cursor.Count(95),np=cursor.Count(96);var knots=new double[nk];var points=new Point[np];var weights=new List<double>();
                        for(int j=0;j<nk;j++)knots[j]=cursor.Take(40);
                        for(int j=0;j<np;j++){points[j]=cursor.Point();if(cursor.Code==42)weights.Add(cursor.Take(42));}
                        while(cursor.Code==42)weights.Add(cursor.Take(42));
                        curves.AddRange(SplineGeometry.Flatten(degree,points,knots,weights.Count==0?null:weights.ToArray(),cancellationToken));
                        // Optional fit data (not needed for evaluation), followed by optional tangents.
                        if(cursor.Code==97)
                        {
                            var saved=cursor.Index;var fits=cursor.Count(97);
                            if(fits==0||cursor.Code==11){for(int j=0;j<fits;j++)cursor.Point(11,21);}
                            else cursor.Index=saved;
                        }
                        if(cursor.Code==12)cursor.Point(12,22);if(cursor.Code==13)cursor.Point(13,23);
                        approximate=true;
                    }
                    else throw new NotSupportedException("HATCH (boundary edge type)");
                }
            }
            cursor.References();var polygon=Flatten(curves,cancellationToken);approximate|=curves.Any(c=>!c.IsLine);
            paths.Add((curves,polygon));
        }
        int style=(int)N(75);if(style is <0 or >2)throw new InvalidDataException("Invalid hatch island style.");
        var selected=paths.Where(p=>
        {
            int depth=paths.Count(other=>!ReferenceEquals(p.Polygon,other.Polygon)&&Inside(other.Polygon,p.Polygon[0]));
            return style==0||depth<=(style==1?1:0);
        }).ToArray();
        var contours=selected.Select(p=>p.Polygon).ToList();var boundary=selected.SelectMany(p=>p.Curves).ToList();
        var pattern=new List<Curve>();var dots=new List<Point>();bool solid=N(70)==1;
        if(!solid)
        {
            cursor.Seek(78);var definitions=cursor.Count(78);if(definitions==0)throw new NotSupportedException("HATCH (missing pattern definition)");
            for(int i=0;i<definitions;i++)
            {
                var angle=cursor.Take(53)*Math.PI/180;var origin=cursor.Point(43,44);var offset=(Vector)cursor.Point(45,46);
                int n=cursor.Count(79);var dash=new double[n];for(int j=0;j<n;j++)dash[j]=cursor.Take(49);
                Pattern(contours,angle,origin,offset,dash,pattern,dots,cancellationToken);
            }
        }
        return new DxfEntity("HATCH",layer,handle,boundary){FillContours=solid?contours:[],RegionContours=contours,
            RenderCurves=pattern,RenderDots=dots,IsApproximate=approximate};
    }
    private static List<Curve> FlattenOpen(Curve c)
    {
        var step=2*Math.Acos(Math.Clamp(1-SplineGeometry.Tolerance/c.Radius,-1,1));
        var required=Math.Ceiling(Math.Abs(c.Sweep)/Math.Max(step,1e-12));
        if(required>Limit)throw new InvalidDataException("Hatch curve limit.");
        int count=Math.Max(1,(int)required);
        return Enumerable.Range(0,count).Select(i=>Curve.Line(c.At((double)i/count),c.At((double)(i+1)/count))).ToList();
    }
    private static bool Inside(Point[] polygon,Point p)
    {
        bool inside=false;
        for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
        {var a=polygon[i];var b=polygon[j];if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}
        return inside;
    }
    private static void Pattern(List<Point[]> contours,double angle,Point origin,Vector offset,double[] dash,List<Curve> output,List<Point> dots,CancellationToken cancellationToken)
    {
        var direction=new Vector(Math.Cos(angle),Math.Sin(angle));var normal=new Vector(-direction.Y,direction.X);
        var spacing=Vector.Multiply(offset,normal);if(Math.Abs(spacing)<1e-12)throw new InvalidDataException("Zero hatch spacing.");
        var edges=contours.SelectMany(p=>p.Select((a,i)=>(A:a,B:p[(i+1)%p.Length]))).ToArray();
        var distances=contours.SelectMany(p=>p).Select(p=>Vector.Multiply(p-origin,normal)/spacing).ToArray();
        double lo=Math.Ceiling(distances.Min()),hi=Math.Floor(distances.Max());
        if(!double.IsFinite(lo)||!double.IsFinite(hi)||hi-lo>Limit||(hi-lo+1)*edges.Length>20000000)
            throw new NotSupportedException("HATCH (pattern density limit)");
        var period=dash.Sum(Math.Abs);if(dash.Length>0&&period<=1e-12)throw new InvalidDataException("Zero dash period.");
        long work=0;
        for(double k=lo;k<=hi;k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at=origin+k*offset;var intersections=new List<double>();
            foreach(var edge in edges)
            {
                var a=Vector.Multiply(edge.A-at,normal);var b=Vector.Multiply(edge.B-at,normal);
                if((a>0)==(b>0))continue;
                var point=edge.A+(edge.B-edge.A)*(a/(a-b));intersections.Add(Vector.Multiply(point-at,direction));
            }
            intersections.Sort();
            for(int i=0;i+1<intersections.Count;i+=2)
            {
                var begin=intersections[i];var end=intersections[i+1];
                if(dash.Length==0){output.Add(Curve.Line(at+begin*direction,at+end*direction));continue;}
                var cycle=Math.Floor(begin/period)*period;
                for(double t=cycle;t<=end;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var previous=t;
                    foreach(var item in dash)
                    {
                        if(++work>Limit)throw new NotSupportedException("HATCH (pattern density limit)");
                        var length=Math.Abs(item);
                        if(item==0&&t>=begin&&t<=end)dots.Add(at+t*direction);
                        else if(item>0&&t+length>begin&&t<end)output.Add(Curve.Line(at+Math.Max(begin,t)*direction,at+Math.Min(end,t+length)*direction));
                        t+=length;
                    }
                    if(t<=previous)throw new InvalidDataException("Hatch dash precision limit.");
                }
            }
            if(output.Count+dots.Count>Limit)throw new NotSupportedException("HATCH (pattern density limit)");
            if(k+1==k)throw new InvalidDataException("Hatch spacing precision limit.");
        }
    }
}
