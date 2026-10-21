using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf;
using Xunit;

[Collection("Visual checks")]
public sealed class MLeaderChecks
{
    private static void PointIs(Point expected,Point actual,double tolerance=1e-7)
    {TestSupport.Near(expected.X,actual.X,tolerance);TestSupport.Near(expected.Y,actual.Y,tolerance);}
    private static List<(int Code,string Value)> Tags(params object[] data)=>Enumerable.Range(0,data.Length/2)
        .Select(i=>((int)data[2*i],Convert.ToString(data[2*i+1],System.Globalization.CultureInfo.InvariantCulture)!)).ToList();
    // Mirrors the CONTEXT_DATA layout written by AutoCAD/ezdxf: context, MTEXT data, leaders, then entity overrides.
    private static List<(int Code,string Value)> MLeader(string text,int alignment,object[][] leaders,params object[] entity)
    {
        var tags=Tags(300,"CONTEXT_DATA{",40,1,10,0,20,0,41,1,140,1,145,.5,290,text.Length>0?1:0,304,text,
            11,0,21,0,31,1,12,20,22,10,13,1,23,0,43,0,171,alignment,296,0,110,0,120,0,111,1,121,0,112,0,122,1);
        foreach(var leader in leaders)tags.AddRange(Tags(leader));
        tags.AddRange(Tags(301,"}"));tags.AddRange(Tags(entity));return tags;
    }
    private static object[] Leader(double lastX,double lastY,double dx,double length,params double[] vertices)
    {
        var data=new List<object>{302,"LEADER{",290,1,291,1,10,lastX,20,lastY,11,dx,21,0,40,length,304,"LEADER_LINE{"};
        for(int i=0;i<vertices.Length;i+=2)data.AddRange([10,vertices[i],20,vertices[i+1]]);
        data.AddRange([305,"}",303,"}"]);return data.ToArray();
    }

