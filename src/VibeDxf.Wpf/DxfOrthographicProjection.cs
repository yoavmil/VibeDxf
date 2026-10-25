using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

// DXF DCS uses the arbitrary-axis basis of the WCS view direction.
// Autodesk VIEWPORT (16/26/36, 17/27/37) and arbitrary-axis algorithm references.
internal static class DxfOrthographicProjection
{
    internal static Func<Point3D,Point> Create(Vector3D direction, Point3D target)
    {
        bool Finite(double x)=>double.IsFinite(x);
        if(!Finite(direction.X)||!Finite(direction.Y)||!Finite(direction.Z)||!Finite(direction.Length)||direction.Length<1e-20||
            !Finite(target.X)||!Finite(target.Y)||!Finite(target.Z))throw new InvalidDataException("Invalid viewport direction or target.");
        direction.Normalize();
        var x=Math.Abs(direction.X)<1d/64&&Math.Abs(direction.Y)<1d/64
            ?Vector3D.CrossProduct(new(0,1,0),direction):Vector3D.CrossProduct(new(0,0,1),direction);
        x.Normalize();var y=Vector3D.CrossProduct(direction,x);y.Normalize();
        return p=>
        {
            var v=p-target;var result=new Point(Vector3D.DotProduct(v,x),Vector3D.DotProduct(v,y));
            if(!Finite(result.X)||!Finite(result.Y))throw new InvalidDataException("Nonfinite projected geometry.");
            return result;
        };
    }
    internal static DxfEntity Planar(DxfEntity entity, Func<Point3D,Point> project, double elevation)
    {
        var origin=project(new(0,0,elevation));var x=project(new(1,0,elevation))-origin;var y=project(new(0,1,elevation))-origin;
        var matrix=new Matrix(x.X,x.Y,y.X,y.Y,origin.X,origin.Y);
        var collapsed=entity.Curves.Where(c=>c.IsUnbounded&&matrix.Transform(c.End-c.Start).Length<1e-12).ToHashSet();
        if(collapsed.Count>0)
            entity=new DxfEntity(entity.Kind,entity.Layer,entity.Handle,entity.Curves.Select(c=>collapsed.Contains(c)?Curve.Line(c.Start,c.Start):c))
            {
                RenderCurves=entity.RenderCurves,RenderDots=entity.RenderDots.Concat(collapsed.Select(c=>c.Start)).ToArray(),
                FillContours=entity.FillContours,RegionContours=entity.RegionContours,Text=entity.Text,
                SourceHandle=entity.SourceHandle,ColorSource=entity.ColorSource,Category=entity.Category,Color=entity.Color,IsApproximate=entity.IsApproximate
            };
        return DxfPrimitives.Transform(entity,matrix,entity.Layer,entity.Handle);
    }
    internal static IReadOnlyList<DxfEntity> Prepare(DxfViewportWindow viewport, IEnumerable<DxfEntity> entities,ref long count)
    {
        var output=new List<DxfEntity>();
        foreach(var entity in entities)
        {
            LoadingContext.Check();
            var projected=entity.Projection3D?.Invoke(viewport.ProjectPoint3D!)??Planar(entity,viewport.ProjectPoint3D!,0);
            projected.Group=entity.Group;
            count+=projected.Curves.Count+projected.RenderDots.Count+projected.FillContours.Sum(c=>c.Count);
            if(count>4_000_000)throw new InvalidDataException("Orthographic viewport projected geometry exceeds its limit.");
            if(projected.FillContours.Count>0)projected.FillTriangles=Array.AsReadOnly(DxfPrimitives.Triangulate(projected.FillContours).ToArray());
            output.Add(projected);
        }
        return Array.AsReadOnly(output.ToArray());
    }
}
