using System.Windows;
namespace VibeDxf.Wpf;

/// <summary>Two closest endpoints in the same DXF coordinate space.</summary>
public readonly record struct ClosestPair(Point A, Point B)
{
    /// <summary>Euclidean endpoint distance in drawing units.</summary>
    public double Distance => (B - A).Length;
}

// A straight curve is a segment, a RAY from Start through End, or an infinite XLINE through both.
/// <summary>Parameter domain of a straight curve.</summary>
public enum LineExtent
{
    /// <summary>Finite segment with parameters from zero to one.</summary>
    Segment,
    /// <summary>Ray starting at parameter zero and continuing indefinitely.</summary>
    Ray,
    /// <summary>Line extending indefinitely in both directions.</summary>
    Infinite
}

// All calculations use DXF XY coordinates; rendering never affects measurement precision.
/// <summary>Immutable analytic line/arc geometry in DXF XY coordinates. Radius zero denotes a straight curve; angles and sweeps are radians.</summary>
public sealed record Curve(Point Start, Point End, Point Center, double Radius, double Angle, double Sweep, LineExtent Extent = LineExtent.Segment)
{
    /// <summary>True when Radius is zero and the curve uses its straight-line parameter domain.</summary>
    public bool IsLine => Radius == 0;
    // For rays and xlines End is only a unit direction handle, not an endpoint.
    /// <summary>Whether geometry includes a ray or infinite straight line.</summary>
    public bool IsUnbounded => Extent != LineExtent.Segment;
    /// <summary>Minimum straight-line parameter; negative infinity for XLINE, otherwise zero.</summary>
    public double TMin => Extent == LineExtent.Infinite ? double.NegativeInfinity : 0;
    /// <summary>Maximum straight-line parameter; one for a segment and positive infinity for RAY/XLINE.</summary>
    public double TMax => Extent == LineExtent.Segment ? 1 : double.PositiveInfinity;
    private bool InRange(double t) => t >= TMin && t <= TMax;
    /// <summary>Creates an exact finite straight segment between two points.</summary>
    public static Curve Line(Point a, Point b) => new(a, b, default, 0, 0, 0);
    /// <summary>Creates a ray from a finite base point along a nonzero finite direction.</summary>
    public static Curve Ray(Point origin, Vector direction) => Unbounded(origin, direction, LineExtent.Ray);
    /// <summary>Creates an infinite line from a finite base point and nonzero finite direction.</summary>
    public static Curve XLine(Point origin, Vector direction) => Unbounded(origin, direction, LineExtent.Infinite);
    private static Curve Unbounded(Point origin, Vector direction, LineExtent extent)
    {
        if (!(direction.Length > 0) || !double.IsFinite(direction.Length)) throw new ArgumentException("Direction must be finite and nonzero.");
        direction.Normalize();
        return new(origin, origin + direction, default, 0, 0, 0, extent);
    }
    /// <summary>Creates a circular arc from center/radius, start angle and signed sweep in radians.</summary>
    public static Curve Arc(Point c, double r, double start, double sweep) =>
        new(c + new Vector(r * Math.Cos(start), r * Math.Sin(start)),
            c + new Vector(r * Math.Cos(start + sweep), r * Math.Sin(start + sweep)), c, r, start, sweep);
    /// <summary>Normalizes an angle in radians into the half-open interval from zero to two pi.</summary>
    public static double Wrap(double a) => (a % Math.Tau + Math.Tau) % Math.Tau;
    /// <summary>Tests whether a radian angle lies on the signed arc sweep.</summary>
    public bool ContainsAngle(double a) => Math.Abs(Sweep) >= Math.Tau - 1e-10 ||
        (Sweep >= 0 ? Wrap(a - Angle) : Wrap(Angle - a)) <= Math.Abs(Sweep) + 1e-10;
    /// <summary>Evaluates the line/arc at parameter t; finite curves normally use zero through one.</summary>
    public Point At(double t) => IsLine ? Start + (End - Start) * t :
        Center + new Vector(Radius * Math.Cos(Angle + Sweep * t), Radius * Math.Sin(Angle + Sweep * t));
    /// <summary>Returns an analytic closest point in the line/arc parameter domain.</summary>
    public Point Closest(Point p)
    {
        if (IsLine)
        {
            var d = End - Start;
            return Start + d * (d.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(p - Start, d) / d.LengthSquared, TMin, TMax));
        }
        var v = p - Center;
        var a = Math.Atan2(v.Y, v.X);
        if (v.LengthSquared > 0 && ContainsAngle(a)) return Center + v * (Radius / v.Length);
        return (p - Start).LengthSquared <= (p - End).LengthSquared ? Start : End;
    }
    /// <summary>Finite fit bounds; rays and infinite lines contribute only their base point.</summary>
    public Rect Bounds
    {
        get
        {
            // Unbounded lines contribute only their base point to fit extents.
            var r = IsUnbounded ? new Rect(Start, Start) : new Rect(Start, End);
            if (!IsLine) for (int i = 0; i < 4; i++)
            {
                var a = i * Math.PI / 2;
                if (ContainsAngle(a)) r.Union(Center + new Vector(Radius * Math.Cos(a), Radius * Math.Sin(a)));
            }
            return r;
        }
    }
    /// <summary>Returns an analytic closest endpoint pair for two curves in the same coordinate space.</summary>
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
                if (a.InRange(t) && b.InRange(s)) { var p = a.Start + t * u; Add(p, p); }
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
                if (line.InRange(t))
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
                        if (line.InRange(s))
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
/// <summary>Visibility categories. Values from <see cref="Dimension"/> up are display-only annotations.</summary>
public enum EntityCategory
{
    /// <summary>Ordinary selectable geometric entities.</summary>
    Geometry,
    /// <summary>Prepared text and attributes.</summary>
    Text,
    /// <summary>Hatch boundaries, patterns and fills.</summary>
    Hatch,
    /// <summary>Saved display-only dimension graphics.</summary>
    Dimension,
    /// <summary>Display-only legacy leader graphics.</summary>
    Leader,
    /// <summary>Display-only combined multileader graphics.</summary>
    Multileader,
    /// <summary>Display-only paper viewport frame.</summary>
    ViewportFrame
}
/// <summary>One read-only expanded drawing occurrence with prepared numeric geometry and source metadata.</summary>
public sealed class DxfEntity
{
    internal DxfEntity(string kind, string layer, string handle, IEnumerable<Curve> curves)
    {
        Kind=kind; Layer=layer; Handle=handle; SourceHandle=handle;
        Curves=Array.AsReadOnly(curves.ToArray());
        Bounds=Curves.Aggregate(Rect.Empty,(r,c)=>{r.Union(c.Bounds);return r;});
        Category=kind switch { "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF"=>EntityCategory.Text,"HATCH"=>EntityCategory.Hatch,_=>EntityCategory.Geometry };
    }
    /// <summary>Document-local stable identity; do not use collection positions or handles as identity.</summary>
    public DxfEntityId Id { get; internal set; }
    /// <summary>Stable file-order layer index within this document.</summary>
    public int LayerId { get; internal set; }
    /// <summary>Model or paper coordinate space in which entity coordinates and query results are expressed.</summary>
    public DxfCoordinateContext CoordinateContext { get; internal set; }
    /// <summary>Original supported DXF entity kind after expansion.</summary>
    public string Kind { get; }
    /// <summary>Effective layer name after block layer inheritance.</summary>
    public string Layer { get; }
    /// <summary>Legacy combined handle including expanded instance path when applicable; not a unique public ID.</summary>
    public string Handle { get; }
    /// <summary>Original entity handle before block/annotation instance path expansion.</summary>
    public string SourceHandle { get; internal init; }
    /// <summary>Original ACI/true-color fields, separate from resolved source RGBA and display theme.</summary>
    public DxfColorSource ColorSource { get; internal set; } = new();
    /// <summary>Read-only analytic or approximated measurement boundary curves prepared during loading.</summary>
    public IReadOnlyList<Curve> Curves { get; }
    private IReadOnlyList<IReadOnlyList<Point>> fillContours=[];
    /// <summary>Deeply read-only even/odd fill contours, including glyph holes.</summary>
    public IReadOnlyList<IReadOnlyList<Point>> FillContours { get=>fillContours; internal init=>fillContours=Freeze(value); }
    internal IReadOnlyList<Point>? FillTriangles { get; set; }
    private IReadOnlyList<IReadOnlyList<Point>>? regionContours;
    /// <summary>Optional measurement/picking region contours, separate from visible hatch pattern strokes.</summary>
    public IReadOnlyList<IReadOnlyList<Point>>? RegionContours { get=>regionContours; internal init=>regionContours=value==null?null:Freeze(value); }
    private IReadOnlyList<Curve>? renderCurves;
    /// <summary>Optional visible stroke override; null uses measurement curves for rendering.</summary>
    public IReadOnlyList<Curve>? RenderCurves { get=>renderCurves; internal init=>renderCurves=value==null?null:Array.AsReadOnly(value.ToArray()); }
    private IReadOnlyList<Point> renderDots=[];
    /// <summary>Read-only fixed-display-size pattern dot positions.</summary>
    public IReadOnlyList<Point> RenderDots { get=>renderDots; internal init=>renderDots=Array.AsReadOnly(value.ToArray()); }
    /// <summary>Decoded text content when this occurrence contains text; exact original font fidelity is not guaranteed.</summary>
    public string? Text { get; internal init; }
    /// <summary>Expanded instance path retained from source block/annotation expansion.</summary>
    public string? InstancePath { get; internal init; }
    /// <summary>Whether prepared geometry or the resulting distance involves a supported approximation.</summary>
    public bool IsApproximate { get; internal init; }
    /// <summary>Visibility category; Dimension and later categories are display-only in interactive picking.</summary>
    public EntityCategory Category { get; internal init; }
    /// <summary>Whether this entity is display-only for interactive picking; document measurements can still query its geometry.</summary>
    public bool IsAnnotation => Category >= EntityCategory.Dimension;
    /// <summary>Resolved source RGBA, before viewer theme adjustment.</summary>
    public uint Color { get; internal init; } = DxfColors.White;
    internal int Group { get; set; }

