using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace VibeDxf.Wpf;

// A bounded ACIS boundary reader, not a solid modelling kernel. Only actual straight
// edge topology is projected; unsupported curves never become endpoint chords.
// Format references: Autodesk's 3DSOLID DXF reference and ezdxf's MIT-licensed
// acis/{const,sab,sat,entities}.py and sections/acdsdata.py. No upstream code is embedded.
internal static class DxfSolidProjection
{
    private const int MaxBytes = 16 * 1024 * 1024, MaxRecords = 200_000, MaxTokens = 2_000_000;
    private readonly record struct V3(double X, double Y, double Z);
    private readonly record struct Token(int Kind, object Value);
    private sealed record Node(string Name, Token[] Data);
    private static InvalidDataException Invalid() => new("Invalid ACIS solid data.");
    private static NotSupportedException Unsupported(string reason) => new($"3DSOLID ({reason})");

    internal static List<Curve> Read(IReadOnlyList<(int Code, string Value)> fields,
        IReadOnlyList<(int Code, string Value)>? binaryFields)
    {
        List<Node> nodes;
        bool binary;
        if (binaryFields != null)
        {
            var chunks = binaryFields.Where(f => f.Code == 310).Select(f => f.Value).ToArray();
            long size = chunks.Sum(s => (long)s.Length / 2);
            if (size == 0 || size > MaxBytes || chunks.Any(s => (s.Length & 1) != 0)) throw Invalid();
            var declared = binaryFields.FirstOrDefault(f => f.Code == 94).Value;
            if (!int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != size) throw Invalid();
            var bytes = new byte[(int)size]; int offset = 0;
            foreach (var chunk in chunks)
            {
                LoadingContext.Check();
                var decoded=Convert.FromHexString(chunk);
                decoded.CopyTo(bytes,offset); offset+=decoded.Length;
            }
            nodes = ReadSab(bytes); binary = true;
        }
        else
        {
            // Only modeler data before the next subclass belongs to inline SAT.
            var lines = new List<StringBuilder>(); bool modeler = false; long characters = 0;
            foreach (var field in fields)
            {
                if (field.Code == 100) { modeler = field.Value == "AcDbModelerGeometry"; continue; }
                LoadingContext.Check();
                if (modeler && field.Code is 1 or 3)
                {
                    characters += field.Value.Length;
                    if (characters > MaxBytes) throw Unsupported("data limit");
                }
                if (modeler && field.Code == 1) lines.Add(new StringBuilder(field.Value));
                else if (modeler && field.Code == 3)
                {
                    if (lines.Count == 0) throw Invalid();
                    lines[^1].Append(field.Value);
                }
            }
            if (lines.Count == 0) throw Unsupported("missing ACIS geometry");
            nodes = ReadSat(lines.Select(s=>s.ToString()).ToList()); binary = false;
        }
        return Project(nodes, binary);
    }

