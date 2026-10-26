using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

// Clip in the camera's XYZ coordinates before perspective division. Display vertices use
// reversed depth (near=1, far=0); the GPU camera pass must clear depth to zero and use GreaterEqual.
internal sealed class DxfCameraProjection
{
    private readonly DxfModelView view;
    private readonly Vector3D axisX,axisY,axisZ;
    private readonly double eye,lens,near;
    private readonly Plane[] planes;
    private readonly Plane[] rayPlanes;
    private readonly record struct Plane(double X,double Y,double Z,double Offset)
    {
        internal double At(Point3D p)=>X*p.X+Y*p.Y+Z*p.Z+Offset;
        internal double Along(Vector3D v)=>X*v.X+Y*v.Y+Z*v.Z;
    }
    private readonly record struct ClippedLine(Point3D Origin,Vector3D Direction,double First,double Last);
    private sealed record ClippedShape(DxfEntity Source,List<ClippedLine> Lines,List<DxfSpatialTriangle> Triangles,List<Point3D> Dots,bool Approximate);

    private DxfCameraProjection(DxfModelView camera,bool clipLateral)
    {
        view=camera;
        bool Finite(double n)=>double.IsFinite(n);
        eye=camera.Direction.Length;
        if(!new[]{eye,camera.Height,camera.AspectRatio,camera.Twist,camera.Center.X,camera.Center.Y,camera.Target.X,camera.Target.Y,camera.Target.Z,
            camera.FrontClip,camera.BackClip,camera.LensLength}.All(Finite)||eye<1e-20||camera.Height<=0||camera.AspectRatio<=0||camera.LensLength<=0)
            throw new InvalidDataException("Invalid saved camera parameters.");
        axisZ=camera.Direction;axisZ.Normalize();
        var x=Math.Abs(axisZ.X)<1d/64&&Math.Abs(axisZ.Y)<1d/64?Vector3D.CrossProduct(new(0,1,0),axisZ):Vector3D.CrossProduct(new(0,0,1),axisZ);
        x.Normalize();var y=Vector3D.CrossProduct(axisZ,x);y.Normalize();
        double angle=camera.Twist*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle);
        axisX=x*c-y*s;axisY=x*s+y*c;
        // Autodesk's paper/model perspective matrix uses the 42 mm film diagonal.
        // https://forums.autodesk.com/t5/net-forum/paper-space-to-model-space-conversion-matrix/td-p/2360506
        lens=camera.Height*camera.LensLength*Math.Sqrt(1+camera.AspectRatio*camera.AspectRatio)/42;
        if(!Finite(lens)||lens<=0)throw new InvalidDataException("Invalid perspective framing.");
        near=Math.Max(eye*1e-9,1e-300);
        var frame=camera.Frame;
        if(!new[]{frame.Left,frame.Right,frame.Top,frame.Bottom}.All(Finite))throw new InvalidDataException("Invalid camera frame.");
        var clip=new List<Plane>();var lateral=new List<Plane>();
        if(camera.Perspective)
        {
            clip.Add(new(0,0,-1,eye-near));
            lateral.Add(new(lens,0,frame.Left,-frame.Left*eye));
            lateral.Add(new(-lens,0,-frame.Right,frame.Right*eye));
            lateral.Add(new(0,lens,frame.Top,-frame.Top*eye));
            lateral.Add(new(0,-lens,-frame.Bottom,frame.Bottom*eye));
        }
        else
        {
            lateral.Add(new(1,0,0,-frame.Left));lateral.Add(new(-1,0,0,frame.Right));
            lateral.Add(new(0,1,0,-frame.Top));lateral.Add(new(0,-1,0,frame.Bottom));
        }
        if(camera.FrontClipping)clip.Add(new(0,0,-1,camera.FrontAtEye?eye:camera.FrontClip));
        if(camera.BackClipping)clip.Add(new(0,0,1,-camera.BackClip));
        rayPlanes=clip.Concat(lateral).ToArray();
        planes=clipLateral?rayPlanes:clip.ToArray();
    }

    private Point3D Dcs(Point3D point)
    {
        var v=point-view.Target;
        var p=new Point3D(Vector3D.DotProduct(v,axisX),Vector3D.DotProduct(v,axisY),Vector3D.DotProduct(v,axisZ));
        if(!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Z))throw new InvalidDataException("Nonfinite camera coordinates.");
        return p;
    }
    private bool ClipLine(DxfSpatialLine input,out ClippedLine output)
    {
        var p=Dcs(input.Start);var d=Dcs(input.End)-p;
        double first=input.Extent==LineExtent.Infinite?double.NegativeInfinity:0,last=input.Extent==LineExtent.Segment?1:double.PositiveInfinity;
        foreach(var plane in input.Extent==LineExtent.Segment?planes:rayPlanes)
        {
            double value=plane.At(p),delta=plane.Along(d);
            if(!double.IsFinite(value)||!double.IsFinite(delta))throw new InvalidDataException("Excessive camera coordinates.");
            if(delta==0){if(value<0){output=default;return false;}continue;}
            double t=-value/delta;
            if(delta>0)first=Math.Max(first,t);else last=Math.Min(last,t);
            if(first>last){output=default;return false;}
        }
        output=new(p,d,first,last);return true;
    }
    private IEnumerable<DxfSpatialTriangle> ClipTriangle(DxfSpatialTriangle triangle)
    {
        var polygon=new List<Point3D>{Dcs(triangle.A),Dcs(triangle.B),Dcs(triangle.C)};
        foreach(var plane in planes)
        {
            if(polygon.Count==0)yield break;
            var next=new List<Point3D>();var previous=polygon[^1];double before=plane.At(previous);
            foreach(var point in polygon)
            {
                double after=plane.At(point);
                if(!double.IsFinite(before)||!double.IsFinite(after))throw new InvalidDataException("Excessive camera coordinates.");
                if((before>=0)!=(after>=0))
                {
                    double scale=Math.Max(Math.Abs(before),Math.Abs(after));
                    double t=(before/scale)/(before/scale-after/scale);
                    next.Add(new(previous.X*(1-t)+point.X*t,previous.Y*(1-t)+point.Y*t,previous.Z*(1-t)+point.Z*t));
                }
                if(after>=0)next.Add(point);
                previous=point;before=after;
            }
            polygon=next;
        }
        for(int i=1;i+1<polygon.Count;i++)yield return new(polygon[0],polygon[i],polygon[i+1]);
    }
    private ClippedShape Clip(DxfEntity source,ref long work)
    {
        var spatial=source.SpatialGeometry??DxfSpatialGeometry.FromPlanar(source,0);
        var lines=new List<ClippedLine>();var triangles=new List<DxfSpatialTriangle>();var dots=new List<Point3D>();
        void CheckBudget()
        {
            // The outer loop accounts aggregate work across all source entities and saved cameras.
            LoadingContext.Check();
        }
        foreach(var line in spatial.Lines)
        {
            CheckBudget();if(++work>4_000_000)throw new InvalidDataException("Camera display geometry exceeds its aggregate limit.");
            if(ClipLine(line,out var clipped))lines.Add(clipped);
        }
        foreach(var triangle in spatial.Triangles)
        {
            CheckBudget();if(++work>4_000_000)throw new InvalidDataException("Camera display geometry exceeds its aggregate limit.");
            foreach(var clipped in ClipTriangle(triangle))
            {
                if(++work>4_000_000)throw new InvalidDataException("Camera display geometry exceeds its aggregate limit.");
                triangles.Add(clipped);
            }
        }
        foreach(var dot in spatial.Dots)
        {
            CheckBudget();if(++work>4_000_000)throw new InvalidDataException("Camera display geometry exceeds its aggregate limit.");
            var p=Dcs(dot);if(planes.All(plane=>plane.At(p)>=0))dots.Add(p);
        }
        return new(source,lines,triangles,dots,spatial.Approximate||view.Perspective);
    }

    internal static IReadOnlyList<DxfEntity> Prepare(DxfModelView view,IReadOnlyList<DxfEntity> entities,ref long work,bool clipLateral=true)
    {
        var camera=new DxfCameraProjection(view,clipLateral);var shapes=new List<ClippedShape>();
        foreach(var entity in entities){LoadingContext.Check();shapes.Add(camera.Clip(entity,ref work));}
        double low=double.PositiveInfinity,high=double.NegativeInfinity;
        void Include(Point3D p){low=Math.Min(low,p.Z);high=Math.Max(high,p.Z);}
        foreach(var shape in shapes)
        {
            foreach(var line in shape.Lines)
            {
                if(double.IsFinite(line.First))Include(line.Origin+line.Direction*line.First);
                if(double.IsFinite(line.Last))Include(line.Origin+line.Direction*line.Last);
            }
            foreach(var t in shape.Triangles){Include(t.A);Include(t.B);Include(t.C);}
            foreach(var p in shape.Dots)Include(p);
        }
        if(!double.IsFinite(low)||!double.IsFinite(high)){low=-1;high=1;}
        if(high-low<1e-12*Math.Max(1,Math.Max(Math.Abs(high),Math.Abs(low))))
        {double pad=Math.Max(1,Math.Max(Math.Abs(high),Math.Abs(low)))*1e-6;low-=pad;high+=pad;}
        return Array.AsReadOnly(shapes.Select(shape=>camera.Project(shape,low,high)).ToArray());
    }
    internal static DxfEntity Project(DxfEntity source,DxfModelView view)
    {long work=0;return Prepare(view,new[]{source},ref work)[0];}

    private DxfEntity Project(ClippedShape shape,double low,double high)
    {
        Point3D Vertex(Point3D p)
        {
            double factor=view.Perspective?lens/(eye-p.Z):1;
            double depth=view.Perspective?near/(eye-p.Z):(p.Z-low)/(high-low);
            var result=new Point3D(p.X*factor,p.Y*factor,Math.Clamp(depth,0,1));
            if(!double.IsFinite(result.X)||!double.IsFinite(result.Y)||!double.IsFinite(result.Z))throw new InvalidDataException("Invalid projected camera geometry.");
            return result;
        }
        Point3D End(ClippedLine line,double t)
        {
            if(double.IsFinite(t))return Vertex(line.Origin+line.Direction*t);
            // A visible infinite perspective endpoint is its vanishing point, with zero reversed depth.
            if(view.Perspective&&line.Direction.Z!=0)return new(-lens*line.Direction.X/line.Direction.Z,-lens*line.Direction.Y/line.Direction.Z,0);
            // Orthographic lines parallel to the viewing axis collapse to one screen point.
            var p=Vertex(line.Origin);return new(p.X,p.Y,(t>0)==(line.Direction.Z>0)?1:0);
        }
        var lines=new List<DxfSpatialLine>();var triangles=new List<DxfSpatialTriangle>();var dots=new List<Point3D>();
        foreach(var line in shape.Lines)
        {
            LoadingContext.Check();var a=End(line,line.First);var b=End(line,line.Last);
            if(a.X==b.X&&a.Y==b.Y)dots.Add(a.Z>b.Z?a:b);else lines.Add(new(a,b));
        }
        foreach(var t in shape.Triangles){LoadingContext.Check();triangles.Add(new(Vertex(t.A),Vertex(t.B),Vertex(t.C)));}
        foreach(var p in shape.Dots)dots.Add(Vertex(p));
        var display=new DxfSpatialGeometry(lines,triangles,dots,shape.Approximate);
        var curves=lines.Select(l=>Curve.Line(new(l.Start.X,l.Start.Y),new(l.End.X,l.End.Y))).ToArray();
        Rect bounds=Rect.Empty;
        void Bound(Point3D p)=>bounds.Union(new Point(p.X,p.Y));
        foreach(var l in lines){Bound(l.Start);Bound(l.End);}foreach(var t in triangles){Bound(t.A);Bound(t.B);Bound(t.C);}foreach(var p in dots)Bound(p);
        var source=shape.Source;
        return new DxfEntity(source.Kind,source.Layer,source.Handle,curves)
        {
            Bounds=bounds,DisplayGeometry3D=display,SpatialGeometry=source.SpatialGeometry,Group=source.Group,Color=source.Color,Category=source.Category,
            ColorSource=source.ColorSource,SourceHandle=source.SourceHandle,InstancePath=source.InstancePath,Text=source.Text,
            RenderDots=dots.Select(p=>new Point(p.X,p.Y)).ToArray(),IsApproximate=shape.Approximate
        };
    }
}
