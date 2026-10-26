using System.IO;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

// Explicit ACIS spline boundaries. No surface intersection is inferred from endpoints.
internal static partial class DxfSolidProjection
{
    private static (DxfSpatialSpline Geometry,bool Reversed) ReadSpline(Node node,bool binary)
    {
        if(!binary)throw Unsupported("ASCII ACIS intersection spline layout");
        int i=1;
        Token Take()=>At(node,i++);
        bool Sense(){var t=Take();return t.Value is bool b?b:throw Invalid();}
        int Integer(){var t=Take();return t.Value is int n&&t.Kind is 4 or 21?n:throw Invalid();}
        double Number()=>Real(Take());
        void Text(string value){if(Take().Value is not string s||s!=value)throw Unsupported("ACIS intersection spline profile");}
        bool reversed=Sense();
        if(Take().Kind!=15)throw Invalid();
        Text("exact_int_cur");int version=Integer();
        if(version!=20900||Integer()!=0)throw Unsupported("ACIS intersection spline version");
        var type=Take().Value as string;
        if(type is not ("nubs" or "nurbs"))throw Unsupported("ACIS intersection spline representation");
        int degree=Integer(),form=Integer(),count=Integer();
        if(degree is <1 or >16||form!=0||count is <2 or >4096)throw Unsupported("ACIS intersection spline degree or closure");
        var knots=new List<double>();double previous=double.NegativeInfinity;
        for(int k=0;k<count;k++)
        {
            double knot=Number();int multiplicity=Integer();
            if(knot<=previous||multiplicity<1||multiplicity>degree||((k==0||k==count-1)&&multiplicity!=degree))throw Invalid();
            previous=knot;
            if(k==0)knots.Add(knot); // ACIS stores endpoint multiplicity p, standard NURBS uses p+1.
            for(int j=0;j<multiplicity;j++)knots.Add(knot);
            if(k==count-1)knots.Add(knot);
        }
        int controls=knots.Count-degree-1;
        if(controls<=degree||controls>65536)throw Invalid();
        var points=new Point3D[controls];var weights=new double[controls];
        for(int k=0;k<controls;k++)
        {LoadingContext.Check();points[k]=new(Number(),Number(),Number());weights[k]=type=="nurbs"?Number():1;}
        if(Number()!=0)throw Unsupported("nonzero ACIS intersection spline fit tolerance");
        Text("null_surface");Text("null_surface");Text("nullbs");Text("nullbs");
        if(Sense()||Sense())throw Unsupported("ACIS intersection spline extensions");
        // Saved discontinuity parameter lists do not modify the explicit curve.
        for(int list=0;list<4;list++)
        {int n=Integer();if(n<0||n>4096)throw Invalid();for(int k=0;k<n;k++)Number();}
        if(!Sense()||Number()!=1||!Sense()||Number()!=0||Integer()!=0||Integer()!=0)throw Unsupported("ACIS intersection spline parameter mapping");
        if(Take().Kind!=16||Sense()||Sense()||i!=node.Data.Length)throw Unsupported("ACIS intersection spline trailing fields");
        return (new DxfSpatialSpline(degree,points,knots,weights),reversed);
    }
}

