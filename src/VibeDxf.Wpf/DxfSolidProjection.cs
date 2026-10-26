using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

// A bounded ACIS boundary reader, not a solid modelling kernel. Actual straight and
// elliptical edge topology is projected; edge-free torus faces have a separate display mesh.
// Unsupported curves never become endpoint chords or invented boundary edges.
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
        => ReadProjected(fields, binaryFields, p => new Point(p.X,p.Y), out _);

    internal static List<Curve> Read(IReadOnlyList<(int Code, string Value)> fields,
        IReadOnlyList<(int Code, string Value)>? binaryFields, out bool approximate)
        => ReadProjected(fields, binaryFields, p => new Point(p.X,p.Y), out approximate);

    // The supplied projection must be affine. Body placement is applied in full XYZ first.
    internal static List<Curve> ReadProjected(IReadOnlyList<(int Code, string Value)> fields,
        IReadOnlyList<(int Code, string Value)>? binaryFields, Func<Point3D,Point> projection, out bool approximate)
    {
        return ReadGeometry(fields,binaryFields,projection,out approximate,out _,out _);
    }

    internal static DxfEntity ReadEntity(IReadOnlyList<(int Code,string Value)> fields,
        IReadOnlyList<(int Code,string Value)>? binaryFields,string layer,string handle,Func<Point3D,Point>? projection=null)
    {
        projection??=p=>new Point(p.X,p.Y);
        var curves=ReadGeometry(fields,binaryFields,projection,out bool approximate,out var spatial,out var fill);
        var bounds=curves.Aggregate(Rect.Empty,(r,c)=>{r.Union(c.Bounds);return r;});
        foreach(var point in fill)bounds.Union(point);
        return new DxfEntity("3DSOLID",layer,handle,curves)
        {
            IsApproximate=approximate,SpatialGeometry=spatial,FillTriangles=Array.AsReadOnly(fill.ToArray()),Bounds=bounds,
            RenderDots=curves.Where(c=>c.Start==c.End).Select(c=>c.Start).Distinct().ToArray()
        };
    }

    private static List<Curve> ReadGeometry(IReadOnlyList<(int Code,string Value)> fields,
        IReadOnlyList<(int Code,string Value)>? binaryFields,Func<Point3D,Point> projection,out bool approximate,
        out DxfSpatialGeometry spatial,out List<Point> fill)
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
        return Project(nodes, binary, projection, out approximate,out spatial,out fill);
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
        if (lines.Count < 4) throw Invalid();
        var header=lines[0].Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(header.Length!=4||!int.TryParse(header[0],out int version)||!int.TryParse(header[1],out int declaredRecords)||
            !int.TryParse(header[2],out int declaredBodies)||!int.TryParse(header[3],out _))throw Invalid();
        if ((version != 400 && version < 700) || version > 30000) throw Unsupported("ACIS version");
        bool legacy=version==400;
        if(declaredRecords<0||declaredRecords>MaxRecords||declaredBodies<1||declaredBodies>MaxRecords)throw Invalid();
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
            string attribute=Next();
            if(!attribute.StartsWith('$')||!int.TryParse(attribute.AsSpan(1),NumberStyles.Integer,CultureInfo.InvariantCulture,out int attributePointer)||attributePointer < -1)throw Invalid();
            if(!legacy&&!int.TryParse(Next(),NumberStyles.Integer,CultureInfo.InvariantCulture,out _))throw Invalid();
            var data = new List<Token>();
            // SAT 4 has the attribute pointer, but no persistence ID or pattern pointer.
            // Normalize only the supported geometry classes; transforms and attribute records have different layouts.
            if(legacy&&name is "body" or "lump" or "shell" or "face" or "loop" or "coedge" or "edge" or "vertex" or "point" or "straight-curve" or "ellipse-curve" or "plane-surface" or "torus-surface")
                data.Add(new(12,-1));
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
        if (!ended||(declaredRecords!=0&&declaredRecords!=result.Count)||result.Count(n=>n.Name is "body" or "asmheader")!=declaredBodies) throw Invalid();
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

    private static List<Curve> Project(List<Node> nodes, bool binary, Func<Point3D,Point> projection, out bool approximate,out DxfSpatialGeometry spatial,out List<Point> fill)
    {
        approximate = false;
        var spatialLines=new List<DxfSpatialLine>();var spatialTriangles=new List<DxfSpatialTriangle>();bool spatialApproximate=false;
        fill=[];
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
            Point3D XYZ(V3 p)
            {
                double x = matrix == null ? p.X : p.X * matrix[0] + p.Y * matrix[3] + p.Z * matrix[6] + matrix[9];
                double y = matrix == null ? p.Y : p.X * matrix[1] + p.Y * matrix[4] + p.Z * matrix[7] + matrix[10];
                double z = matrix == null ? p.Z : p.X * matrix[2] + p.Y * matrix[5] + p.Z * matrix[8] + matrix[11];
                return new(Finite(x),Finite(y),Finite(z));
            }
            Point XY(V3 p){var point=projection(XYZ(p));return new(Finite(point.X),Finite(point.Y));}
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
                if(node.Name is "body" or "lump" or "shell" or "face" or "loop" or "coedge" or "edge" or "vertex" or "point" or "straight-curve" or "ellipse-curve")
                    if(Ptr(node,0)!=-1)throw Unsupported("patterned ACIS topology");
                switch(node.Name)
                {
                    case "body": Link(1,"lump",false);Link(2,"wire");Link(3,"transform");break;
                    case "lump": Link(1,"lump");Link(2,"shell",false);Link(3,"body",false);break;
                    case "shell": Link(1,"shell");Link(2,"subshell");Link(3,"face",false);Link(4,"wire");Link(5,"lump",false);break;
                    case "face":
                        Link(1,"face");Link(2,"loop");Link(3,"shell",false);Link(4,"subshell");Link(5,"surface",false);
                        if(Ptr(node,2)==-1)
                        {
                            var surface=nodes[Ptr(node,5)];
                            if(surface.Name!="torus-surface")throw Unsupported("untrimmed ACIS surface: "+surface.Name);
                            bool reversedFace=At(node,6).Value switch {"forward"=>false,"reversed"=>true,bool b=>b,_=>throw Invalid()};
                            var mesh=Torus(surface,binary,XYZ,reversedFace);
                            if(spatialTriangles.Count+mesh.Count>MaxRecords)throw Unsupported("surface triangle limit");
                            spatialTriangles.AddRange(mesh);spatialApproximate=true;approximate=true;
                            foreach(var triangle in mesh)
                            {
                                foreach(var vertex in new[]{triangle.A,triangle.B,triangle.C})
                                {var point=projection(vertex);fill.Add(new(Finite(point.X),Finite(point.Y)));}
                            }
                        }
                        break;
                    case "loop": Link(1,"loop");Link(2,"coedge",false);Link(3,"face",false);break;
                    case "coedge": Link(1,"coedge",false);Link(2,"coedge",false);Link(3,"coedge");Link(4,"edge",false);Link(6,"loop",false);break;
                    case "vertex": Link(1,"edge",false);Link(binary?3:2,"point",false);break;
                }
                if (node.Name == "edge")
                {
                    if (Ptr(node, 0) != -1) throw Unsupported("patterned ACIS edge");
                    int curvePointer = Ptr(node, 6);
                    if (curvePointer < 0 || curvePointer >= nodes.Count) throw Invalid();
                    Link(5,"coedge",false);
                    V3 start=Position(nodes,Ptr(node,1),binary),end=Position(nodes,Ptr(node,3),binary);
                    var underlying=nodes[curvePointer];
                    if(Ptr(underlying,0)!=-1)throw Unsupported("patterned ACIS curve");
                    V3 VectorAt(int index) => binary
                        ? At(underlying,index).Value is V3 v ? v : throw Invalid()
                        : new(Real(At(underlying,index)),Real(At(underlying,index+1)),Real(At(underlying,index+2)));
                    void Matches(V3 vertex,V3 expected)
                    {
                        double scale=1+Math.Max(Math.Max(Math.Abs(vertex.X),Math.Abs(vertex.Y)),Math.Abs(vertex.Z));
                        if(Math.Abs(vertex.X-expected.X)>1e-7*scale||Math.Abs(vertex.Y-expected.Y)>1e-7*scale||Math.Abs(vertex.Z-expected.Z)>1e-7*scale)throw Invalid();
                    }
                    void Append(Curve curve)
                    {
                        if(output.Count==MaxRecords)throw Unsupported("projected edge limit");
                        if(curve.IsLine)
                        {
                            Point a=curve.Start,b=curve.End;
                            if(a.X>b.X||(a.X==b.X&&a.Y>b.Y))(a,b)=(b,a);
                            if(!segments.Add((a,b)))return;
                        }
                        output.Add(curve);
                    }
                    double first=Real(At(node,2)),last=Real(At(node,4));
                    if(underlying.Name=="straight-curve")
                    {
                        var origin=VectorAt(1);var direction=VectorAt(binary?2:4);
                        if(direction==default)throw Invalid();
                        V3 Evaluate(double t)=>new(Finite(origin.X+t*direction.X),Finite(origin.Y+t*direction.Y),Finite(origin.Z+t*direction.Z));
                        Matches(start,Evaluate(first));Matches(end,Evaluate(last));
                        Append(Curve.Line(XY(start),XY(end)));
                        spatialLines.Add(new(XYZ(start),XYZ(end)));
                    }
                    else if(underlying.Name=="ellipse-curve")
                    {
                        var center=VectorAt(1);var normal=VectorAt(binary?2:4);var major=VectorAt(binary?3:7);
                        double ratio=Real(At(underlying,binary?4:10));
                        double length=Math.Sqrt(normal.X*normal.X+normal.Y*normal.Y+normal.Z*normal.Z);
                        double radius=Math.Sqrt(major.X*major.X+major.Y*major.Y+major.Z*major.Z);
                        if(!double.IsFinite(length)||!double.IsFinite(radius)||length<1e-15||radius<1e-15||ratio<=0||ratio>1)throw Invalid();
                        normal=new(normal.X/length,normal.Y/length,normal.Z/length);
                        if(Math.Abs(normal.X*major.X+normal.Y*major.Y+normal.Z*major.Z)>1e-9*radius)throw Invalid();
                        var minor=new V3((normal.Y*major.Z-normal.Z*major.Y)*ratio,
                            (normal.Z*major.X-normal.X*major.Z)*ratio,(normal.X*major.Y-normal.Y*major.X)*ratio);
                        V3 Evaluate(double t)=>new(Finite(center.X+major.X*Math.Cos(t)+minor.X*Math.Sin(t)),
                            Finite(center.Y+major.Y*Math.Cos(t)+minor.Y*Math.Sin(t)),Finite(center.Z+major.Z*Math.Cos(t)+minor.Z*Math.Sin(t)));
                        double sweep=last-first;
                        if(!double.IsFinite(sweep)||Math.Abs(sweep)<1e-14||Math.Abs(sweep)>Math.Tau+1e-9)throw Invalid();
                        Matches(start,Evaluate(first));Matches(end,Evaluate(last));
                        // Retain actual XYZ curve samples independently of the XY representation.
                        var majorWorld=XYZ(new(center.X+major.X,center.Y+major.Y,center.Z+major.Z))-XYZ(center);
                        var minorWorld=XYZ(new(center.X+minor.X,center.Y+minor.Y,center.Z+minor.Z))-XYZ(center);
                        double spatialRequired=Math.Max(1,Math.Ceiling(Math.Abs(sweep)*Math.Sqrt((majorWorld.Length+minorWorld.Length)/(8*SplineGeometry.Tolerance))));
                        if(!double.IsFinite(spatialRequired)||spatialRequired>MaxRecords-spatialLines.Count)throw Unsupported("spatial edge limit");
                        int spatialCount=(int)spatialRequired;var spatialPrevious=XYZ(Evaluate(first));
                        for(int j=1;j<=spatialCount;j++){LoadingContext.Check();var next=XYZ(Evaluate(first+sweep*j/spatialCount));spatialLines.Add(new(spatialPrevious,next));spatialPrevious=next;}
                        spatialApproximate=true;
                        Point c=XY(center);
                        Vector u=XY(new(center.X+major.X,center.Y+major.Y,center.Z+major.Z))-c;
                        Vector v=XY(new(center.X+minor.X,center.Y+minor.Y,center.Z+minor.Z))-c;
                        double scale=Math.Max(u.Length,v.Length);
                        if(scale>0&&Math.Abs(u.Length-v.Length)<=1e-12&&Math.Abs(Vector.Multiply(u,v))<=1e-12*scale)
                        {
                            double orientation=Vector.CrossProduct(u,v)<0?-1:1;
                            Append(Curve.Arc(c,u.Length,Math.Atan2(u.Y,u.X)+orientation*first,orientation*sweep));
                        }
                        else
                        {
                            // |q''(t)| <= |u|+|v|. Linear interpolation error <= M*h^2/8,
                            // including edge-on ellipses and intervals whose endpoints coincide.
                            double bound=u.Length+v.Length;
                            double required=Math.Max(1,Math.Ceiling(Math.Abs(sweep)*Math.Sqrt(bound/(8*SplineGeometry.Tolerance))));
                            if(!double.IsFinite(required)||required>MaxRecords-output.Count)throw Unsupported("projected edge limit");
                            int count=(int)required;
                            Point previous=XY(Evaluate(first));
                            for(int j=1;j<=count;j++)
                            {
                                LoadingContext.Check();
                                Point next=XY(Evaluate(first+sweep*j/count));
                                Append(Curve.Line(previous,next));previous=next;
                            }
                            approximate=true;
                        }
                    }
                    else throw Unsupported("unsupported ACIS curve: "+underlying.Name);
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
        if (output.Count == 0 && spatialTriangles.Count==0) throw Unsupported("no boundary edges");
        spatial=new DxfSpatialGeometry(spatialLines,spatialTriangles,[],spatialApproximate);
        return output;
    }    // Full, untrimmed ring tori have no boundary edges at all. Their triangles are
    // display geometry only: tessellation segments are not measurable ACIS edges.
    private static List<DxfSpatialTriangle> Torus(Node surface,bool binary,Func<V3,Point3D> place,bool reversedFace)
    {
        if(Ptr(surface,0)!=-1)throw Unsupported("patterned ACIS torus");
        V3 VectorAt(int index)=>binary?At(surface,index).Value is V3 v?v:throw Invalid():
            new(Real(At(surface,index)),Real(At(surface,index+1)),Real(At(surface,index+2)));
        var center=VectorAt(1);var axis=VectorAt(binary?2:4);var reference=VectorAt(binary?5:9);
        double major=Real(At(surface,binary?3:7)),minor=Real(At(surface,binary?4:8));
        var normal=new Vector3D(axis.X,axis.Y,axis.Z);var x=new Vector3D(reference.X,reference.Y,reference.Z);
        if(!double.IsFinite(normal.Length)||!double.IsFinite(x.Length)||normal.Length<1e-15||x.Length<1e-15||!(major>minor&&minor>0))throw Invalid();
        normal.Normalize();
        if(Math.Abs(Vector3D.DotProduct(normal,x))>1e-9*x.Length)throw Invalid();
        x.Normalize();var y=Vector3D.CrossProduct(normal,x);y.Normalize();
        int sense=binary?6:12;var parameterSense=At(surface,sense).Value;
        double sign=parameterSense switch {"forward_v"=>1,"reversed_v"=>-1,bool b=>b?-1:1,_=>throw Invalid()};
        if(surface.Data.Length!=sense+5)throw Unsupported("torus parameter layout");
        for(int i=sense+1;i<surface.Data.Length;i++)
            if(!(At(surface,i).Value is string t&&t=="I")&&At(surface,i).Value is not bool)throw Unsupported("trimmed torus parameter domain");
        V3 Local(double u,double v)
        {
            double radial=major+minor*Math.Cos(v),z=sign*minor*Math.Sin(v);
            var d=x*(radial*Math.Cos(u))+y*(radial*Math.Sin(u))+normal*z;
            return new(Finite(center.X+d.X),Finite(center.Y+d.Y),Finite(center.Z+d.Z));
        }
        var origin=place(center);
        double Length(Vector3D v)=>(place(new(center.X+v.X,center.Y+v.Y,center.Z+v.Z))-origin).Length;
        // Bound the separate u/v interpolation errors after body placement.
        // This display mesh uses 0.02 drawing units before INSERT scaling.
        const double tolerance=0.02;
        double mu=(major+minor)*(Length(x)+Length(y)),mv=minor*(Length(x)+Length(y)+Length(normal));
        double nu=Math.Max(12,Math.Ceiling(Math.Tau*Math.Sqrt(mu/(4*tolerance))));
        double nv=Math.Max(12,Math.Ceiling(Math.Tau*Math.Sqrt(mv/(4*tolerance))));
        if(!double.IsFinite(nu)||!double.IsFinite(nv)||2*nu*nv>MaxRecords)throw Unsupported("surface triangle limit");
        int uCount=(int)nu,vCount=(int)nv;var output=new List<DxfSpatialTriangle>(2*uCount*vCount);
        for(int i=0;i<uCount;i++)
        {
            LoadingContext.Check();
            for(int j=0;j<vCount;j++)
            {
                var a=place(Local(Math.Tau*i/uCount,Math.Tau*j/vCount));
                var b=place(Local(Math.Tau*(i+1)/uCount,Math.Tau*j/vCount));
                var c=place(Local(Math.Tau*(i+1)/uCount,Math.Tau*(j+1)/vCount));
                var d=place(Local(Math.Tau*i/uCount,Math.Tau*(j+1)/vCount));
                if((sign<0)^reversedFace){output.Add(new(a,c,b));output.Add(new(a,d,c));}
                else {output.Add(new(a,b,c));output.Add(new(a,c,d));}
            }
        }
        return output;
    }

}
