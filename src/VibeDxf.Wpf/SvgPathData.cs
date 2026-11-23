using System.Globalization;
using System.Windows;
namespace VibeDxf.Wpf;

/// <summary>SVG path-data grammar: M L H V C S Q T A Z, absolute and relative, implicit repeats and compact arc flags.
/// Curves are in the element's local coordinates. Lines and circular arcs stay exact; Béziers and elliptical arcs are
/// flattened to the caller's bound and mark the path approximate.</summary>
internal static class SvgPathData
{
    private static readonly double[] CubicKnots = [0, 0, 0, 0, 1, 1, 1, 1];

    /// <summary>Parses path data. <paramref name="exact"/> receives lines and circular arcs, <paramref name="cubics"/> the display
    /// form of Béziers and elliptical arcs; the returned subpaths hold all measurement curves (with chords).</summary>
    public static List<List<Curve>> Parse(string d, double tolerance, out bool approximate, out string? error,
        List<Curve>? exact = null, List<CubicSegment>? cubics = null)
    {
        var subpaths = new List<List<Curve>>(); List<Curve>? curves = null;
        error = null; bool curved = false;
        int i = 0; char command = '\0', previous = '\0';
        Point current = default, start = default, lastControl = default;
        void Skip() { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; }
        bool Number(out double value)
        {
            Skip(); int begin = i; value = 0;
            if (i < d.Length && d[i] is '+' or '-') i++;
            bool dot = false, digits = false;
            while (i < d.Length && (char.IsAsciiDigit(d[i]) || (d[i] == '.' && !dot))) { dot |= d[i] == '.'; digits |= d[i] != '.'; i++; }
            if (digits && i < d.Length && d[i] is 'e' or 'E')
            {
                int mark = i++; if (i < d.Length && d[i] is '+' or '-') i++;
                if (i < d.Length && char.IsAsciiDigit(d[i])) while (i < d.Length && char.IsAsciiDigit(d[i])) i++; else i = mark;
            }
            if (!digits) { i = begin; return false; }
            return double.TryParse(d.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
        bool Flag(out bool value)
        {
            Skip(); value = false;
            if (i >= d.Length || d[i] is not ('0' or '1')) return false;
            value = d[i++] == '1'; return true;
        }
        bool Pt(bool relative, out Point p)
        {
            p = default;
            if (!Number(out var x) || !Number(out var y)) return false;
            p = relative ? new Point(current.X + x, current.Y + y) : new Point(x, y); return true;
        }
        List<Curve> Open() { if (curves == null) { curves = []; subpaths.Add(curves); } return curves; }
        void Line(Point to) { if (to != current) { var k = Curve.Line(current, to); Open().Add(k); exact?.Add(k); } current = to; }
        void Cubic(Point c1, Point c2, Point to)
        {
            if (current == c1 && c2 == to) Line(to);
            else { Open().AddRange(SplineGeometry.Flatten(3, [current, c1, c2, to], CubicKnots, null, tolerance)); cubics?.Add(new(current, c1, c2, to)); curved = true; current = to; }
            lastControl = c2;
        }
        while (true)
        {
            Skip();
            if (i >= d.Length) break;
            if (char.IsAsciiLetter(d[i])) command = d[i++];
            else if (command == '\0') { error = "malformed data"; break; }
            bool rel = char.IsLower(command); char upper = char.ToUpperInvariant(command);
            bool ok = true;
            switch (upper)
            {
                case 'M':
                {
                    if (!Pt(rel, out var m)) { ok = false; break; }
                    current = start = m; curves = null; command = rel ? 'l' : 'L'; break; // implicit repeats are line-to
                }
                case 'L':
                {
                    if (!Pt(rel, out var l)) { ok = false; break; }
                    Line(l); break;
                }
                case 'H':
                {
                    if (!Number(out var h)) { ok = false; break; }
                    Line(new Point(rel ? current.X + h : h, current.Y)); break;
                }
                case 'V':
                {
                    if (!Number(out var v)) { ok = false; break; }
                    Line(new Point(current.X, rel ? current.Y + v : v)); break;
                }
                case 'C':
                {
                    if (!(Pt(rel, out var c1) && Pt(rel, out var c2) && Pt(rel, out var to))) { ok = false; break; }
                    Cubic(c1, c2, to); break;
                }
                case 'S':
                {
                    var c1 = previous is 'C' or 'S' ? current + (current - lastControl) : current;
                    if (!(Pt(rel, out var c2) && Pt(rel, out var to))) { ok = false; break; }
                    Cubic(c1, c2, to); break;
                }
                case 'Q' or 'T':
                {
                    Point q = previous is 'Q' or 'T' ? current + (current - lastControl) : current;
                    if (upper == 'Q' && !Pt(rel, out q)) { ok = false; break; }
                    if (!Pt(rel, out var to)) { ok = false; break; }
                    var from = current;
                    Cubic(from + (q - from) * (2.0 / 3), to + (q - to) * (2.0 / 3), to);
                    lastControl = q; break;
                }
                case 'A':
                {
                    if (!(Number(out var rx) && Number(out var ry) && Number(out var phi) && Flag(out var large) && Flag(out var sweep) && Pt(rel, out var to))) { ok = false; break; }
                    if (to == current) break;
                    if (Math.Abs(rx) < 1e-300 || Math.Abs(ry) < 1e-300) { Line(to); break; }
                    curved |= Arc(Open(), current, to, Math.Abs(rx), Math.Abs(ry), phi * Math.PI / 180, large, sweep, tolerance, exact, cubics);
                    current = to; break;
                }
                case 'Z':
                    Line(start); curves = null; break;
                default:
                    error = "unknown command " + command; ok = false; break;
            }
            if (!ok) { error ??= "malformed data"; break; }
            previous = upper;
            if (upper == 'Z') command = '\0';
        }
        approximate = curved;
        subpaths.RemoveAll(s => s.Count == 0);
        return subpaths;
    }

    // SVG 1.1 F.6.5 endpoint-to-center conversion. Returns true when the arc had to be flattened (elliptical).
    private static bool Arc(List<Curve> output, Point p1, Point p2, double rx, double ry, double phi, bool large, bool sweep, double tolerance,
        List<Curve>? exact, List<CubicSegment>? cubics)
    {
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p1.X - p2.X) / 2, dy = (p1.Y - p2.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { var s = Math.Sqrt(lambda); rx *= s; ry *= s; }
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1, den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coef = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));
        double cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        var center = new Point(cos * cx1 - sin * cy1 + (p1.X + p2.X) / 2, sin * cx1 + cos * cy1 + (p1.Y + p2.Y) / 2);
        static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        double t1 = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double dt = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
        if (Math.Abs(rx - ry) <= 1e-12 * Math.Max(rx, ry))
        {
            var arc = Curve.Arc(center, rx, t1 + phi, dt);
            output.Add(arc); exact?.Add(arc);
            return false;
        }
        Ellipse(output, center, rx, ry, phi, t1, dt, tolerance, p1, p2);
        if (cubics != null) EllipseCubics(cubics, center, rx, ry, phi, t1, dt);
        return true;
    }