// Positive-weight rational Bezier convex hulls bound XYZ chord error after body placement.
internal sealed class DxfSpatialSpline
{
    private readonly int degree;
    private readonly Point3D[] points;
    private readonly double[] knots,weights;
    private readonly record struct H(Vector3D V,double W)
    {
        internal Point3D Point=>(Point3D)(V/W);
        internal static H Blend(H a,H b,double t)=>new(a.V*(1-t)+b.V*t,a.W*(1-t)+b.W*t);
    }
    internal DxfSpatialSpline(int degree,IReadOnlyList<Point3D> points,IReadOnlyList<double> knots,IReadOnlyList<double> weights)
    {
        if(degree is <1 or >16||points.Count<=degree||points.Count>65536||knots.Count!=points.Count+degree+1||weights.Count!=points.Count||
            points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Z))||
            knots.Any(k=>!double.IsFinite(k))||knots.Zip(knots.Skip(1)).Any(k=>k.First>k.Second)||
            weights.Any(w=>!double.IsFinite(w)||w<=0)||knots[degree]>=knots[points.Count])throw new InvalidDataException("Invalid spatial spline.");
        this.degree=degree;this.points=points.ToArray();this.knots=knots.ToArray();this.weights=weights.ToArray();
    }
    internal Point3D Evaluate(double parameter)
    {
        LoadingContext.Check();
        if(!double.IsFinite(parameter)||parameter<knots[degree]||parameter>knots[points.Length])throw new InvalidDataException("Spatial spline parameter outside its domain.");
        int span=parameter==knots[points.Length]?points.Length-1:Array.FindLastIndex(knots,k=>k<=parameter);
        var origin=points[0];double max=weights.Max();var work=new H[degree+1];
        for(int j=0;j<=degree;j++){int k=span-degree+j;double w=weights[k]/max;work[j]=new((points[k]-origin)*w,w);}
        for(int r=1;r<=degree;r++)for(int j=degree;j>=r;j--)
        {int k=span-degree+j;work[j]=H.Blend(work[j-1],work[j],(parameter-knots[k])/(knots[k+degree-r+1]-knots[k]));}
        return origin+(Vector3D)work[degree].Point;
    }
    internal List<DxfSpatialLine> Flatten(double first,double last,Func<Point3D,Point3D> place,double tolerance=SplineGeometry.Tolerance)
    {
        LoadingContext.Check();
        bool reversed=last<first;double start=Math.Min(first,last),end=Math.Max(first,last);
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<knots[degree]||end>knots[points.Length]||start>=end||!double.IsFinite(tolerance)||tolerance<=0)throw new InvalidDataException("Spatial spline trim outside its domain or invalid tolerance.");
        var origin=place(points[0]);double max=weights.Max();
        var controls=points.Select((p,i)=>{double w=weights[i]/max;return new H((place(p)-origin)*w,w);}).ToList();
        var u=knots.ToList();long work=0;
        foreach(double knot in knots.Where(k=>k>=start&&k<=end).Append(start).Append(end).Distinct().Order())
        {
            LoadingContext.Check();int multiplicity=u.Count(k=>k==knot);
            if(multiplicity>degree+1)throw new InvalidDataException("Invalid spatial spline knot multiplicity.");
            while(multiplicity<degree)
            {
                LoadingContext.Check();
                if((work+=controls.Count)>8_000_000)throw new InvalidDataException("Spatial spline knot insertion work limit.");
                int k=u.FindLastIndex(v=>v<=knot);var next=new H[controls.Count+1];
                for(int j=0;j<=k-degree;j++)next[j]=controls[j];
                for(int j=k-multiplicity;j<controls.Count;j++)next[j+1]=controls[j];
                for(int j=k-degree+1;j<=k-multiplicity;j++)next[j]=H.Blend(controls[j-1],controls[j],(knot-u[j])/(u[j+degree]-u[j]));
                controls=next.ToList();u.Insert(k+1,knot);multiplicity++;
            }
        }
        var result=new List<DxfSpatialLine>();
        for(int span=degree;span<controls.Count;span++)
            if(u[span]>=start&&u[span+1]<=end&&u[span]<u[span+1])Subdivide(controls.GetRange(span-degree,degree+1).ToArray(),0);
        if(result.Count==0)throw new InvalidDataException("Empty spatial spline.");
        if(reversed){result.Reverse();for(int i=0;i<result.Count;i++)result[i]=new(result[i].End,result[i].Start);}
        return result;
        void Subdivide(H[] c,int depth)
        {
            LoadingContext.Check();
            if(c.Any(h=>h.W<=0||!double.IsFinite(h.Point.X)||!double.IsFinite(h.Point.Y)||!double.IsFinite(h.Point.Z)))throw new InvalidDataException("Unstable spatial spline.");
            Point3D a=c[0].Point,b=c[^1].Point;var chord=b-a;
            double Distance(Point3D p){double t=chord.LengthSquared==0?0:Math.Clamp(Vector3D.DotProduct(p-a,chord)/chord.LengthSquared,0,1);return (p-(a+chord*t)).Length;}
            if(c.All(h=>Distance(h.Point)<=tolerance))
            {
                if(result.Count>=200000)throw new InvalidDataException("Spatial spline subdivision limit.");
                result.Add(new(origin+(Vector3D)a,origin+(Vector3D)b));return;
            }
            if(depth>=24)throw new InvalidDataException("Spatial spline tolerance cannot be reached.");
            var work=(H[])c.Clone();var left=new H[c.Length];var right=new H[c.Length];left[0]=work[0];right[^1]=work[^1];
            for(int level=1;level<c.Length;level++)
            {for(int j=0;j<c.Length-level;j++)work[j]=H.Blend(work[j],work[j+1],.5);left[level]=work[0];right[c.Length-1-level]=work[c.Length-1-level];}
            Subdivide(left,depth+1);Subdivide(right,depth+1);
        }
    }
}