    internal Func<Func<System.Windows.Media.Media3D.Point3D,Point>,DxfEntity>? Projection3D { get; set; }
    internal DxfSpatialGeometry? SpatialGeometry { get; set; }
    internal DxfSpatialGeometry? DisplayGeometry3D { get; init; }
    /// <summary>Whether geometry includes a ray or infinite straight line.</summary>
    public bool IsUnbounded => Curves.Any(c=>c.IsUnbounded);
    /// <summary>Finite fit bounds in this coordinate space; unbounded curves contribute their base point only.</summary>
    public Rect Bounds { get; internal init; }
    private static IReadOnlyList<IReadOnlyList<Point>> Freeze(IEnumerable<IReadOnlyList<Point>> value)=>
        Array.AsReadOnly(value.Select(p=>(IReadOnlyList<Point>)Array.AsReadOnly(p.ToArray())).ToArray());
    /// <summary>Tests even/odd containment in filled measurement regions; boundary-only entities normally have no interior.</summary>
    public bool Contains(Point p)
    {
        bool inside=false;
        foreach(var polygon in RegionContours ?? FillContours)
            for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
            {
                var a=polygon[i];var b=polygon[j];
                if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
        return inside;
    }
    internal Point Closest(Point p)=>Contains(p)?p:Curves.MinBy(c=>(c.Closest(p)-p).LengthSquared)!.Closest(p);
    internal ClosestPair Distance(DxfEntity other)
    {
        foreach(var c in Curves)if(other.Contains(c.Start))return new(c.Start,c.Start);
        foreach(var c in other.Curves)if(Contains(c.Start))return new(c.Start,c.Start);
        return Curves.SelectMany(a=>other.Curves.Select(b=>Curve.Distance(a,b))).MinBy(p=>p.Distance);
    }
}