    /// <summary>Display cubics for an elliptical arc: affine images of ≤22.5° circular-arc cubics (relative error below 1e-7).</summary>
    public static void EllipseCubics(List<CubicSegment> output, Point c, double rx, double ry, double phi, double t1, double dt)
    {
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 8)));
        double cos = Math.Cos(phi), sin = Math.Sin(phi), step = dt / n, k = 4.0 / 3 * Math.Tan(step / 4);
        Point Map(double x, double y) => new(c.X + rx * x * cos - ry * y * sin, c.Y + rx * x * sin + ry * y * cos);
        for (int i = 0; i < n; i++)
        {
            double a0 = t1 + step * i, a1 = a0 + step;
            double x0 = Math.Cos(a0), y0 = Math.Sin(a0), x1 = Math.Cos(a1), y1 = Math.Sin(a1);
            output.Add(new(Map(x0, y0), Map(x0 - k * y0, y0 + k * x0), Map(x1 + k * y1, y1 - k * x1), Map(x1, y1)));
        }
    }

    /// <summary>Flattens an elliptical arc into chords with at most <paramref name="tolerance"/> deviation; endpoints are kept exact.</summary>
    public static void Ellipse(List<Curve> output, Point c, double rx, double ry, double phi, double t1, double dt, double tolerance, Point? first = null, Point? last = null)
    {
        double r = Math.Max(rx, ry);
        double step = tolerance >= r ? Math.PI / 2 : 2 * Math.Acos(1 - tolerance / r);
        int n = (int)Math.Clamp(Math.Ceiling(Math.Abs(dt) / step), 4, 4096);
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        Point At(double t) => new(c.X + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin, c.Y + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos);
        var previous = first ?? At(t1);
        for (int k = 1; k <= n; k++)
        {
            var next = k == n && last is { } end ? end : At(t1 + dt * k / n);
            if (next != previous) output.Add(Curve.Line(previous, next));
            previous = next;
        }
    }
}
