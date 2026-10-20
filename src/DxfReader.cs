using System.Globalization;
using System.IO;
using System.Windows;
namespace VibeDxf;
public sealed class DxfDocument
{
    public List<DxfEntity> Entities { get; } = [];
    public Dictionary<string, int> Skipped { get; } = [];
    public string Units { get; set; } = "drawing units (unspecified)";
    public Rect Bounds => Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; });
}
public static class DxfReader
{
    private sealed record Pair(int Code, string Value);
    public static DxfDocument Load(string path)
    {
        using var reader = new StreamReader(path);
        var pairs = new List<Pair>();
        string? code;
        while ((code = reader.ReadLine()) != null)
        {
            var value = reader.ReadLine() ?? throw new InvalidDataException("Incomplete DXF group pair.");
            if (!int.TryParse(code.Trim(), out var n)) throw new InvalidDataException("Only ASCII DXF files are supported in this initial viewer.");
            pairs.Add(new Pair(n, value.Trim()));
        }
        var doc = new DxfDocument();
        for (int j = 0; j + 1 < pairs.Count; j++)
            if (pairs[j].Code == 9 && pairs[j].Value == "$INSUNITS")
                doc.Units = pairs[j + 1].Value switch { "1" => "inches", "2" => "feet", "4" => "mm", "5" => "cm", "6" => "m", "0" => "drawing units (unitless)", var u => $"drawing units (INSUNITS={u})" };
        string section = "";
        List<(Point P, double Bulge)>? vertices = null;
        string polyLayer = "0", polyHandle = ""; bool polyClosed = false;
        for (int i = 0; i < pairs.Count;)
        {
            var p = pairs[i];
            if (p.Code != 0) { i++; continue; }
            int end = i + 1;
            while (end < pairs.Count && pairs[end].Code != 0) end++;
            var fields = pairs.GetRange(i + 1, end - i - 1);
            string S(int c, string fallback = "") => fields.FirstOrDefault(x => x.Code == c)?.Value ?? fallback;
            double N(int c, double fallback = 0) => double.TryParse(S(c), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
            Point P(int x, int y) => new(N(x), N(y));
            if (p.Value == "SECTION") section = S(2);
            if (section == "ENTITIES")
            {
                var curves = new List<Curve>();
                switch (p.Value)
                {
                    case "LINE": curves.Add(Curve.Line(P(10, 20), P(11, 21))); break;
                    case "CIRCLE": case "ARC":
                        var r = N(40); if (r <= 0) break;
                        var start = N(50) * Math.PI / 180;
                        var sweep = p.Value == "CIRCLE" ? Math.Tau : Curve.Wrap((N(51) - N(50)) * Math.PI / 180);
                        curves.Add(Curve.Arc(P(10, 20), r, start, sweep)); break;
                    case "LWPOLYLINE":
                        var vs = new List<(Point P, double Bulge)>();
                        double x = 0, y = 0, bulge = 0; bool has = false;
                        foreach (var f in fields)
                        {
                            if (f.Code == 10) { if (has) vs.Add((new Point(x, y), bulge)); x = double.Parse(f.Value, CultureInfo.InvariantCulture); y = bulge = 0; has = true; }
                            if (f.Code == 20) y = double.Parse(f.Value, CultureInfo.InvariantCulture);
                            if (f.Code == 42) bulge = double.Parse(f.Value, CultureInfo.InvariantCulture);
                        }
                        if (has) vs.Add((new Point(x, y), bulge));
                        curves = Polyline(vs, ((int)N(70) & 1) != 0); break;
                    case "POLYLINE":
                        if (((int)N(70) & (8 | 16 | 64)) != 0) { doc.Skipped["3D POLYLINE"] = doc.Skipped.GetValueOrDefault("3D POLYLINE") + 1; vertices = null; break; }
                        vertices = []; polyLayer = S(8, "0"); polyHandle = S(5); polyClosed = ((int)N(70) & 1) != 0; break;
                    case "VERTEX": vertices?.Add((P(10, 20), N(42))); break;
                    case "SEQEND":
                        if (vertices != null)
                        {
                            var cs = Polyline(vertices, polyClosed);
                            if (cs.Count > 0) doc.Entities.Add(new DxfEntity("POLYLINE", polyLayer, polyHandle, cs));
                            vertices = null;
                        }
                        break;
                    case "SECTION": case "ENDSEC": break;
                    default: doc.Skipped[p.Value] = doc.Skipped.GetValueOrDefault(p.Value) + 1; break;
                }
                if (curves.Count > 0) doc.Entities.Add(new DxfEntity(p.Value, S(8, "0"), S(5), curves));
            }
            if (p.Value == "ENDSEC") section = "";
            i = end;
        }
        if (doc.Entities.Count == 0) throw new InvalidDataException("No supported model/paper-space geometry was found. Supported: LINE, ARC, CIRCLE and 2D polylines. Blocks, text, hatches and splines are not supported yet.");
        return doc;
    }
    private static List<Curve> Polyline(List<(Point P, double Bulge)> vs, bool closed)
    {
        var result = new List<Curve>();
        for (int i = 0; i < vs.Count - (closed ? 0 : 1); i++)
        {
            var (a, b) = (vs[i], vs[(i + 1) % vs.Count]);
            if (Math.Abs(a.Bulge) < 1e-12 || a.P == b.P) { result.Add(Curve.Line(a.P, b.P)); continue; }
            var chord = b.P - a.P; var mid = a.P + chord / 2;
            var center = mid + new Vector(-chord.Y, chord.X) * ((1 - a.Bulge * a.Bulge) / (4 * a.Bulge));
            result.Add(Curve.Arc(center, (a.P - center).Length, Math.Atan2(a.P.Y - center.Y, a.P.X - center.X), 4 * Math.Atan(a.Bulge)));
        }
        return result;
    }
}


