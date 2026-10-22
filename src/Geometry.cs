using System.Windows;
namespace VibeDxf;

public readonly record struct ClosestPair(Point A, Point B)
{
    public double Distance => (B - A).Length;
}

// A straight curve is a segment, a RAY from Start through End, or an infinite XLINE through both.
public enum LineExtent { Segment, Ray, Infinite }

// All calculations use DXF XY coordinates; rendering never affects measurement precision.
public sealed record Curve(Point Start,Point End,Point Center,double Radius,double Angle,double Sweep,LineExtent Extent=LineExtent.Segment)
{
 private VibeDxf.Core.Curve Value=>CoreDocumentAdapter.Core(this);
 public bool IsLine=>Radius==0;
 public bool IsUnbounded=>Extent!=LineExtent.Segment;
 public double TMin=>Value.TMin;public double TMax=>Value.TMax;
 public static Curve Line(Point a,Point b)=>CoreDocumentAdapter.Wpf(VibeDxf.Core.Curve.Line(CoreDocumentAdapter.Core(a),CoreDocumentAdapter.Core(b)));
 public static Curve Ray(Point a,Vector v)=>CoreDocumentAdapter.Wpf(VibeDxf.Core.Curve.Ray(CoreDocumentAdapter.Core(a),new(v.X,v.Y)));
 public static Curve XLine(Point a,Vector v)=>CoreDocumentAdapter.Wpf(VibeDxf.Core.Curve.XLine(CoreDocumentAdapter.Core(a),new(v.X,v.Y)));
 public static Curve Arc(Point c,double r,double start,double sweep)=>CoreDocumentAdapter.Wpf(VibeDxf.Core.Curve.Arc(CoreDocumentAdapter.Core(c),r,start,sweep));
 public static double Wrap(double a)=>VibeDxf.Core.Curve.Wrap(a);
 public bool ContainsAngle(double a)=>Value.ContainsAngle(a);
 public Point At(double t)=>CoreDocumentAdapter.Wpf(Value.At(t));
 public Point Closest(Point p)=>CoreDocumentAdapter.Wpf(Value.Closest(CoreDocumentAdapter.Core(p)));
 public Rect Bounds=>CoreDocumentAdapter.Wpf(Value.Bounds);
 public static ClosestPair Distance(Curve a,Curve b){var p=VibeDxf.Core.Curve.Distance(a.Value,b.Value);return new(CoreDocumentAdapter.Wpf(p.A),CoreDocumentAdapter.Wpf(p.B));}
}
/// <summary>Visibility categories. Values from <see cref="Dimension"/> up are display-only annotations.</summary>
public enum EntityCategory { Geometry, Text, Hatch, Dimension, Leader, Multileader, ViewportFrame }
public sealed class DxfEntity(string kind, string layer, string handle, List<Curve> curves)
{
    public string Kind { get; } = kind;
    public string Layer { get; } = layer;
    public string Handle { get; } = handle;
    public List<Curve> Curves { get; } = curves;
    public List<Point[]> FillContours { get; init; } = [];
    // Load-time cache of DxfPrimitives.Triangulate(FillContours), filled by DxfReader off the UI thread.
    public List<Point>? FillTriangles { get; set; }
    // Hatches measure/select their region boundary but render only fill or clipped pattern strokes.
    public List<Point[]>? RegionContours { get; init; }
    public List<Curve>? RenderCurves { get; init; }
    public List<Point> RenderDots { get; init; } = [];
    public string? Text { get; init; }
    public string? InstancePath { get; init; }
    public bool IsApproximate { get; init; }
    /// <summary>Display category for visibility toggles; annotation categories are drawn but never picked, snapped or measured.</summary>
    public EntityCategory Category { get; init; } = kind switch
    {
        "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF" => EntityCategory.Text,
        "HATCH" => EntityCategory.Hatch,
        _ => EntityCategory.Geometry,
    };
    public bool IsAnnotation => Category >= EntityCategory.Dimension;
    /// <summary>Resolved DXF color as packed RGBA (R in the low byte), ready for the GPU.</summary>
    public uint Color { get; init; } = DxfColors.White;
    /// <summary>Index into <c>DxfDocument.Groups</c> (layer + category); assigned once the drawing is loaded.</summary>
    public int Group { get; internal set; }
    public bool IsUnbounded => Curves.Any(c => c.IsUnbounded);
    public bool Contains(Point p)
    {
        bool inside=false;
        foreach(var polygon in RegionContours ?? FillContours)
            for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
            {
                var a=polygon[i];var b=polygon[j];
                if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
        return inside;
    }
    public Point Closest(Point p) => Contains(p)?p:Curves.MinBy(c=>(c.Closest(p)-p).LengthSquared)!.Closest(p);
    public Rect Bounds { get; } = curves.Aggregate(Rect.Empty, (r, c) => { r.Union(c.Bounds); return r; });
    public ClosestPair Distance(DxfEntity other)
    {
        foreach(var c in Curves)if(other.Contains(c.Start))return new(c.Start,c.Start);
        foreach(var c in other.Curves)if(Contains(c.Start))return new(c.Start,c.Start);
        return Curves.SelectMany(a=>other.Curves.Select(b=>Curve.Distance(a,b))).MinBy(p=>p.Distance);
    }
}
