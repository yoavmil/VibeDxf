using System.Globalization;
using System.Windows;
using System.Windows.Media;
using C = VibeDxf.Core;
using G = VibeDxf.Core.Geometry;
namespace VibeDxf;

// Temporary projection for the existing WPF renderer. Remove with Stage 3 integration.
internal static class CoreDocumentAdapter
{
    internal static G.Point2D Core(Point p)=>new(p.X,p.Y);
    internal static Point Wpf(G.Point2D p)=>new(p.X,p.Y);
    internal static G.Affine2D Core(Matrix m)=>new(m.M11,m.M12,m.M21,m.M22,m.OffsetX,m.OffsetY);
    internal static Matrix Wpf(G.Affine2D m)=>new(m.M11,m.M12,m.M21,m.M22,m.OffsetX,m.OffsetY);
    internal static Rect Wpf(G.Bounds2D b)=>b.IsEmpty?Rect.Empty:new(b.X,b.Y,b.Width,b.Height);
    internal static C.Curve Core(Curve c)=>new(Core(c.Start),Core(c.End),Core(c.Center),c.Radius,c.Angle,c.Sweep,(C.LineExtent)c.Extent);
    internal static Curve Wpf(C.Curve c)=>new(Wpf(c.Start),Wpf(c.End),Wpf(c.Center),c.Radius,c.Angle,c.Sweep,(LineExtent)c.Extent);
    internal static C.DxfEntity Core(DxfEntity e)=>new(e.Kind,e.Layer,e.Handle,e.Curves.Select(Core).ToList())
    {
        FillContours=e.FillContours.Select(p=>p.Select(Core).ToArray()).ToList(),
        RegionContours=e.RegionContours?.Select(p=>p.Select(Core).ToArray()).ToList(),
        RenderCurves=e.RenderCurves?.Select(Core).ToList(),RenderDots=e.RenderDots.Select(Core).ToList(),
        Text=e.Text,InstancePath=e.InstancePath,IsApproximate=e.IsApproximate,Category=(C.EntityCategory)e.Category,Color=e.Color
    };
    internal static DxfEntity? Wpf(C.DxfEntity e,Action<string>? warn=null,Action<string>? skip=null)
    {
        var curves=e.Curves.Select(Wpf).ToList();
        var fills=e.FillContours.Select(p=>p.Select(Wpf).ToArray()).ToList();
        string? text=e.Text;
        foreach(var descriptor in e.DeferredText)
        {
            try
            {
                DxfEntity shaped;
                if(descriptor.Kind==C.DxfTextKind.Multiline)
                    shaped=DxfMText.Read(descriptor.Tags.ToList(),e.Layer,e.Handle,warn).Entity;
                else
                {
                    var tags=descriptor.Tags;
                    double N(int code,double fallback=0)=>tags.FirstOrDefault(t=>t.Code==code).Value is {} v?double.Parse(v,CultureInfo.InvariantCulture):fallback;
                    Point P(int x,int y)=>new(N(x),N(y));
                    var contours=DxfPrimitives.Text(descriptor.RawText,N(40),N(41,1),N(51),(int)N(71),(int)N(72),
                        (int)N(e.Kind is "ATTRIB" or "ATTDEF"?74:73),P(10,20),P(11,21),N(50));
                    shaped=new(e.Kind,e.Layer,e.Handle,DxfPrimitives.Outline(contours)){FillContours=contours,Text=descriptor.RawText,IsApproximate=true};
                }
                shaped=DxfPrimitives.Transform(shaped,Wpf(descriptor.Transform),e.Layer,e.Handle,(EntityCategory)e.Category,e.Color);
                if(shaped!=null){curves.AddRange(shaped.Curves);fills.AddRange(shaped.FillContours);text=shaped.Text;}
            }
            catch(NotSupportedException ex){skip?.Invoke(ex.Message);}
            catch(Exception ex) when(ex is ArgumentException or System.IO.InvalidDataException or FormatException or OverflowException)
            {skip?.Invoke((descriptor.Kind==C.DxfTextKind.Multiline?"MTEXT":e.Kind)+" (invalid or unsupported)");}
        }
        if(curves.Count==0)return null;
        return new(e.Kind,e.Layer,e.Handle,curves)
        {
            FillContours=fills,RegionContours=e.RegionContours?.Select(p=>p.Select(Wpf).ToArray()).ToList(),
            RenderCurves=e.RenderCurves?.Select(Wpf).ToList(),RenderDots=e.RenderDots.Select(Wpf).ToList(),
            Text=text,InstancePath=e.InstancePath,IsApproximate=e.IsApproximate,Category=(EntityCategory)e.Category,
            Color=DxfColors.ForDarkBackground(e.Color),Group=e.Group
        };
    }
    internal static DxfDocument Load(string path)
    {
        var source=C.DxfReader.Load(path).Document;
        var doc=new DxfDocument{Units=source.Units};
        foreach(var p in source.Skipped)doc.Skipped.Add(p.Key,p.Value);
        foreach(var p in source.RenderingWarnings)doc.RenderingWarnings.Add(p.Key,p.Value);
        var existingNotes=source.RenderingWarnings.Keys.ToHashSet();
        void Skip(string key)=>doc.Skipped[key]=doc.Skipped.GetValueOrDefault(key)+1;
        foreach(var l in source.Layers)doc.Layers.Add(new(l.Name,DxfColors.ForDarkBackground(l.Color),l.OnInFile,l.FrozenInFile,l.Plot,l.Linetype));
        doc.Groups.AddRange(source.Groups.Select(g=>(g.Layer,(EntityCategory)g.Category)));
        void Convert(IEnumerable<C.DxfEntity> from,List<DxfEntity> to)
        {
            foreach(var e in from)
            {
                var notes=new HashSet<string>();var entity=Wpf(e,n=>notes.Add(n),Skip);
                if(entity!=null)to.Add(entity);
                foreach(var note in notes)if(!existingNotes.Contains(note))doc.RenderingWarnings[note]=doc.RenderingWarnings.GetValueOrDefault(note)+1;
            }
        }
        Convert(source.Entities,doc.Entities);
        foreach(var layout in source.Layouts)
        {
            var target=new DxfLayout(layout.Name);
            Convert(layout.Entities,target.Entities);
            target.Viewports.AddRange(layout.Viewports.Select(v=>new DxfViewportWindow(v.Handle,Wpf(v.Paper),Wpf(v.ModelToPaper)){FrozenLayers=v.FrozenLayers.ToArray()}));
            doc.Layouts.Add(target);
        }
        Parallel.ForEach(doc.Entities.Concat(doc.Layouts.SelectMany(l=>l.Entities)).Where(e=>e.FillContours.Count>0),e=>e.FillTriangles=DxfPrimitives.Triangulate(e.FillContours));
        return doc;
    }
}
