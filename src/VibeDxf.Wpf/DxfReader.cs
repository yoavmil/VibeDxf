using System.Globalization;
using System.IO;
using System.Windows;
namespace VibeDxf.Wpf;
internal static class DxfReader
{
    private sealed record Pair(int Code, string Value);
    public static DxfDocument Load(string path)
    {
        using var source = File.OpenRead(path);
        return Load(source);
    }
    internal static DxfDocument Load(Stream source, bool leaveOpen=true)
    {
        using var reader = new StreamReader(source, System.Text.Encoding.UTF8, true, 1024, leaveOpen);
        var pairs = new List<Pair>();
        string? code;
        while ((code = reader.ReadLine()) != null)
        { LoadingContext.Check();
            var value = reader.ReadLine() ?? throw new InvalidDataException("Incomplete DXF group pair.");
            if (!int.TryParse(code.Trim(), out var n)) throw new InvalidDataException("Only ASCII DXF files are supported in this initial viewer.");
            pairs.Add(new Pair(n, n is 1 or 3 ? value : value.Trim()));
        }
        var doc = new DrawingBuilder();
        for (int j = 0; j + 1 < pairs.Count; j++)
            if (pairs[j].Code == 9 && pairs[j].Value == "$INSUNITS")
                doc.Units = pairs[j + 1].Value switch { "1" => "inches", "2" => "feet", "4" => "mm", "5" => "cm", "6" => "m", "0" => "drawing units (unitless)", var u => $"drawing units (INSUNITS={u})" };
        bool metric=pairs.Zip(pairs.Skip(1)).Any(p=>p.First.Code==9&&p.First.Value=="$MEASUREMENT"&&p.Second.Value=="1");
        var blocks=new Dictionary<string,(Record Header,List<Record> Records)>(StringComparer.OrdinalIgnoreCase);
        var blockNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var dimStyles=new Dictionary<string,Record>(StringComparer.OrdinalIgnoreCase);
        var layoutRecords=new List<Record>();var layerRecords=new List<Record>();
        var entities=new List<Record>();string section="";List<Record>? blockRecords=null;
        var solidData=new Dictionary<string,IReadOnlyList<(int Code,string Value)>?>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<pairs.Count;)
        { LoadingContext.Check();
            if(pairs[i].Code!=0){i++;continue;}
            int end=i+1;while(end<pairs.Count&&pairs[end].Code!=0){if((end&1023)==0)LoadingContext.Check();end++;}
            var record=new Record(pairs[i].Value,pairs.GetRange(i+1,end-i-1));i=end;
            if(record.Kind=="SECTION"){section=record.S(2);continue;}
            if(record.Kind=="ENDSEC"){section="";continue;}
            if(section=="ENTITIES")entities.Add(record);
            else if(section=="TABLES"&&record.Kind=="BLOCK_RECORD")blockNames[record.S(5)]=record.S(2);
            else if(section=="TABLES"&&record.Kind=="DIMSTYLE")dimStyles[record.S(2)]=record;
            else if(section=="TABLES"&&record.Kind=="LAYER")layerRecords.Add(record);
            else if(section=="OBJECTS"&&record.Kind=="LAYOUT")layoutRecords.Add(record);
            else if(section=="ACDSDATA"&&record.Kind=="ACDSRECORD")
            {
                string subsection="",owner="";List<(int Code,string Value)> binary=[];
                foreach(var field in record.Fields)
                {
                    if(field.Code==2)subsection=field.Value;
                    else if(subsection=="AcDbDs::ID"&&field.Code==320)owner=field.Value;
                    else if(subsection=="ASM_Data"&&field.Code is 94 or 310)binary.Add((field.Code,field.Value));
                }
                if(owner.Length!=0&&binary.Count!=0)
                {
                    if(!solidData.TryAdd(owner,binary))solidData[owner]=null;
                }
            }
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
        // LAYER table: a negative color means the layer is off, group 70 bit 1 frozen, 290=0 not plotted.
        var layerIndex=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var layerHandles=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        int? Int(Record r,int code)=>r.Fields.Any(f=>f.Code==code)?(int)r.N(code):null;
        foreach(var r in layerRecords)
        { LoadingContext.Check();
            var name=r.S(2);if(name.Length==0||layerIndex.ContainsKey(name))continue;
            var aci=Int(r,62)??7;
            var color=DxfColors.Resolve(Math.Abs(aci)==0?7:Math.Abs(aci),Int(r,420),DxfColors.White,null);
            layerIndex[name]=doc.Layers.Count;layerHandles[r.S(5)]=doc.Layers.Count;
            doc.Layers.Add(new DxfLayer(name,color,aci>=0,((int)r.N(70)&1)!=0,r.S(290,"1")!="0",r.S(6,"Continuous")){ColorIndex=aci,TrueColor=Int(r,420)});
        }
        int LayerOf(string name)
        {
            if(layerIndex.TryGetValue(name,out var index))return index;
            layerIndex[name]=doc.Layers.Count;doc.Layers.Add(new DxfLayer(name,DxfColors.White,true,false,true,"Continuous"));
            return doc.Layers.Count-1;
        }
        // BYLAYER uses the effective layer (block children on layer 0 take the insert's layer); BYBLOCK the insert's color.
        uint ColorOf(Record r,string layer,uint? blockColor)=>DxfColors.Resolve(Int(r,62),Int(r,420),doc.Layers[LayerOf(layer)].Color,blockColor);
        // LAYOUT objects name the sheet tabs and own a *Paper_Space block. The active layout's content is
        // stored in ENTITIES with group 67=1; every other layout's content is its block's records.
        var layouts=new List<(LayoutBuilder Layout,int Tab,string Block)>();
        foreach(var record in layoutRecords)
        {
            var own=record.Fields.SkipWhile(f=>!(f.Code==100&&f.Value=="AcDbLayout")).ToList();
            var block=blockNames.GetValueOrDefault(own.FirstOrDefault(f=>f.Code==330)?.Value??"","");
            if(block.Length==0||block.Equals("*Model_Space",StringComparison.OrdinalIgnoreCase))continue;
            var name=own.FirstOrDefault(f=>f.Code==1)?.Value;
            layouts.Add((new LayoutBuilder(string.IsNullOrWhiteSpace(name)?block:name),(int)new Record("",own).N(71),block));
        }
        layouts.Sort((a,b)=>a.Tab.CompareTo(b.Tab));
        var active=layouts.FirstOrDefault(l=>l.Block.Equals("*Paper_Space",StringComparison.OrdinalIgnoreCase)).Layout;
        // Files without LAYOUT objects (R12) still mark paper space with group 67.
        if(active==null&&entities.Any(r=>r.N(67)!=0)){active=new LayoutBuilder("Layout");layouts.Insert(0,(active,0,"*Paper_Space"));}
        doc.Layouts.AddRange(layouts.Select(l=>l.Layout));
        // Current output: null is model space. Block children follow their insert.
        LayoutBuilder? target=null;bool entitiesPass=true;
        List<DxfEntity> Output()=>target?.Entities??doc.Entities;
        void ReadViewport(Record r,LayoutBuilder layout)
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
            // Group 331 lists layers frozen in this viewport only, by LAYER table handle.
            int[] frozen=[.. r.Fields.Where(f=>f.Code==331).Select(f=>layerHandles.TryGetValue(f.Value,out var i)?i:-1).Where(i=>i>=0).Distinct()];

            Point[] corners=[paper.BottomLeft,paper.BottomRight,paper.TopRight,paper.TopLeft];
            var frame=new DxfEntity("VIEWPORT",r.S(8,"0"),r.S(5),DxfPrimitives.Outline([corners]))
                {Category=EntityCategory.ViewportFrame,Color=ColorOf(r,r.S(8,"0"),null),ColorSource=new(Int(r,62),Int(r,420))};
            layout.Entities.Add(frame);
            layout.Viewports.Add(new DxfViewportWindow(r.S(5),paper,transform){FrozenLayers=frozen,FrameEntity=frame});
            if((flags&65536)!=0&&r.Fields.Any(f=>f.Code==340))Note("VIEWPORT non-rectangular clipping is shown as its rectangle");
        }
        // Shared by INSERT and dimension blocks; a successful lookup pushes the name onto the cycle stack.
        bool Enter(string kind,string name,HashSet<string> stack,out (Record Header,List<Record> Records) block)
        {
            if(name.Length==0||!blocks.TryGetValue(name,out block)){block=default;Skip(kind+" (missing block)");return false;}
            if(((int)block.Header.N(70)&(4|8))!=0){Skip(kind+" (external reference)");return false;}
            if(stack.Count>=32||!stack.Add(name)){Skip(kind+" (recursive or too deep)");return false;}
            return true;
        }
        void Expand(Record record,System.Windows.Media.Matrix parent,string inheritedLayer,string path,HashSet<string> stack,EntityCategory? category,uint color)
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
                    Process(block.Records,transform,layer,$"{path}{record.S(5,name)}[{row},{col}]/",stack,category,color);
                }
            }
            finally{stack.Remove(name);}
        }
        // The anonymous *D block already holds the dimension graphics in WCS; only group 12 (copied dimensions) moves it.
        void ExpandDimension(Record record,System.Windows.Media.Matrix parent,string inheritedLayer,string path,HashSet<string> stack,uint color)
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
                Process(block.Records,transform,layer,$"{path}{record.S(5,name)}/",stack,EntityCategory.Dimension,color);
                if(Output().Count==before)Skip(record.Kind+" (empty block)");
            }
            finally{stack.Remove(name);}
        }
        // category: dimension/leader block content overrides its children's categories.
        // blockColor: the enclosing insert's resolved color, used by BYBLOCK children.
        void Process(List<Record> records,System.Windows.Media.Matrix matrix,string inheritedLayer,string path,HashSet<string> stack,EntityCategory? category=null,uint? blockColor=null)
        {
            List<(Point P,double Bulge)>? vertices=null;
            DxfPolylineProjection? projectedPolyline=null; string? projectedPolylineError=null;
            string polyLayer="0",polyHandle="";bool polyClosed=false;uint polyColor=DxfColors.White;
            uint color=DxfColors.White;
            DxfColorSource colorSource = new(), polyColorSource = new();
            void Add(DxfEntity entity,uint? colorOverride=null,DxfColorSource? sourceOverride=null)
            {
                if(doc.Entities.Count+doc.Layouts.Sum(l=>l.Entities.Count)>=1000000)throw new InvalidDataException("Expanded drawing exceeds one million entities.");
                var layer=entity.Layer=="0"?inheritedLayer:entity.Layer;
                try { entity.ColorSource=sourceOverride??colorSource; Output().Add(DxfPrimitives.Transform(entity,matrix,layer,path+entity.Handle,category,colorOverride??color)); }
                catch(InvalidDataException){Skip(entity.Kind+" (transformed geometry limit)");}
            }
            foreach(var record in records)
        { LoadingContext.Check();
                // Polyline VERTEX/SEQEND records continue the space of their POLYLINE header.
                if(entitiesPass&&stack.Count==0&&record.Kind is not ("VERTEX" or "SEQEND"))target=record.N(67)!=0?active:null;
                colorSource=new(Int(record,62),Int(record,420));
                var p=(Value:record.Kind,Code:0);var fields=record.Fields;
                if(projectedPolyline!=null && record.Kind is not ("VERTEX" or "SEQEND"))
                {Skip(projectedPolyline.Description+" (missing SEQEND)");projectedPolyline=null;projectedPolylineError=null;}
                string S(int c,string fallback="")=>record.S(c,fallback);
                double N(int c,double fallback=0)=>record.N(c,fallback);
                Point P(int x,int y)=>new(N(x),N(y));
                var recordLayer=S(8,"0");if(recordLayer=="0")recordLayer=inheritedLayer;
                color=ColorOf(record,recordLayer,blockColor);
                // OCS parallel to XY is supported, including a reversed normal. General 3D is intentionally excluded.
                if(record.Kind is "HATCH" or "POINT" or "SOLID" or "TEXT" or "INSERT" or "ATTDEF" or "ATTRIB" or "CIRCLE" or "ARC" or "LWPOLYLINE" or "DIMENSION" or "ARC_DIMENSION" or "LARGE_RADIAL_DIMENSION" or "LEADER")
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
                    case "3DSOLID":
                        try
                        {
                            bool hasBinary=solidData.TryGetValue(S(5),out var binary);
                            if(hasBinary&&binary==null)throw new InvalidDataException("Duplicate solid payloads.");
                            curves=DxfSolidProjection.Read(fields.Select(f=>(f.Code,f.Value)).ToArray(),binary);
                            Note("3DSOLID shown as XY boundary wireframe; distances are projected, not 3D");
                        }
                        catch(NotSupportedException ex){Skip(ex.Message);}
                        catch(Exception ex) when(ex is InvalidDataException or EndOfStreamException or FormatException or OverflowException or ArgumentException)
                        {Skip("3DSOLID (invalid ACIS geometry)");}
                        break;
                    case "MESH":
                        try { Add(DxfMeshProjection.Read(fields.Select(f=>(f.Code,f.Value)).ToArray(),S(8,"0"),S(5),Note)); }
                        catch(InvalidDataException) { Skip("MESH (invalid topology)"); }
                        break;
                    // Dimension blocks use POINT only for non-plotting definition points.
                    case "POINT": if(category==EntityCategory.Dimension)break; curves.Add(Curve.Line(P(10,20),P(10,20)));break;
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
                    case "LEADER":
                    {
                        // Follows ezdxf: arrow size DIMASZ·DIMSCALE from the style and its ACAD DSTYLE overrides,
                        // arrow at the first vertex pointing away from the second, optional hook line to the text.
                        var points=new List<Point>();
                        for(int k=0;k+1<fields.Count;k++)
                            if(fields[k].Code==10&&fields[k+1].Code==20&&
                                double.TryParse(fields[k].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var px)&&
                                double.TryParse(fields[k+1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var py)&&
                                double.IsFinite(px)&&double.IsFinite(py))points.Add(new(px,py));
                        if(points.Count<2){Skip("LEADER (too few vertices)");break;}
                        var style=dimStyles.GetValueOrDefault(S(3));var overrides=DimStyleOverrides(fields);
                        double Style(int code,double fallback)=>
                            overrides.TryGetValue(code,out var o)&&double.TryParse(o,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v)?v:
                            style!=null&&style.Fields.Any(f=>f.Code==code)?style.N(code,fallback):fallback;
                        var dimScale=Style(40,1);if(dimScale==0)dimScale=1;
                        var arrowSize=Style(41,metric?2.5:0.1875)*dimScale;
                        if(N(72)==1)Note("LEADER spline paths are shown as straight segments");
                        if(N(73,3)==0&&N(75)==1&&Style(77,1)!=0&&N(41)>0)
                        {
                            var hook=new Vector(N(211,1),N(221));if(N(74)==1)hook=-hook;
                            points.Add(points[^1]+hook*(Style(147,metric?0.625:0.0625)*dimScale+N(41)));
                        }
                        var leaderLines=points.Zip(points.Skip(1)).Where(s=>s.First!=s.Second).Select(s=>Curve.Line(s.First,s.Second)).ToList();
                        List<Point[]> arrowFill=[];
                        if(N(71,1)!=0&&arrowSize>0&&points[0]!=points[1])
                        {
                            var axis=points[0]-points[1];axis.Normalize();
                            var arrowName=blockNames.GetValueOrDefault(overrides.GetValueOrDefault(341)??style?.S(341)??"","");
                            if(arrowName.Length==0)
                            {
                                // Default closed filled arrowhead.
                                var foot=points[0]-axis*arrowSize;var half=new Vector(-axis.Y,axis.X)*arrowSize/6;
                                arrowFill.Add([points[0],foot+half,foot-half]);
                            }
                            else if(Enter("LEADER arrowhead",arrowName,stack,out var arrow))
                            {
                                try
                                {
                                    // Arrow blocks are unit size with the tip at the origin and the body along -X.
                                    var arrowTransform=System.Windows.Media.Matrix.Identity;
                                    arrowTransform.Translate(-arrow.Header.N(10),-arrow.Header.N(20));arrowTransform.Scale(arrowSize,arrowSize);
                                    arrowTransform.Rotate(Math.Atan2(axis.Y,axis.X)*180/Math.PI);arrowTransform.Translate(points[0].X,points[0].Y);
                                    arrowTransform.Append(matrix);
                                    var arrowLayer=S(8,"0");if(arrowLayer=="0")arrowLayer=inheritedLayer;
                                    Process(arrow.Records,arrowTransform,arrowLayer,$"{path}{S(5)}/arrow/",stack,category??EntityCategory.Leader,color);
                                }
                                finally{stack.Remove(arrowName);}
                            }
                        }
                        if(leaderLines.Count>0)Add(new DxfEntity("LEADER",S(8,"0"),S(5),leaderLines){FillContours=arrowFill,Category=EntityCategory.Leader});
                        break;
                    }
                    case "INSERT":
                        Expand(record,matrix,inheritedLayer,path,stack,category,color);break;
                    case "VIEWPORT":
                        if(stack.Count!=0||target==null){Skip("VIEWPORT (outside paper space)");break;}
                        ReadViewport(record,target);break;
                    case "DIMENSION": case "ARC_DIMENSION": case "LARGE_RADIAL_DIMENSION":
                        ExpandDimension(record,matrix,inheritedLayer,path,stack,color);break;
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
                        vertices=null; projectedPolyline=null; projectedPolylineError=null;
                        polyLayer=S(8,"0"); polyHandle=S(5); polyColor=color; polyColorSource=colorSource;
                        if (((int)N(70) & (8 | 16 | 64)) != 0)
                        {
                            try { projectedPolyline=new(fields.Select(f=>(f.Code,f.Value)).ToArray()); }
                            catch(NotSupportedException ex){Skip(ex.Message);}
                            catch(InvalidDataException){Skip("POLYLINE (invalid 3D header)");}
                        }
                        else { vertices=[];polyClosed=((int)N(70)&1)!=0; }
                        break;
                    case "VERTEX":
                        if(projectedPolyline!=null && projectedPolylineError==null)
                        {
                            try { projectedPolyline.AddVertex(fields.Select(f=>(f.Code,f.Value)).ToArray()); }
                            catch(InvalidDataException ex){projectedPolylineError=ex.Message;}
                        }
                        else vertices?.Add((P(10,20),N(42)));
                        break;
                    case "SEQEND":
                        if(projectedPolyline!=null)
                        {
                            try
                            {
                                if(projectedPolylineError!=null)throw new InvalidDataException(projectedPolylineError);
                                Add(projectedPolyline.Finish(polyLayer,polyHandle),polyColor,polyColorSource);
                                Note(projectedPolyline.Description+" displayed as XY wireframe projection; measurements ignore Z");
                            }
                            catch(InvalidDataException ex){Skip(projectedPolyline.Description+" ("+ex.Message+")");}
                            projectedPolyline=null;projectedPolylineError=null;
                        }
                        if (vertices != null)
                        {
                            var cs = Polyline(vertices, polyClosed);
                            if (cs.Count > 0) Add(new DxfEntity("POLYLINE", polyLayer, polyHandle, cs), polyColor, polyColorSource);
                            vertices = null;
                        }
                        break;
                    case "SECTION": case "ENDSEC": break;
                    default: doc.Skipped[p.Value] = doc.Skipped.GetValueOrDefault(p.Value) + 1; break;
                }
                if (curves.Count > 0) Add(new DxfEntity(p.Value,S(8,"0"),S(5),curves){FillContours=fill,Text=text,
                    RenderDots=p.Value=="3DSOLID"?curves.Where(c=>c.Start==c.End).Select(c=>c.Start).Distinct().ToArray():[],
                    IsApproximate=p.Value is "SPLINE" or "TEXT" or "ATTDEF" or "ATTRIB"});

                matrix=originalMatrix;
            }
            if(projectedPolyline!=null)Skip(projectedPolyline.Description+" (missing SEQEND)");
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
        // One group per distinct layer + category: the renderer's visibility table and the panel toggles index it.
        var groups=new Dictionary<(int,EntityCategory),int>();
        foreach(var entity in all)
        {
            var key=(LayerOf(entity.Layer),entity.Category);
            if(!groups.TryGetValue(key,out var group)){group=groups[key]=doc.Groups.Count;doc.Groups.Add(key);}
            entity.Group=group;
        }
        // Fills are independent and Triangulate is pure; the renderer still concatenates them in entity order.
        Parallel.ForEach(all.Where(e=>e.FillContours.Count>0),new ParallelOptions{CancellationToken=LoadingContext.Token},e=>e.FillTriangles=Array.AsReadOnly(DxfPrimitives.Triangulate(e.FillContours).ToArray()));
        if(all.Count==0)throw new InvalidDataException("No supported geometry was found. Supported: points, lines, rays, construction lines, arcs, circles, 2D polylines, splines, solids, text, attributes, dimensions, leaders, multileaders and block inserts. Skipped: "+string.Join(", ",doc.Skipped.Select(p=>$"{p.Key} ({p.Value})")));
        return doc.Freeze();
    }
    private sealed record Record(string Kind,List<Pair> Fields)
    {
        public string S(int code,string fallback="")=>Fields.FirstOrDefault(p=>p.Code==code)?.Value??fallback;
        public double N(int code,double fallback=0)=>double.TryParse(S(code),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:fallback;
    }
    // ACAD DSTYLE XDATA: 1070 dimension-variable code followed by its value (1040/1070/1000/1005).
    private static Dictionary<int,string> DimStyleOverrides(List<Pair> fields)
    {
        var result=new Dictionary<int,string>();
        int start=fields.FindIndex(f=>f.Code==1000&&f.Value=="DSTYLE");
        if(start<0)return result;
        for(int i=start+1;i+1<fields.Count&&fields[i].Code!=1001;i++)
            if(fields[i].Code==1070&&int.TryParse(fields[i].Value,out var key)){result[key]=fields[i+1].Value;i++;}
        return result;
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
