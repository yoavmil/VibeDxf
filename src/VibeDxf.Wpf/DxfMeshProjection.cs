using System.Globalization;
using System.IO;
using System.Windows;

namespace VibeDxf.Wpf;

// Level-0 wireframe in XY: no filled surface, hidden-line removal or 3D distances.
internal static class DxfMeshProjection
{
    private const int VertexLimit = 1_000_000, EdgeLimit = 1_000_000, FaceItemLimit = 4_000_000;
    internal static DxfEntity Read(IReadOnlyList<(int Code, string Value)> fields, string layer, string handle, Action<string> warn)
    {
        int end = fields.Count;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Code == 1001) { end = i; break; }
        int cursor = 0;
        while (cursor < end && fields[cursor].Code != 92) cursor++;
        if (cursor == end) throw new InvalidDataException("MESH vertex count is missing.");
        int subdivisions = 0;
        for (int i = 0; i < cursor; i++)
            if (fields[i].Code == 91) subdivisions = Integer(fields[i].Value, 0, 100);
        int vertexCount = Count(92, VertexLimit);
        if (vertexCount == 0 || vertexCount > (end - cursor) / 2) throw new InvalidDataException("MESH vertex list is empty or truncated.");
        var vertices = new Point[vertexCount];
        var elevations = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            LoadingContext.Check();
            double x = Number(10), y = Number(20);
            if (cursor < end && fields[cursor].Code == 30) elevations[i] = Number(30);
            vertices[i] = new Point(x, y);
        }
        int faceItems = Count(93, FaceItemLimit);
        if (faceItems > end - cursor) throw new InvalidDataException("MESH face list is truncated.");
        int faceEnd = cursor + faceItems;
        var edges = new HashSet<(int A, int B)>();
        while (cursor < faceEnd)
        {
            LoadingContext.Check();
            int count = Count(90, VertexLimit);
            if (count < 3 || count > faceEnd - cursor) throw new InvalidDataException("MESH face size is invalid.");
            int first = Index(), previous = first;
            var distinct = new HashSet<int> { first };
            for (int i = 1; i < count; i++)
            {
                LoadingContext.Check();
                int next = Index(); distinct.Add(next); AddEdge(previous, next); previous = next;
            }
            if (distinct.Count < 3) throw new InvalidDataException("MESH face needs three distinct vertices.");
            AddEdge(previous, first);
        }
        int edgeCount = Count(94, EdgeLimit);
        if ((long)edgeCount * 2 > end - cursor) throw new InvalidDataException("MESH edge list is truncated.");
        for (int i = 0; i < edgeCount; i++) { LoadingContext.Check(); AddEdge(Index(), Index()); }
        int creaseCount = Count(95, EdgeLimit);
        if (creaseCount != 0 && creaseCount != edgeCount) throw new InvalidDataException("MESH crease count does not match its edge count.");
        if (creaseCount > end - cursor) throw new InvalidDataException("MESH crease list is truncated.");
        for (int i = 0; i < creaseCount; i++)
        {
            LoadingContext.Check();
            double crease = Number(140);
            if (crease < 0 && crease != -1) throw new InvalidDataException("MESH crease value is invalid.");
        }
        int overrides = cursor < end && fields[cursor].Code == 90 ? Count(90, VertexLimit) : 0;
        if (overrides > 0) warn("MESH per-face appearance overrides are not displayed");
        if (overrides == 0 && cursor < end) throw new InvalidDataException("MESH has unexpected topology data.");
        if (edges.Count == 0) throw new InvalidDataException("MESH has no edges.");
        var curves = new List<Curve>(edges.Count);
        var dots = new HashSet<Point>();
        foreach (var edge in edges)
        {
            LoadingContext.Check();
            var a = vertices[edge.A]; var b = vertices[edge.B];
            curves.Add(Curve.Line(a, b));
            if (a == b && elevations[edge.A] != elevations[edge.B]) dots.Add(a);
        }
        warn("MESH is shown as an XY-projected wireframe; measurements ignore Z");
        if (subdivisions > 0) warn("MESH subdivision smoothing is shown as its level-0 control wireframe");
        return new DxfEntity("MESH", layer, handle, curves) { IsApproximate = subdivisions > 0, RenderDots = dots.ToArray() };
        string Take(int code)
        {
            if (cursor >= end || fields[cursor].Code != code) throw new InvalidDataException($"MESH expected group {code}.");
            return fields[cursor++].Value;
        }
        int Count(int code, int limit) => Integer(Take(code), 0, limit);
        int Index() => Integer(Take(90), 0, vertexCount - 1);
        double Number(int code)
        {
            if (!double.TryParse(Take(code), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidDataException("MESH coordinate/value is not finite.");
            return value;
        }
        void AddEdge(int a, int b)
        {
            if (a == b) return;
            edges.Add(a < b ? (a, b) : (b, a));
            if (edges.Count > EdgeLimit) throw new InvalidDataException("MESH projected edge limit exceeded.");
        }
    }
    private static int Integer(string text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new InvalidDataException("MESH count or vertex index is invalid or exceeds its limit.");
        return value;
    }
}
