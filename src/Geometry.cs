using System.Windows;
namespace VibeDxf;

public readonly record struct ClosestPair(Point A, Point B)
{
    public double Distance => (B - A).Length;
}

// All calculations use DXF XY coordinates; rendering never affects measurement precision.
public sealed record Curve(Point Start, Point End, Point Center, double Radius, double Angle, double Sweep)
{
    public bool IsLine => Radius == 0;
    public static Curve Line(Point a, Point b) => new(a, b, default, 0, 0, 0);
    public static Curve Arc(Point c, double r, double start, double sweep) =>
        new(c + new Vector(r * Math.Cos(start), r * Math.Sin(start)),
            c + new Vector(r * Math.Cos(start + sweep), r * Math.Sin(start + sweep)), c, r, start, sweep);
    public static double Wrap(double a) => (a % Math.Tau + Math.Tau) % Math.Tau;
    public bool ContainsAngle(double a) => Math.Abs(Sweep) >= Math.Tau - 1e-10 ||
        (Sweep >= 0 ? Wrap(a - Angle) : Wrap(Angle - a)) <= Math.Abs(Sweep) + 1e-10;
    public Point At(double t) => IsLine ? Start + (End - Start) * t :
        Center + new Vector(Radius * Math.Cos(Angle + Sweep * t), Radius * Math.Sin(Angle + Sweep * t));
    public Point Closest(Point p)
    {
        if (IsLine)
        {
            var d = End - Start;
            return Start + d * (d.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(p - Start, d) / d.LengthSquared, 0, 1));
        }
        var v = p - Center;
        var a = Math.Atan2(v.Y, v.X);
        if (v.LengthSquared > 0 && ContainsAngle(a)) return Center + v * (Radius / v.Length);
        return (p - Start).LengthSquared <= (p - End).LengthSquared ? Start : End;
    }
    public Rect Bounds
    {
        get
        {
            var r = new Rect(Start, End);
            if (!IsLine) for (int i = 0; i < 4; i++)
            {
                var a = i * Math.PI / 2;
                if (ContainsAngle(a)) r.Union(Center + new Vector(Radius * Math.Cos(a), Radius * Math.Sin(a)));
            }
            return r;
        }
    }
    public static ClosestPair Distance(Curve a, Curve b)
    {
        var best = new ClosestPair(a.Start, b.Closest(a.Start));
        void Add(Point x, Point y) { var p = new ClosestPair(x, y); if (p.Distance < best.Distance) best = p; }
        Add(a.End, b.Closest(a.End)); Add(a.Closest(b.Start), b.Start); Add(a.Closest(b.End), b.End);
        if (a.IsLine && b.IsLine)
        {
            var u = a.End - a.Start; var v = b.End - b.Start;
            var det = Vector.CrossProduct(u, v);
            if (Math.Abs(det) > 1e-20)
            {
                var t = Vector.CrossProduct(b.Start - a.Start, v) / det;
                var s = Vector.CrossProduct(b.Start - a.Start, u) / det;
                if (t >= 0 && t <= 1 && s >= 0 && s <= 1) { var p = a.Start + t * u; Add(p, p); }
            }
        }
        else if (a.IsLine || b.IsLine)
        {
            var line = a.IsLine ? a : b; var arc = a.IsLine ? b : a;
            void Candidate(Point p, Point q) { if (a.IsLine) Add(p, q); else Add(q, p); }
            var d = line.End - line.Start;
            if (d.LengthSquared > 0)
            {
                var t = Vector.Multiply(arc.Center - line.Start, d) / d.LengthSquared;
                var foot = line.Start + d * t;
                if (t >= 0 && t <= 1)
                {
                    var n = new Vector(-d.Y, d.X); n.Normalize();
                    foreach (var sign in new[] { -1, 1 })
                    {
                        var q = arc.Center + sign * arc.Radius * n;
                        if (arc.ContainsAngle(Math.Atan2(q.Y - arc.Center.Y, q.X - arc.Center.X))) Candidate(foot, q);
                    }
                }
                var offset2 = arc.Radius * arc.Radius - (foot - arc.Center).LengthSquared;
                if (offset2 >= -1e-10)
                {
                    var dt = Math.Sqrt(Math.Max(0, offset2) / d.LengthSquared);
                    foreach (var s in new[] { t - dt, t + dt })
                        if (s >= 0 && s <= 1)
                        {
                            var p = line.Start + s * d;
                            if (arc.ContainsAngle(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X))) Candidate(p, p);
                        }
                }
            }
        }
        else
        {
            var v = b.Center - a.Center; var d = v.Length;
            if (d > 1e-12)
            {
                var n = v / d;
                foreach (var sa in new[] { -1, 1 }) foreach (var sb in new[] { -1, 1 })
                {
                    var p = a.Center + sa * a.Radius * n; var q = b.Center + sb * b.Radius * n;
                    if (a.ContainsAngle(Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X)) &&
                        b.ContainsAngle(Math.Atan2(q.Y - b.Center.Y, q.X - b.Center.X))) Add(p, q);
                }
                var x = (a.Radius * a.Radius - b.Radius * b.Radius + d * d) / (2 * d);
                var h2 = a.Radius * a.Radius - x * x;
                if (h2 >= -1e-10)
                {
                    var foot = a.Center + x * n; var perp = new Vector(-n.Y, n.X);
                    foreach (var sign in new[] { -1, 1 })
                    {
                        var p = foot + sign * Math.Sqrt(Math.Max(0, h2)) * perp;
                        if (a.ContainsAngle(Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X)) &&
                            b.ContainsAngle(Math.Atan2(p.Y - b.Center.Y, p.X - b.Center.X))) Add(p, p);
                    }
                }
            }
        }
        return best;
    }
}
public sealed class DxfEntity(string kind, string layer, string handle, List<Curve> curves)
{
    public string Kind { get; } = kind;
    public string Layer { get; } = layer;
    public string Handle { get; } = handle;
    public List<Curve> Curves { get; } = curves;
    public Rect Bounds { get; } = curves.Aggregate(Rect.Empty, (r, c) => { r.Union(c.Bounds); return r; });
    public ClosestPair Distance(DxfEntity other) => Curves.SelectMany(a => other.Curves.Select(b => Curve.Distance(a, b))).MinBy(p => p.Distance);
}
