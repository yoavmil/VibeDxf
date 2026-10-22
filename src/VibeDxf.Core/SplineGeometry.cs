using System.IO;

namespace VibeDxf.Core;

/// <summary>Positive-weight NURBS to bounded-error line segments, once at file load.</summary>
public static class SplineGeometry
{
    public const double Tolerance = 0.0001; // DXF drawing units; also used for measurement.
    private readonly record struct H(double X, double Y, double W)
    {
        public Point Point => new(X / W, Y / W);
        public static H Blend(H a, H b, double t) => new(
            a.X * (1-t) + b.X * t, a.Y * (1-t) + b.Y * t, a.W * (1-t) + b.W * t);
    }
    public static List<Curve> Flatten(int degree, IReadOnlyList<Point> points, IReadOnlyList<double> knots,
        IReadOnlyList<double>? weights = null, CancellationToken cancellationToken = default)
    {
        if (degree < 1 || degree > 16 || points.Count <= degree || knots.Count != points.Count + degree + 1 ||
            points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) || knots.Any(k => !double.IsFinite(k)) ||
            knots.Zip(knots.Skip(1)).Any(k => k.First > k.Second) ||
            (weights != null && (weights.Count != points.Count || weights.Any(w => !double.IsFinite(w) || w <= 0))))
            throw new InvalidDataException("Invalid or unsupported spline control data.");
        double start = knots[degree], end = knots[points.Count];
        if (start >= end) throw new InvalidDataException("Empty spline parameter domain.");
        var origin = points[0];
        double maxWeight = weights?.Max() ?? 1;
        var controls = points.Select((p,i) => {
            var w = (weights?[i] ?? 1) / maxWeight;
            return new H((p.X-origin.X)*w, (p.Y-origin.Y)*w, w);
        }).ToList();
        var u = knots.ToList();
        // Knot insertion preserves the exact rational curve. Multiplicity p exposes
        // each nonempty span as a rational Bezier curve, including periodic domains.
        foreach (double knot in knots.Where(k => k >= start && k <= end).Distinct())
        {
            int multiplicity = u.Count(k => k == knot);
            if (multiplicity > degree + 1) throw new InvalidDataException("Invalid spline knot multiplicity.");
            while (multiplicity < degree)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int k = u.FindLastIndex(v => v <= knot);
                var next = new H[controls.Count+1];
                for (int i=0; i<=k-degree; i++) next[i]=controls[i];
                for (int i=k-multiplicity; i<controls.Count; i++) next[i+1]=controls[i];
                for (int i=k-degree+1; i<=k-multiplicity; i++)
                    next[i]=H.Blend(controls[i-1], controls[i], (knot-u[i])/(u[i+degree]-u[i]));
                controls=next.ToList();u.Insert(k+1,knot);multiplicity++;
            }
        }
        var result = new List<Curve>();
        for (int span=degree; span<controls.Count; span++)
            if (u[span]>=start && u[span+1]<=end && u[span]<u[span+1])
                Subdivide(controls.GetRange(span-degree,degree+1).ToArray(),0);
        if (result.Count==0) throw new InvalidDataException("Spline has no usable spans.");
        return result;

        void Subdivide(H[] control,int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a=control[0].Point;var b=control[^1].Point;
            if (control.Any(h => h.W<=0 || !double.IsFinite(h.Point.X) || !double.IsFinite(h.Point.Y)))
                throw new InvalidDataException("Unstable spline weights or coordinates.");
            var chord=Curve.Line(a,b);
            // Positive rational weights keep the curve in the control-point convex
            // hull. The maximum control distance bounds deviation from this chord.
            if (control.All(h => (h.Point-chord.Closest(h.Point)).Length<=Tolerance))
            {
                if (result.Count>=250000) throw new InvalidDataException("Spline subdivision limit exceeded.");
                result.Add(Curve.Line(origin+(a-new Point()),origin+(b-new Point())));return;
            }
            if (depth>=24) throw new InvalidDataException("Spline tolerance cannot be reached.");
            var work=(H[])control.Clone();var left=new H[control.Length];var right=new H[control.Length];
            left[0]=work[0];right[^1]=work[^1];
            for (int level=1;level<control.Length;level++)
            {
                for(int i=0;i<control.Length-level;i++) work[i]=H.Blend(work[i],work[i+1],.5);
                left[level]=work[0];right[control.Length-1-level]=work[control.Length-1-level];
            }
            Subdivide(left,depth+1);Subdivide(right,depth+1);
        }
    }
}
