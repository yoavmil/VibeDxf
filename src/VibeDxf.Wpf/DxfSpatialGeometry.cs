using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

internal readonly record struct DxfSpatialLine(Point3D Start,Point3D End,LineExtent Extent=LineExtent.Segment);
internal readonly record struct DxfSpatialTriangle(Point3D A,Point3D B,Point3D C);

// Immutable display geometry; the existing analytic XY curves remain the query geometry.
internal sealed class DxfSpatialGeometry
{
    internal const int MaxPrimitives=1_000_000;
    internal IReadOnlyList<DxfSpatialLine> Lines {get;}
    internal IReadOnlyList<DxfSpatialTriangle> Triangles {get;}
    internal IReadOnlyList<Point3D> Dots {get;}
    internal bool Approximate {get;}

    internal DxfSpatialGeometry(IEnumerable<DxfSpatialLine> lines,IEnumerable<DxfSpatialTriangle> triangles,IEnumerable<Point3D> dots,bool approximate=false)
    {
        int count=0;
        IReadOnlyList<T> Freeze<T>(IEnumerable<T> source,Action<T> validate)
        {
            var result=new List<T>();
            foreach(var item in source)
            {
                if(++count>MaxPrimitives)throw new InvalidDataException("Spatial display geometry exceeds its primitive limit.");
                if((count&255)==0)LoadingContext.Check();
                validate(item);result.Add(item);
            }
            return Array.AsReadOnly(result.ToArray());
        }
        LoadingContext.Check();
        Lines=Freeze(lines,l=>
        {
            Check(l.Start);Check(l.End);
            if(!Enum.IsDefined(l.Extent)||(l.Extent!=LineExtent.Segment&&l.Start==l.End))throw new InvalidDataException("Invalid spatial line domain.");
        });
        Triangles=Freeze(triangles,t=>{Check(t.A);Check(t.B);Check(t.C);});
        Dots=Freeze(dots,Check);Approximate=approximate;
    }
    private static void Check(Point3D p)
    {
        if(!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Z))throw new InvalidDataException("Nonfinite spatial display geometry.");
    }

    internal static DxfSpatialGeometry FromPlanar(DxfEntity entity,double elevation)
    {
        if(!double.IsFinite(elevation))throw new InvalidDataException("Invalid display elevation.");
        Point3D Lift(Point p)=>new(p.X,p.Y,elevation);
        var lines=new List<DxfSpatialLine>();var dots=entity.RenderDots.Select(Lift).ToList();
        bool approximate=entity.IsApproximate;
        if(entity.Kind=="POINT")
        {
            if(entity.Curves.Count>0)dots.Add(Lift(entity.Curves[0].Start));
        }
        else foreach(var curve in entity.RenderCurves??entity.Curves)
        {
            LoadingContext.Check();
            if(curve.IsLine)
            {
                lines.Add(new(Lift(curve.Start),Lift(curve.End),curve.Extent));
            }
            else
            {
                if(!double.IsFinite(curve.Radius)||curve.Radius<=0||!double.IsFinite(curve.Angle)||!double.IsFinite(curve.Sweep))
                    throw new InvalidDataException("Invalid spatial arc.");
                // Stable sagitta bound: r*(1-cos(h/2)) <= tolerance, without subtracting nearly equal doubles.
                double step=4*Math.Asin(Math.Sqrt(Math.Min(1,SplineGeometry.Tolerance/(2*curve.Radius))));
                double required=Math.Max(1,Math.Ceiling(Math.Abs(curve.Sweep)/Math.Max(step,1e-15)));
                if(!double.IsFinite(required)||required>200_000||required>MaxPrimitives-lines.Count)
                    throw new InvalidDataException("Spatial arc exceeds its display segment limit.");
                int count=(int)required;var previous=curve.Start;
                for(int i=1;i<=count;i++)
                {
                    if((i&255)==0)LoadingContext.Check();
                    var next=curve.At((double)i/count);lines.Add(new(Lift(previous),Lift(next)));previous=next;
                }
                approximate=true;
            }
            if(lines.Count+dots.Count>MaxPrimitives)throw new InvalidDataException("Spatial display geometry exceeds its primitive limit.");
        }
        var fill=entity.FillTriangles??DxfPrimitives.Triangulate(entity.FillContours);
        if(fill.Count%3!=0||fill.Count/3>MaxPrimitives-lines.Count-dots.Count)throw new InvalidDataException("Invalid or excessive spatial fill geometry.");
        var triangles=new List<DxfSpatialTriangle>(fill.Count/3);
        for(int i=0;i<fill.Count;i+=3)
        {
            if((i&255)==0)LoadingContext.Check();
            triangles.Add(new(Lift(fill[i]),Lift(fill[i+1]),Lift(fill[i+2])));
        }
        return new(lines,triangles,dots,approximate);
    }

    internal DxfSpatialGeometry Transform(Matrix xy,double zScale,double zOffset)
    {
        if(!new[]{xy.M11,xy.M12,xy.M21,xy.M22,xy.OffsetX,xy.OffsetY,zScale,zOffset}.All(double.IsFinite))
            throw new InvalidDataException("Invalid spatial placement.");
        Point3D Map(Point3D p)
        {
            var q=xy.Transform(new Point(p.X,p.Y));return new(q.X,q.Y,p.Z*zScale+zOffset);
        }
        return new(Lines.Select(l=>new DxfSpatialLine(Map(l.Start),Map(l.End),l.Extent)),
            Triangles.Select(t=>new DxfSpatialTriangle(Map(t.A),Map(t.B),Map(t.C))),Dots.Select(Map),Approximate);
    }
}
