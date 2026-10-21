using System.Windows;
namespace VibeDxf;

public enum MeasurementStrategy { Closest, Center }

/// <summary>
/// A whole element or the exact midpoint of one of its straight segments. On a sheet, model geometry picked through
/// a layout viewport keeps that <paramref name="Viewport"/>: its coordinates and measurements stay in model units and
/// only its display is mapped onto the sheet.
/// </summary>
public sealed record MeasurementTarget(DxfEntity Entity, int? MidpointSegment = null, DxfViewportWindow? Viewport = null)
{
    /// <summary>Maps a point of this target's own space onto the displayed sheet (identity outside viewports).</summary>
    public Point ToDisplay(Point p) => Viewport?.ModelToPaper.Transform(p) ?? p;
    public Point? Midpoint => MidpointSegment is int i ? Entity.Curves[i].At(0.5) : null;
    public string Description => MidpointSegment is null ? Entity.Kind : $"{Entity.Kind} midpoint";
    // A polyline can contain several unrelated arc centers; only standalone arcs/circles use this strategy.
    public Point? Center => MidpointSegment is null && (Entity.Kind is "CIRCLE" or "ARC") &&
        Entity.Curves.Count == 1 && !Entity.Curves[0].IsLine ? Entity.Curves[0].Center : null;
    public Point? FixedPoint(MeasurementStrategy strategy) => Midpoint ?? (Entity.Kind == "POINT" ? Entity.Curves[0].Start : strategy == MeasurementStrategy.Center ? Center : null);
    public string Describe(MeasurementStrategy strategy) => strategy == MeasurementStrategy.Center && Center != null
        ? $"{Entity.Kind} center" : Description;
    public ClosestPair Distance(MeasurementTarget other, MeasurementStrategy strategy = MeasurementStrategy.Closest,
        MeasurementStrategy otherStrategy = MeasurementStrategy.Closest)
    {
        var firstPoint = FixedPoint(strategy);
        var secondPoint = other.FixedPoint(otherStrategy);
        if (firstPoint is Point a)
        {
            if (secondPoint is Point b) return new ClosestPair(a, b);
            return new ClosestPair(a,other.Entity.Closest(a));
        }
        if (secondPoint is Point point)
            return new ClosestPair(Entity.Closest(point),point);
        return Entity.Distance(other.Entity);
    }
}

/// <summary>Two sorted indexes avoid scanning the drawing on every pointer move.</summary>
public sealed class MidpointIndex
{
    private readonly Entry[] byX;
    private readonly Entry[] byY;
    private sealed record Entry(MeasurementTarget Target, Point Point);
    public MidpointIndex(IEnumerable<DxfEntity> entities)
    {
        // Flattened spline chords are approximation details, not straight segments users can snap to.
        var entries = entities.Where(e => e.Kind is not ("MTEXT" or "HATCH" or "SPLINE" or "TEXT" or "POINT") && !e.IsApproximate && !e.IsAnnotation).SelectMany(e => e.Curves.Select((c, i) => (Curve: c, Index: i))
            .Where(v => v.Curve.IsLine && !v.Curve.IsUnbounded && v.Curve.Start != v.Curve.End)
            .Select(v => new Entry(new MeasurementTarget(e, v.Index), v.Curve.At(0.5)))).ToArray();
        byX = entries.OrderBy(e => e.Point.X).ToArray();
        byY = entries.OrderBy(e => e.Point.Y).ToArray();
    }
    public MeasurementTarget? Find(Point point, double radius)
    {
        var x0 = Lower(byX, point.X - radius, true); var x1 = Upper(byX, point.X + radius, true);
        var y0 = Lower(byY, point.Y - radius, false); var y1 = Upper(byY, point.Y + radius, false);
        var useX = x1 - x0 <= y1 - y0;
        var entries = useX ? byX : byY;
        var start = useX ? x0 : y0; var end = useX ? x1 : y1;
        MeasurementTarget? nearest = null; var best = radius * radius;
        for (int i = start; i < end; i++)
        {
            var distance = (entries[i].Point - point).LengthSquared;
            if (distance <= best) { nearest = entries[i].Target; best = distance; }
        }
        return nearest;
    }
    private static int Lower(Entry[] entries, double value, bool x)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if ((x ? entries[mid].Point.X : entries[mid].Point.Y) < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
    private static int Upper(Entry[] entries, double value, bool x)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if ((x ? entries[mid].Point.X : entries[mid].Point.Y) <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
