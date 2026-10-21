using System.Globalization;
using System.IO;
using System.Windows;
namespace VibeDxf;
// A paper-space window onto model space; ModelToPaper follows ezdxf (top views only).
public sealed record DxfViewportWindow(string Handle, Rect Paper, System.Windows.Media.Matrix ModelToPaper);
/// <summary>One paper layout (sheet tab): its paper-space entities and the viewports that show model space.</summary>
public sealed class DxfLayout(string name)
{
    public string Name { get; } = name;
    public List<DxfEntity> Entities { get; } = [];
    public List<DxfViewportWindow> Viewports { get; } = [];
    public Rect Bounds => Viewports.Aggregate(Entities.Aggregate(Rect.Empty, (r, e) => { r.Union(e.Bounds); return r; }),
        (r, v) => { r.Union(v.Paper); return r; });
    // The first VIEWPORT of every layout is the sheet's own view, not a window.
    internal bool SheetViewSeen { get; set; }
}
public sealed class DxfDocument
{
    /// <summary>Model space. Paper-space records belong to <see cref="Layouts"/>.</summary>
    public List<DxfEntity> Entities { get; } = [];
    /// <summary>Paper layouts in tab order.</summary>
    public List<DxfLayout> Layouts { get; } = [];
    public Dictionary<string, int> Skipped { get; } = [];
    public string Units { get; set; } = "drawing units (unspecified)";
    public Dictionary<string,int> RenderingWarnings { get; } = [];
    public string? UnsupportedWarning => Skipped.Count == 0 && RenderingWarnings.Count == 0 ? null :
        (Skipped.Count>0 ? $"This drawing is only partially displayed. {Skipped.Values.Sum():N0} unsupported records were skipped.\n\n" : "Some drawing features are displayed approximately.\n\n") +
        string.Join("\n", Skipped.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {p.Value:N0}")) +
        (RenderingWarnings.Count==0?"":"\n\nRendering notes:\n"+string.Join("\n",RenderingWarnings.OrderBy(p=>p.Key).Select(p=>$"{p.Key}: {p.Value:N0}"))) +
        "\n\nSkipped block inserts may contain additional geometry that is not shown. Measurements apply only to displayed geometry.";
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
            pairs.Add(new Pair(n, n is 1 or 3 ? value : value.Trim()));
        }
        var doc = new DxfDocument();
        for (int j = 0; j + 1 < pairs.Count; j++)
            if (pairs[j].Code == 9 && pairs[j].Value == "$INSUNITS")
                doc.Units = pairs[j + 1].Value switch { "1" => "inches", "2" => "feet", "4" => "mm", "5" => "cm", "6" => "m", "0" => "drawing units (unitless)", var u => $"drawing units (INSUNITS={u})" };
        var blocks=new Dictionary<string,(Record Header,List<Record> Records)>(StringComparer.OrdinalIgnoreCase);
        var blockNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var layoutRecords=new List<Record>();
        var entities=new List<Record>();string section="";List<Record>? blockRecords=null;
        for(int i=0;i<pairs.Count;)
        {
            if(pairs[i].Code!=0){i++;continue;}
            int end=i+1;while(end<pairs.Count&&pairs[end].Code!=0)end++;
            var record=new Record(pairs[i].Value,pairs.GetRange(i+1,end-i-1));i=end;
            if(record.Kind=="SECTION"){section=record.S(2);continue;}
            if(record.Kind=="ENDSEC"){section="";continue;}
            if(section=="ENTITIES")entities.Add(record);
            else if(section=="TABLES"&&record.Kind=="BLOCK_RECORD")blockNames[record.S(5)]=record.S(2);
            else if(section=="OBJECTS"&&record.Kind=="LAYOUT")layoutRecords.Add(record);
            else if(section=="BLOCKS")
            {
                if(record.Kind=="BLOCK")
                {
                    blockRecords=[];blocks[record.S(2)]=(record,blockRecords);
                }
                else if(record.Kind=="ENDBLK")blockRecords=null;
                else blockRecords?.Add(record);
            }
        }
        void Skip(string kind)=>doc.Skipped[kind]=doc.Skipped.GetValueOrDefault(kind)+1;
        void Note(string note)=>doc.RenderingWarnings[note]=doc.RenderingWarnings.GetValueOrDefault(note)+1;
        int expansionCount=0;
        // LAYOUT objects name the sheet tabs and own a *Paper_Space block. The active layout's content is
        // stored in ENTITIES with group 67=1; every other layout's content is its block's records.
        var layouts=new List<(DxfLayout Layout,int Tab,string Block)>();
        foreach(var record in layoutRecords)
        {
            var own=record.Fields.SkipWhile(f=>!(f.Code==100&&f.Value=="AcDbLayout")).ToList();
            var block=blockNames.GetValueOrDefault(own.FirstOrDefault(f=>f.Code==330)?.Value??"","");
            if(block.Length==0||block.Equals("*Model_Space",StringComparison.OrdinalIgnoreCase))continue;
            var name=own.FirstOrDefault(f=>f.Code==1)?.Value;
            layouts.Add((new DxfLayout(string.IsNullOrWhiteSpace(name)?block:name),(int)new Record("",own).N(71),block));
        }
        layouts.Sort((a,b)=>a.Tab.CompareTo(b.Tab));
        var active=layouts.FirstOrDefault(l=>l.Block.Equals("*Paper_Space",StringComparison.OrdinalIgnoreCase)).Layout;
        // Files without LAYOUT objects (R12) still mark paper space with group 67.
        if(active==null&&entities.Any(r=>r.N(67)!=0)){active=new DxfLayout("Layout");layouts.Insert(0,(active,0,"*Paper_Space"));}
        doc.Layouts.AddRange(layouts.Select(l=>l.Layout));
        // Current output: null is model space. Block children follow their insert.
        DxfLayout? target=null;bool entitiesPass=true;
        List<DxfEntity> Output()=>target?.Entities??doc.Entities;
        void ReadViewport(Record r,DxfLayout layout)
        {
            var flags=(long)r.N(90);
            // The sheet view is ID 1 in the active layout. Inactive layouts store ID and status 0 for every
            // viewport, so there the first one is the sheet view and the status is not meaningful.
            bool first=!layout.SheetViewSeen;layout.SheetViewSeen=true;
            if(r.N(69)==1||(first&&r.N(69)==0))return;
            if((flags&131072)!=0||(layout==active&&r.Fields.Any(f=>f.Code==68)&&r.N(68)==0))return;
            if((flags&1)!=0||Math.Abs(r.N(16))>1e-9||Math.Abs(r.N(26))>1e-9||r.N(36,1)<=0){Skip("VIEWPORT (3D view)");return;}
            double width=r.N(40),height=r.N(41),viewHeight=r.N(45);
            if(!(width>0&&height>0&&viewHeight>0)){Skip("VIEWPORT (invalid view)");return;}
            var scale=height/viewHeight;var transform=System.Windows.Media.Matrix.Identity;
            transform.Translate(-r.N(17),-r.N(27));transform.Scale(scale,scale);transform.Rotate(r.N(51));
            transform.Translate(r.N(10)-r.N(12)*scale,r.N(20)-r.N(22)*scale);
            var paper=new Rect(r.N(10)-width/2,r.N(20)-height/2,width,height);
            layout.Viewports.Add(new DxfViewportWindow(r.S(5),paper,transform));
            Point[] corners=[paper.BottomLeft,paper.BottomRight,paper.TopRight,paper.TopLeft];
            layout.Entities.Add(new DxfEntity("VIEWPORT",r.S(8,"0"),r.S(5),DxfPrimitives.Outline([corners])){IsAnnotation=true});
            if((flags&65536)!=0&&r.Fields.Any(f=>f.Code==340))Note("VIEWPORT non-rectangular clipping is shown as its rectangle");
            if(r.Fields.Any(f=>f.Code==331))Note("VIEWPORT frozen layers are still shown");
        }
        // Shared by INSERT and dimension blocks; a successful lookup pushes the name onto the cycle stack.
        bool Enter(string kind,string name,HashSet<string> stack,out (Record Header,List<Record> Records) block)
        {
            if(name.Length==0||!blocks.TryGetValue(name,out block)){block=default;Skip(kind+" (missing block)");return false;}
            if(((int)block.Header.N(70)&(4|8))!=0){Skip(kind+" (external reference)");return false;}
            if(stack.Count>=32||!stack.Add(name)){Skip(kind+" (recursive or too deep)");return false;}
            return true;
        }
        void Expand(Record record,System.Windows.Media.Matrix parent,string inheritedLayer,string path,HashSet<string> stack,bool annotation)
        {
            var name=record.S(2);
            if(!Enter("INSERT",name,stack,out var block))return;
            try
            {
                var sx=record.N(41,1);var sy=record.N(42,1);
                if(sx==0||sy==0){Skip("INSERT (zero scale)");return;}
                int columns=Math.Max(1,(int)record.N(70,1)),rows=Math.Max(1,(int)record.N(71,1));
                if((long)columns*rows>100000){Skip("INSERT (array limit)");return;}
                var layer=record.S(8,"0");if(layer=="0")layer=inheritedLayer;
                for(int row=0;row<rows;row++)for(int col=0;col<columns;col++)
                {
                    if(++expansionCount>100000){Skip("INSERT (expansion limit)");return;}
                    var transform=System.Windows.Media.Matrix.Identity;
                    transform.Translate(-block.Header.N(10),-block.Header.N(20));
                    transform.Scale(sx,sy);
                    // Array spacing is in the rotated insertion coordinate system, not scaled block units.
                    transform.Translate(col*record.N(44),row*record.N(45));
                    transform.Rotate(record.N(50));transform.Translate(record.N(10),record.N(20));
                    if(record.N(230,1)<0)transform.Append(new(-1,0,0,1,0,0));
                    transform.Append(parent);
                    Process(block.Records,transform,layer,$"{path}{record.S(5,name)}[{row},{col}]/",stack,annotation);
                }
            }
            finally{stack.Remove(name);}
        }
        // The anonymous *D block already holds the dimension graphics in WCS; only group 12 (copied dimensions) moves it.
        void ExpandDimension(Record record,System.Windows.Media.Matrix parent,string inheritedLayer,string path,HashSet<string> stack)
        {
            var name=record.S(2);
            if(!Enter(record.Kind,name,stack,out var block))return;
            try
            {
                if(++expansionCount>100000){Skip("INSERT (expansion limit)");return;}
                var layer=record.S(8,"0");if(layer=="0")layer=inheritedLayer;
                var transform=System.Windows.Media.Matrix.Identity;
                // Like ezdxf: no block base point; group 12 is an OCS offset, mirrored with the content below.
                transform.Translate(record.N(12),record.N(22));
                if(record.N(230,1)<0)transform.Append(new(-1,0,0,1,0,0));
                transform.Append(parent);
                var before=Output().Count;
                Process(block.Records,transform,layer,$"{path}{record.S(5,name)}/",stack,true);
                if(Output().Count==before)Skip(record.Kind+" (empty block)");
            }
            finally{stack.Remove(name);}
        }
        void Process(List<Record> records,System.Windows.Media.Matrix matrix,string inheritedLayer,string path,HashSet<string> stack,bool annotation=false)
        {
            List<(Point P,double Bulge)>? vertices=null;
            string polyLayer="0",polyHandle="";bool polyClosed=false;
            void Add(DxfEntity entity)
            {
                if(doc.Entities.Count+doc.Layouts.Sum(l=>l.Entities.Count)>=1000000)throw new InvalidDataException("Expanded drawing exceeds one million entities.");
                var layer=entity.Layer=="0"?inheritedLayer:entity.Layer;
                try { Output().Add(DxfPrimitives.Transform(entity,matrix,layer,path+entity.Handle,annotation)); }
                catch(InvalidDataException){Skip(entity.Kind+" (transformed geometry limit)");}
            }
            foreach(var record in records)
            {
                // Polyline VERTEX/SEQEND records continue the space of their POLYLINE header.
                if(entitiesPass&&stack.Count==0&&record.Kind is not ("VERTEX" or "SEQEND"))target=record.N(67)!=0?active:null;
                var p=(Value:record.Kind,Code:0);var fields=record.Fields;
                string S(int c,string fallback="")=>record.S(c,fallback);
                double N(int c,double fallback=0)=>record.N(c,fallback);
                Point P(int x,int y)=>new(N(x),N(y));
                // OCS parallel to XY is supported, including a reversed normal. General 3D is intentionally excluded.
                if(record.Kind is "HATCH" or "POINT" or "SOLID" or "TEXT" or "INSERT" or "ATTDEF" or "ATTRIB" or "CIRCLE" or "ARC" or "LWPOLYLINE" or "DIMENSION" or "ARC_DIMENSION" or "LARGE_RADIAL_DIMENSION")
                {
                    if(Math.Abs(N(210))>1e-9||Math.Abs(N(220))>1e-9||Math.Abs(Math.Abs(N(230,1))-1)>1e-9)
                    {Skip(record.Kind+" (3D extrusion)");continue;}
                }
                var originalMatrix=matrix;
                if(record.Kind is "HATCH" or "SOLID" or "TEXT" or "ATTDEF" or "ATTRIB" or "CIRCLE" or "ARC" or "LWPOLYLINE" && N(230,1)<0)
                {var mirror=new System.Windows.Media.Matrix(-1,0,0,1,0,0);mirror.Append(matrix);matrix=mirror;}
                var curves = new List<Curve>();
                List<Point[]> fill=[];string? text=null;
                switch (p.Value)
                {
                    case "SPLINE":
                        try
                        {
                            double[] Values(int code) => fields.Where(f => f.Code == code)
                                .Select(f => double.Parse(f.Value, CultureInfo.InvariantCulture)).ToArray();
                            var xs=Values(10);var ys=Values(20);var zs=Values(30);
                            var knots=Values(40);var weights=Values(41);
                            if (xs.Length!=ys.Length || (zs.Length!=0 && (zs.Length!=xs.Length ||
                                zs.Any(z => !double.IsFinite(z) || Math.Abs(z-zs[0])>1e-9))) ||
                                N(72)!=knots.Length || N(73)!=xs.Length)
                                throw new InvalidDataException("Inconsistent or nonplanar spline data.");
                            curves=SplineGeometry.Flatten((int)N(71),xs.Select((x,i)=>new Point(x,ys[i])).ToArray(),knots,
                                weights.Length==0 ? null : weights);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
                        {
                            doc.Skipped["SPLINE (invalid or unsupported)"] = doc.Skipped.GetValueOrDefault("SPLINE (invalid or unsupported)")+1;
                        }
                        break;
                    case "MTEXT":
                        try
                        {
                            var notes=new HashSet<string>();
                            var layout=DxfMText.Read(fields.Select(f=>(f.Code,f.Value)).ToList(),S(8,"0"),S(5),note=>notes.Add(note));
                            if(layout.Entity.Curves.Count>0)Add(layout.Entity);
                            foreach(var note in notes)doc.RenderingWarnings[note]=doc.RenderingWarnings.GetValueOrDefault(note)+1;
                        }
                        catch(NotSupportedException ex){Skip(ex.Message);}
                        catch(Exception ex) when(ex is InvalidDataException or FormatException or OverflowException or ArgumentException)
                        {Skip("MTEXT (invalid or unsupported)");}
                        break;
                    case "MULTILEADER":
                        try
                        {
                            var notes=new HashSet<string>();
                            Add(DxfMLeader.Read(fields.Select(f=>(f.Code,f.Value)).ToList(),S(8,"0"),S(5),note=>notes.Add(note)));
                            foreach(var note in notes)doc.RenderingWarnings[note]=doc.RenderingWarnings.GetValueOrDefault(note)+1;
                        }
                        catch(NotSupportedException ex){Skip(ex.Message);}
                        catch(Exception ex) when(ex is InvalidDataException or FormatException or OverflowException or ArgumentException)
                        {Skip("MULTILEADER (invalid or unsupported)");}
                        break;
                    case "HATCH":
                        try { Add(DxfHatch.Read(fields.Select(f=>(f.Code,f.Value)).ToList(),S(8,"0"),S(5))); }
                        catch(NotSupportedException ex){Skip(ex.Message);}
                        catch(Exception ex) when(ex is InvalidDataException or FormatException or OverflowException or ArgumentException)
                        {Skip("HATCH (invalid or unsupported boundary/pattern)");}
                        break;
                    // Dimension blocks use POINT only for non-plotting definition points.
                    case "POINT": if(annotation)break; curves.Add(Curve.Line(P(10,20),P(10,20)));break;
                    case "SOLID":
                        var corners=new[]{P(10,20),P(11,21),fields.Any(f=>f.Code==13)?P(13,23):P(12,22),P(12,22)};
                        fill.Add(corners.Distinct().ToArray());curves=DxfPrimitives.Outline(fill);break;
                    case "TEXT":
                        if(((int)N(71)&~6)!=0){Skip("TEXT (unsupported flags)");break;}
                        try
                        {
                            text=S(1);
                            fill=DxfPrimitives.Text(text,N(40),N(41,1),N(51),(int)N(71),(int)N(72),(int)N(73),P(10,20),P(11,21),N(50));
                            curves=DxfPrimitives.Outline(fill);
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidDataException){Skip("TEXT (invalid or unsupported)");}
                        break;
                    case "ATTDEF":
                        // Only constant visible definitions are drawn; variable values are ATTRIB records.
                        if(((int)N(70)&3)==2)
                        {
                            fill=DxfPrimitives.Text(S(1),N(40),N(41,1),N(51),(int)N(71),(int)N(72),(int)N(74),P(10,20),P(11,21),N(50));
                            curves=DxfPrimitives.Outline(fill);text=S(1);
                        }
                        break;
                    case "ATTRIB":
                        // Attribute values follow their INSERT in the insert's own coordinate system; invisible ones are not drawn.
                        if(((int)N(70)&1)!=0)break;
                        try
                        {
                            text=S(1);
                            fill=DxfPrimitives.Text(text,N(40),N(41,1),N(51),(int)N(71),(int)N(72),(int)N(74),P(10,20),P(11,21),N(50));
                            curves=DxfPrimitives.Outline(fill);
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidDataException){Skip("ATTRIB (invalid or unsupported)");}
                        break;
                    case "INSERT":
                        Expand(record,matrix,inheritedLayer,path,stack,annotation);break;
                    case "VIEWPORT":
                        if(stack.Count!=0||target==null){Skip("VIEWPORT (outside paper space)");break;}
                        ReadViewport(record,target);break;
                    case "DIMENSION": case "ARC_DIMENSION": case "LARGE_RADIAL_DIMENSION":
                        ExpandDimension(record,matrix,inheritedLayer,path,stack);break;
                    case "LINE": curves.Add(Curve.Line(P(10, 20), P(11, 21))); break;
                    case "RAY": case "XLINE":
                        // Base point and unit direction are WCS vectors, not OCS; only XY directions are drawable.
                        var direction = P(11, 21) - new Point();
                        if (Math.Abs(N(31)) > 1e-9) { Skip(p.Value + " (3D direction)"); break; }
                        if (!(direction.Length > 1e-12)) { Skip(p.Value + " (invalid direction)"); break; }
                        curves.Add(p.Value == "RAY" ? Curve.Ray(P(10, 20), direction) : Curve.XLine(P(10, 20), direction)); break;
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
                            if (cs.Count > 0) Add(new DxfEntity("POLYLINE", polyLayer, polyHandle, cs));
                            vertices = null;
                        }
                        break;
                    case "SECTION": case "ENDSEC": break;
                    default: doc.Skipped[p.Value] = doc.Skipped.GetValueOrDefault(p.Value) + 1; break;
                }
                if (curves.Count > 0) Add(new DxfEntity(p.Value,S(8,"0"),S(5),curves){FillContours=fill,Text=text,IsApproximate=p.Value is "SPLINE" or "TEXT" or "ATTDEF" or "ATTRIB"});

                matrix=originalMatrix;
            }
        }
        Process(entities,System.Windows.Media.Matrix.Identity,"0","",new(StringComparer.OrdinalIgnoreCase));
        entitiesPass=false;
        foreach(var (layout,_,block) in layouts)
        {
            if(layout==active||!blocks.TryGetValue(block,out var content))continue;
            target=layout;
            Process(content.Records,System.Windows.Media.Matrix.Identity,"0","",new(StringComparer.OrdinalIgnoreCase));
        }
        target=null;
        // CAD programs save default layouts (Layout1, Layout2) even when unused. Keep a layout only if it adds
        // something beyond model space: its own paper-space content, or several viewports arranging the model.
        doc.Layouts.RemoveAll(l=>!l.Entities.Any(e=>e.Kind!="VIEWPORT")&&l.Viewports.Count<2);
        var all=doc.Entities.Concat(doc.Layouts.SelectMany(l=>l.Entities)).ToList();
        // Fills are independent and Triangulate is pure; the renderer still concatenates them in entity order.
        Parallel.ForEach(all.Where(e=>e.FillContours.Count>0),e=>e.FillTriangles=DxfPrimitives.Triangulate(e.FillContours));
        if(all.Count==0)throw new InvalidDataException("No supported geometry was found. Supported: points, lines, rays, construction lines, arcs, circles, 2D polylines, splines, solids, text, attributes, dimensions, multileaders and block inserts. Skipped: "+string.Join(", ",doc.Skipped.Select(p=>$"{p.Key} ({p.Value})")));
        return doc;
    }
    private sealed record Record(string Kind,List<Pair> Fields)
    {
        public string S(int code,string fallback="")=>Fields.FirstOrDefault(p=>p.Code==code)?.Value??fallback;
        public double N(int code,double fallback=0)=>double.TryParse(S(code),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:fallback;
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


