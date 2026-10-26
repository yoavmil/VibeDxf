using System.Globalization;
using System.IO;
using System.Windows;

namespace VibeDxf.Wpf;

// POLYLINE variants store WCS 3D coordinates; expose their exact top-down wireframe,
// not invented face interiors or 3D distances. Header counts for polyfaces are advisory.
internal sealed class DxfPolylineProjection
{
    private const int Limit = 1_000_000;
    private readonly int flags, m, n;
    private readonly List<System.Windows.Media.Media3D.Point3D> points = [];
    private readonly List<int[]> faces = [];
    internal string Description => (flags & 64) != 0 ? "POLYFACE" : (flags & 16) != 0 ? "POLYGON MESH" : "3D POLYLINE";

    internal DxfPolylineProjection(IReadOnlyList<(int Code, string Value)> header)
    {
        flags = Integer(header, 70);
        if ((flags & (2 | 4)) != 0 || Integer(header, 75) != 0)
            throw new NotSupportedException("POLYLINE (fitted 3D curve/surface)");
        if ((flags & 64) == 0 && (flags & 16) != 0)
        {
            m = Integer(header, 71); n = Integer(header, 72);
            if (m < 2 || n < 2 || (long)m * n > Limit) throw new InvalidDataException("polygon mesh dimensions");
        }
    }

    internal void AddVertex(IReadOnlyList<(int Code, string Value)> fields)
    {
        LoadingContext.Check();
        if (points.Count + faces.Count >= Limit) throw new InvalidDataException("vertex/face limit");
        int vertexFlags = Integer(fields, 70);
        if ((flags & 64) != 0)
        {
            if ((vertexFlags & 128) == 0) throw new InvalidDataException("polyface vertex flags");
            if ((vertexFlags & 64) == 0)
            {
                var indices = new List<int>(); bool ended = false;
                for (int code = 71; code <= 74; code++)
                {
                    var index = Integer(fields, code);
                    if (index == 0) { ended = true; continue; }
                    if (ended || index == int.MinValue) throw new InvalidDataException("polyface index sequence");
                    indices.Add(index);
                }
                if (indices.Count < 3 || indices.Select(Math.Abs).Distinct().Count() < 3) throw new InvalidDataException("polyface face has fewer than three distinct vertices");
                faces.Add(indices.ToArray());
                return;
            }
        }
        else if ((flags & 16) != 0)
        {
            if ((vertexFlags & 64) == 0) throw new InvalidDataException("polygon mesh vertex flags");
        }
        else if ((vertexFlags & (64 | 128)) != 0) throw new InvalidDataException("3D polyline vertex flags");
        double x = Coordinate(fields, 10), y = Coordinate(fields, 20);
        double z = Coordinate(fields, 30, required: false);
        points.Add(new(x, y,z));
    }

    internal DxfEntity Finish(string layer, string handle, Func<System.Windows.Media.Media3D.Point3D,Point>? project=null)
    {
        LoadingContext.Check();
        project??=p=>new(p.X,p.Y);
        var curves = new List<Curve>(); var dots = new HashSet<Point>();
        var emitted = new HashSet<(int A, int B)>();
        void Edge(int a, int b)
        {
            LoadingContext.Check();
            if (a < 0 || a >= points.Count || b < 0 || b >= points.Count) throw new InvalidDataException("polyface vertex index out of range");
            if (a == b || !emitted.Add((Math.Min(a, b), Math.Max(a, b)))) return;
            if (curves.Count >= Limit) throw new InvalidDataException("projected edge limit");
            var pa=project(points[a]);var pb=project(points[b]);curves.Add(Curve.Line(pa,pb));
            if (pa == pb) dots.Add(pa);
        }
        if ((flags & 64) != 0)
        {
            foreach (var face in faces)
            {
                LoadingContext.Check();
                // Validate every index, including invisible outgoing edges.
                foreach (var signed in face)
                    if (Math.Abs(signed) > points.Count) throw new InvalidDataException("polyface vertex index out of range");
                for (int i = 0; i < face.Length; i++)
                    if (face[i] > 0) Edge(face[i] - 1, Math.Abs(face[(i + 1) % face.Length]) - 1);
            }
        }
        else if ((flags & 16) != 0)
        {
            if (points.Count != m * n) throw new InvalidDataException("polygon mesh vertex count");
            for (int row = 0; row < m; row++)
                for (int column = 0; column < n; column++)
                {
                    if (row + 1 < m) Edge(row * n + column, (row + 1) * n + column);
                    else if ((flags & 1) != 0) Edge(row * n + column, column);
                    if (column + 1 < n) Edge(row * n + column, row * n + column + 1);
                    else if ((flags & 32) != 0) Edge(row * n + column, row * n);
                }
        }
        else
        {
            if (points.Count < 2) throw new InvalidDataException("3D polyline has fewer than two vertices");
            for (int i = 1; i < points.Count; i++) Edge(i - 1, i);
            if ((flags & 1) != 0) Edge(points.Count - 1, 0);
        }
        if (curves.Count == 0) throw new InvalidDataException("no visible edges");
        return new("POLYLINE", layer, handle, curves) { RenderDots = dots.ToArray(), SpatialGeometry=new(emitted.Select(e=>new DxfSpatialLine(points[e.A],points[e.B])),[],[]) };
    }

    private static int Integer(IReadOnlyList<(int Code, string Value)> fields, int code)
    {
        var values = fields.Where(f => f.Code == code).ToArray();
        if (values.Length == 0) return 0;
        if (values.Length != 1 || !int.TryParse(values[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException("invalid integer group " + code);
        return value;
    }
    private static double Coordinate(IReadOnlyList<(int Code, string Value)> fields, int code, bool required = true)
    {
        var values = fields.Where(f => f.Code == code).ToArray();
        if (values.Length == 0 && !required) return 0;
        if (values.Length != 1 || !double.TryParse(values[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new InvalidDataException("invalid coordinate group " + code);
        return value;
    }
}