    private static List<Node> ReadSab(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        string signature = Encoding.ASCII.GetString(reader.ReadBytes(15));
        if (signature is not ("ACIS BinaryFile" or "ASM BinaryFile4")) throw Unsupported("unknown binary ACIS signature");
        int version = reader.ReadInt32(), declaredRecords = reader.ReadInt32();
        int declaredBodies = reader.ReadInt32(); reader.ReadInt32();
        if (version < 700 || version > 30000) throw Unsupported("ACIS version");
        if (declaredRecords < 0 || declaredRecords > MaxRecords || declaredBodies < 1 || declaredBodies > MaxRecords) throw Invalid();
        string String(int length)
        {
            if (length < 0 || length > MaxBytes || length > input.Length - input.Position) throw Invalid();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
        string ShortString() => String(reader.ReadByte());
        for (int i = 0; i < 3; i++) { if (reader.ReadByte() != 7) throw Invalid(); ShortString(); }
        for (int i = 0; i < 3; i++) { if (reader.ReadByte() != 6) throw Invalid(); Finite(reader.ReadDouble()); }
        var result = new List<Node>(); int tokens = 0; bool ended = false;
        while (input.Position < input.Length)
        {
            LoadingContext.Check();
            var name = new List<string>();
            while (true)
            {
                int tag = reader.ReadByte();
                if (tag is not (13 or 14)) throw Invalid();
                name.Add(ShortString());
                if (name.Count > 16) throw Invalid();
                if (tag == 13) break;
            }
            string kind = string.Join('-', name);
            if (kind is "End-of-ACIS-data" or "End-of-ASM-data" or "Begin-of-ACIS-History-data") { ended = true; break; }
            if (reader.ReadByte() != 12) throw Invalid(); reader.ReadInt32(); // attribute pointer, not geometry
            if (reader.ReadByte() != 4) throw Invalid(); reader.ReadInt32(); // persistence ID; pointers use record order
            var data = new List<Token>(); int nesting = 0;
            while (true)
            {
                if (++tokens > MaxTokens) throw Unsupported("token limit");
                if ((tokens & 1023) == 0) LoadingContext.Check();
                int tag = reader.ReadByte();
                if (tag == 17) { if (nesting != 0) throw Invalid(); break; }
                object value = tag switch
                {
                    4 or 12 or 21 => reader.ReadInt32(),
                    6 or 23 => Finite(reader.ReadDouble()),
                    7 or 13 or 14 => ShortString(),
                    18 => String(reader.ReadInt32()),
                    19 or 20 => new V3(Finite(reader.ReadDouble()), Finite(reader.ReadDouble()), Finite(reader.ReadDouble())),
                    10 or 11 => tag == 10,
                    15 => ++nesting,
                    16 => --nesting,
                    _ => throw Unsupported($"binary ACIS tag {tag}")
                };
                if (nesting is < 0 or > 64) throw Invalid();
                data.Add(new(tag, value));
            }
            result.Add(new(kind, data.ToArray()));
            if (result.Count > MaxRecords) throw Unsupported("record limit");
        }
        if (!ended || (declaredRecords != 0 && declaredRecords != result.Count)) throw Invalid();
        if (result.Count(n => n.Name is "body" or "asmheader") != declaredBodies) throw Invalid();
        return result;
    }

    private static List<Node> ReadSat(List<string> lines)
    {
        if (!int.TryParse(lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out _))
            lines = lines.Select(DecodeSat).ToList();
        if (lines.Count < 4 || !int.TryParse(lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var version)) throw Invalid();
        if (version < 700 || version > 30000) throw Unsupported("ACIS version");
        string text = string.Join('\n', lines.Skip(3));
        var result = new List<Node>(); int pos = 0, tokens = 0; bool ended = false;
        string Next()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) throw Invalid();
            int start = pos;
            if (text[pos] == '#') { pos++; return "#"; }
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '#') pos++;
            return text[start..pos];
        }
        while (pos < text.Length)
        {
            LoadingContext.Check();
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos == text.Length) break;
            string name = Next();
            if (name is "End-of-ACIS-data" or "End-of-ASM-data" or "Begin-of-ACIS-History-data") { ended = true; break; }
            if (name.StartsWith('-') && int.TryParse(name, out int sequence))
            {
                if (-(long)sequence != result.Count) throw Invalid();
                name = Next();
            }
            if (!Next().StartsWith('$') || !int.TryParse(Next(), out _)) throw Invalid();
            var data = new List<Token>();
            while (true)
            {
                if (++tokens > MaxTokens) throw Unsupported("token limit");
                if ((tokens & 1023) == 0) LoadingContext.Check();
                string word = Next(); if (word == "#") break;
                if (word.StartsWith('$'))
                {
                    if (!int.TryParse(word.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pointer)) throw Invalid();
                    data.Add(new(12, pointer));
                }
                else if (word.StartsWith('@'))
                {
                    if (!int.TryParse(word.AsSpan(1), out int length) || length < 0 || length > MaxBytes) throw Invalid();
                    if (pos >= text.Length || !char.IsWhiteSpace(text[pos])) throw Invalid();
                    pos++;
                    if (length > text.Length - pos) throw Invalid();
                    data.Add(new(7, text.Substring(pos, length))); pos += length;
                }
                else if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) data.Add(new(6, Finite(number)));
                else data.Add(new(7, word));
            }
            result.Add(new(name, data.ToArray()));
            if (result.Count > MaxRecords) throw Unsupported("record limit");
        }
        if (!ended) throw Invalid();
        return result;
    }

    private static string DecodeSat(string line)
    {
        var result = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c > 127) throw Invalid();
            result.Append(c switch { ' ' => ' ', '@' => '_', '_' => '@', >= 'A' and <= '^' => (char)(159 - c), _ => (char)(c ^ 95) });
            if (c == '^' && i + 1 < line.Length && line[i + 1] == ' ') i++;
        }
        return result.ToString();
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : throw Invalid();
    private static Token At(Node node, int index) => index >= 0 && index < node.Data.Length ? node.Data[index] : throw Invalid();
    private static int Ptr(Node node, int index)
    {
        var token = At(node, index);
        return token.Kind == 12 ? (int)token.Value : throw Invalid();
    }
    private static double Real(Token token) => token.Value is double d ? d : throw Invalid();
    private static Node Refer(List<Node> nodes, int pointer, string name)
    {
        if (pointer < 0 || pointer >= nodes.Count || nodes[pointer].Name != name) throw Invalid();
        return nodes[pointer];
    }
    private static V3 Position(List<Node> nodes, int vertex, bool binary)
    {
        var node = Refer(nodes, vertex, "vertex");
        if (Ptr(node, 0) != -1) throw Unsupported("patterned ACIS topology");
        var point = Refer(nodes, Ptr(node, binary ? 3 : 2), "point");
        if (Ptr(point, 0) != -1) throw Unsupported("patterned ACIS topology");
        if (binary) return At(point, 1).Value is V3 p ? p : throw Invalid();
        return new(Real(At(point, 1)), Real(At(point, 2)), Real(At(point, 3)));
    }

    private static List<Curve> Project(List<Node> nodes, bool binary)
    {
        var output = new List<Curve>(); var segments = new HashSet<(Point, Point)>(); int work = 0;
        var bodies = nodes.Select((n, i) => (Node: n, Index: i)).Where(p => p.Node.Name == "body").ToArray();
        if (bodies.Length == 0) throw Invalid();
        foreach (var body in bodies)
        {
            if (Ptr(body.Node, 0) != -1) throw Unsupported("patterned ACIS body");
            int transform = Ptr(body.Node, 3); double[]? matrix = null;
            if (transform != -1)
            {
                var node = Refer(nodes, transform, "transform");
                if (binary && node.Data.Length >= 4 && node.Data.Take(4).All(t => t.Value is V3))
                    matrix = node.Data.Take(4).SelectMany(t => { var v = (V3)t.Value; return new[] { v.X, v.Y, v.Z }; }).ToArray();
                else if (binary && At(node, 0).Value is string s)
                {
                    matrix = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12).Select(v => Finite(double.Parse(v, CultureInfo.InvariantCulture))).ToArray();
                    if (matrix.Length != 12) throw Invalid();
                }
                else if (!binary) matrix = node.Data.Take(12).Select(Real).ToArray();
                else throw Invalid();
                if (matrix.Length != 12) throw Invalid();
            }
            Point XY(V3 p)
            {
                double x = matrix == null ? p.X : p.X * matrix[0] + p.Y * matrix[3] + p.Z * matrix[6] + matrix[9];
                double y = matrix == null ? p.Y : p.X * matrix[1] + p.Y * matrix[4] + p.Z * matrix[7] + matrix[10];
                return new(Finite(x), Finite(y));
            }
            var pending = new Stack<int>(); pending.Push(body.Index); var seen = new HashSet<int>();
            while (pending.Count != 0)
            {
                LoadingContext.Check();
                if(++work>MaxTokens)throw Unsupported("topology work limit");
                int index = pending.Pop();
                if (index < 0 || index >= nodes.Count) throw Invalid();
                if (!seen.Add(index)) continue;
                var node = nodes[index];
                if (node.Name == "body" && index != body.Index) throw Unsupported("cross-linked ACIS bodies");
                if (node.Name is "pattern" or "wire" or "subshell") throw Unsupported($"ACIS {node.Name} topology");
                void Link(int field,string kind,bool optional=true)
                {
                    int pointer=Ptr(node,field);
                    if(pointer==-1&&optional)return;
                    if(pointer<0||pointer>=nodes.Count||
                        !(kind=="surface"?nodes[pointer].Name.EndsWith("-surface",StringComparison.Ordinal):nodes[pointer].Name==kind))throw Invalid();
                }
                if(node.Name is "body" or "lump" or "shell" or "face" or "loop" or "coedge" or "edge" or "vertex" or "point" or "straight-curve")
                    if(Ptr(node,0)!=-1)throw Unsupported("patterned ACIS topology");
                switch(node.Name)
                {
                    case "body": Link(1,"lump",false);Link(2,"wire");Link(3,"transform");break;
                    case "lump": Link(1,"lump");Link(2,"shell",false);Link(3,"body",false);break;
                    case "shell": Link(1,"shell");Link(2,"subshell");Link(3,"face",false);Link(4,"wire");Link(5,"lump",false);break;
                    case "face": Link(1,"face");Link(2,"loop",false);Link(3,"shell",false);Link(4,"subshell");Link(5,"surface",false);break;
                    case "loop": Link(1,"loop");Link(2,"coedge",false);Link(3,"face",false);break;
                    case "coedge": Link(1,"coedge",false);Link(2,"coedge",false);Link(3,"coedge");Link(4,"edge",false);Link(6,"loop",false);break;
                    case "vertex": Link(1,"edge",false);Link(binary?3:2,"point",false);break;
                }
                if (node.Name == "edge")
                {
                    if (Ptr(node, 0) != -1) throw Unsupported("patterned ACIS edge");
                    int curvePointer = Ptr(node, 6);
                    if (curvePointer < 0 || curvePointer >= nodes.Count) throw Invalid();
                    if (nodes[curvePointer].Name != "straight-curve") throw Unsupported("curved ACIS edges");
                    Link(5,"coedge",false);
                    V3 start=Position(nodes,Ptr(node,1),binary),end=Position(nodes,Ptr(node,3),binary);
                    var underlying=nodes[curvePointer];
                    V3 origin,direction;
                    if(binary)
                    {
                        origin=At(underlying,1).Value is V3 o?o:throw Invalid();
                        direction=At(underlying,2).Value is V3 d?d:throw Invalid();
                    }
                    else
                    {
                        origin=new(Real(At(underlying,1)),Real(At(underlying,2)),Real(At(underlying,3)));
                        direction=new(Real(At(underlying,4)),Real(At(underlying,5)),Real(At(underlying,6)));
                    }
                    if(direction==default)throw Invalid();
                    void Matches(V3 vertex,double parameter)
                    {
                        var expected=new V3(Finite(origin.X+parameter*direction.X),Finite(origin.Y+parameter*direction.Y),Finite(origin.Z+parameter*direction.Z));
                        double scale=1+Math.Max(Math.Max(Math.Abs(vertex.X),Math.Abs(vertex.Y)),Math.Abs(vertex.Z));
                        if(Math.Abs(vertex.X-expected.X)>1e-7*scale||Math.Abs(vertex.Y-expected.Y)>1e-7*scale||Math.Abs(vertex.Z-expected.Z)>1e-7*scale)throw Invalid();
                    }
                    Matches(start,Real(At(node,2)));Matches(end,Real(At(node,4)));
                    Point a = XY(start), b = XY(end);
                    if (a.X > b.X || (a.X == b.X && a.Y > b.Y)) (a, b) = (b, a);
                    if (segments.Add((a, b)))
                    {
                        if(output.Count==MaxRecords)throw Unsupported("projected edge limit");
                        output.Add(Curve.Line(a, b));
                    }
                }
                foreach (var token in node.Data)
                    if (token.Kind == 12)
                    {
                        int pointer = (int)token.Value;
                        if (pointer < -1 || pointer >= nodes.Count) throw Invalid();
                        if (pointer != -1) pending.Push(pointer);
                    }
            }
        }
        if (output.Count == 0) throw Unsupported("no straight boundary edges");
        return output;
    }
}