    [Fact]
    public void PublicLeaderGeometry()=>TestSupport.Run("MULTILEADER leader, dogleg, arrow and text placement (uncommon.dxf 4A5)",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        var leader=doc.Entities.Single(e=>e.Handle=="4A5");
        Assert.Equal("MULTILEADER",leader.Kind);Assert.True(leader.IsAnnotation);Assert.Equal("LEADER",leader.Text);
        var tip=new Point(84.03099887243659,54.78971325347647);var landing=new Point(87.10686502456198,56.913311978422);
        var arrow=leader.FillContours[0];PointIs(tip,arrow[0]);
        // The leader line starts at the 0.18 arrow base and ends with the 0.36 dogleg.
        var axis=landing-tip;axis.Normalize();
        PointIs(tip+axis*.18,leader.Curves[0].Start);PointIs(landing,leader.Curves[0].End);
        PointIs(landing,leader.Curves[1].Start);PointIs(landing+new Vector(.36,0),leader.Curves[1].End);
        // Left-aligned text attaches at its top-left corner, after the 0.09 landing gap.
        var text=leader.FillContours.Skip(1).SelectMany(p=>p).Aggregate(Rect.Empty,(r,p)=>{r.Union(p);return r;});
        TestSupport.Near(87.55686502456198,text.Left,.03);TestSupport.Near(57.003311978422,text.Bottom,.01);
        Assert.Null(new MidpointIndex(doc.Entities.Where(e=>e.Kind=="MULTILEADER")).Find(leader.Curves[0].At(.5),1));
    });

    [Fact]
    public void SyntheticLeaders()=>TestSupport.Run("MULTILEADER multiple leaders, alignment, invisible leaders, warnings and skips",()=>
    {
        var warnings=new HashSet<string>();
        var two=DxfMLeader.Read(MLeader("NOTE",3,[Leader(20,10,-1,2,10,0),Leader(20,10,-1,2,10,20,15,20)]),"ANNO","M",w=>warnings.Add(w));
        Assert.Empty(warnings);Assert.Equal("ANNO",two.Layer);
        // Two arrows plus text glyph contours; the second leader has an extra vertex.
        PointIs(new(10,0),two.FillContours[0][0]);PointIs(new(10,20),two.FillContours[1][0]);
        // Leader 1: arrow base -> landing -> dogleg. Leader 2: arrow base -> (15,20) -> landing -> dogleg.
        var foot=new Point(10,0)+new Vector(1,1)/Math.Sqrt(2);
        Point[] expected=[foot,new(20,10),new(20,10),new(18,10),new(11,20),new(15,20),new(15,20),new(20,10),new(20,10),new(18,10)];
        for(int i=0;i<5;i++){PointIs(expected[2*i],two.Curves[i].Start);PointIs(expected[2*i+1],two.Curves[i].End);}
        var text=two.FillContours.Skip(2).SelectMany(p=>p).Aggregate(Rect.Empty,(r,p)=>{r.Union(p);return r;});
        // Glyph ink stops at the side bearing, just inside the right-attached text box.
        Assert.InRange(text.Right,19.85,20);TestSupport.Near(10,text.Bottom,.01);

        var hidden=DxfMLeader.Read(MLeader("ONLY TEXT",1,[Leader(20,10,1,2,0,0)],170,0),"0","H");
        Assert.All(hidden.FillContours,p=>Assert.NotEqual(new Point(0,0),p[0]));Assert.Equal("ONLY TEXT",hidden.Text);

        warnings.Clear();
        DxfMLeader.Read(MLeader("S",1,[Leader(20,10,1,2,0,0)],170,2,342,"1A"),"0","S",w=>warnings.Add(w));
        Assert.Contains("MULTILEADER spline leaders are shown as straight segments",warnings);
        Assert.Contains("MULTILEADER custom arrowheads are shown as closed filled arrows",warnings);

        warnings.Clear();
        var block=DxfMLeader.Read(MLeader("",1,[Leader(20,10,1,2,0,0)],172,1),"0","B",w=>warnings.Add(w));
        Assert.Contains("MULTILEADER block content is not displayed",warnings);Assert.Null(block.Text);Assert.NotEmpty(block.Curves);

        // An arrow is drawn only when the first segment is longer than twice its size.
        var shortLeader=DxfMLeader.Read(MLeader("",1,[Leader(1.5,0,1,0,0,0)]),"0","SH");
        Assert.Empty(shortLeader.FillContours);PointIs(new(0,0),shortLeader.Curves[0].Start);

        var tilted=MLeader("T",1,[Leader(20,10,1,2,0,0)]);tilted[tilted.FindIndex(t=>t.Code==121)]=(131,"1");
        Assert.Equal("MULTILEADER (3D orientation)",Assert.Throws<NotSupportedException>(()=>DxfMLeader.Read(tilted,"0","T")).Message);
        Assert.Equal("MULTILEADER (no displayable content)",
            Assert.Throws<NotSupportedException>(()=>DxfMLeader.Read(MLeader("",1,[],170,0),"0","E")).Message);
    });

    [Fact]
    public void LeadersRenderButAreNotSelectable()=>TestSupport.Run("GPU frame: MULTILEADER arrows, leaders and text are drawn but not selectable",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=900,Height=600,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();
            var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
            view.SetDocument(doc);Pump();
            var leader=doc.Entities.Single(e=>e.Handle=="1AFF");Assert.Contains("HEAVY MULLION",leader.Text);
            var box=leader.Bounds;var scale=Math.Min(view.ActualWidth/box.Width,view.ActualHeight/box.Height)*.7;
            view.GetType().GetField("scale",flags)!.SetValue(view,scale);
            var center=new Point(box.X+box.Width/2,box.Y+box.Height/2);
            view.GetType().GetField("offset",flags)!.SetValue(view,new Vector(view.ActualWidth/2-center.X*scale,view.ActualHeight/2+center.Y*scale));
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"MULTILEADER 1AFF: CW750-002 / HEAVY MULLION",()=>Call(view,"RenderNow"));
            Point Screen(Point p)=>(Point)Call(view,"Screen",p)!;
            bool Light(byte r,byte g,byte b)=>r>120&&g>120&&b>120;
            var tip=leader.FillContours[0][0];var s=Screen(tip+(leader.FillContours[0][1]-tip)/2);
            Assert.True(frame.Near((int)(s.X*frame.Width/view.ActualWidth),(int)(s.Y*frame.Height/view.ActualHeight),Light));
            Call(view,"SelectAt",leader.Curves[0].At(.5),false);
            Assert.DoesNotContain(view.Selected,t=>t.Entity.Kind=="MULTILEADER");
        }
        finally{window.Close();Pump();}
    }));
}
