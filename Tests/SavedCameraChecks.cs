using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class SavedCameraChecks
{
    private const string Model="0\nLINE\n5\nL\n10\n0\n20\n0\n30\n0\n11\n0\n21\n0\n31\n4\n";
    private static string Vport(string name="*ACTIVE",string direction="-1\n26\n-1\n36\n1",string extra="")=>"0\nVPORT\n2\n"+name+"\n16\n"+direction+"\n40\n20\n41\n1\n"+extra;
    private static DxfDocument Load(string cameras,string model=Model,string header="")
    {
        string data="0\nSECTION\n2\nHEADER\n"+header+"0\nENDSEC\n0\nSECTION\n2\nTABLES\n"+cameras+"0\nENDSEC\n0\nSECTION\n2\nENTITIES\n"+model+"0\nENDSEC\n0\nEOF\n";
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(data));return DxfReader.Load(stream);
    }
    private static void Await(Task task)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();
        _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
        if(!task.IsCompleted)Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
    [Fact]
    public void ActivePriorityTiledAndNamedDefinitions()=>TestSupport.Run("Saved cameras first ACTIVE priority, tiled definitions and named VIEW fields",()=>
    {
        var doc=Load(Vport("Other")+Vport()+Vport(direction:"0\n26\n0\n36\n-1")+"0\nVIEW\n2\nNamed front\n11\n0\n21\n-1\n31\n0\n12\n1\n22\n2\n32\n3\n10\n4\n20\n5\n40\n10\n41\n30\n50\n90\n");
        Assert.Equal(4,doc.ModelViews.Count);Assert.Equal(doc.ModelViews[1].Id,doc.DefaultModelView);
        Assert.Equal(new System.Windows.Media.Media3D.Vector3D(0,0,-1),doc.ModelViews[2].Direction);
        var named=doc.ModelViews[3];Assert.Equal(DxfModelViewSource.NamedView,named.Source);Assert.Equal("Named front",named.Name);
        Assert.Equal(new Point(4,5),named.Center);TestSupport.Near(3,named.AspectRatio);TestSupport.Near(90,named.Twist);
        Assert.Equal(new System.Windows.Media.Media3D.Point3D(1,2,3),named.Target);Assert.All(doc.ModelViews,v=>Assert.True(v.IsSupported));
    });
    [Fact]
    public void LegacyHeaderAndActivePrecedence()=>TestSupport.Run("Saved cameras legacy header fallback and VPORT precedence",()=>
    {
        const string header="9\n$VIEWDIR\n10\n0\n20\n-1\n30\n0\n9\n$VIEWSIZE\n40\n25\n9\n$VIEWCTR\n10\n3\n20\n4\n";
        var doc=Load("",header:header);Assert.Equal(DxfModelViewSource.Header,Assert.Single(doc.ModelViews).Source);
        Assert.Equal(new Point(3,4),doc.ModelViews[0].Center);TestSupport.Near(25,doc.ModelViews[0].Height);
        doc=Load(Vport(),header:header);Assert.Single(doc.ModelViews);Assert.Equal(DxfModelViewSource.Viewport,doc.ModelViews[0].Source);
    });
    [Fact]
    public void InvalidCameraFallsBackWithoutDroppingGeometry()=>TestSupport.Run("Saved invalid camera falls back to XY without dropping drawing entities",()=>
    {
        var doc=Load(Vport(direction:"0\n26\n0\n36\n0"));Assert.Empty(doc.ModelViews);Assert.Null(doc.DefaultModelView);Assert.Single(doc.Entities);Assert.Empty(doc.Skipped);
        Assert.Contains(doc.RenderingWarnings.Keys,k=>k.Contains("invalid parameters"));
    });
    [Fact]
    public void ActualLayoutSurvivesPreparation()=>TestSupport.Run("Saved model camera preparation preserves existing public isometric layout",()=>
    {
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","api-cw750-details.dxf"));
        Assert.True(doc.Layouts.Single(l=>l.Name=="Sheet 4").Viewports.Any(v=>v.Handle=="786B"),doc.UnsupportedWarning);
        Assert.Empty(doc.Skipped);Assert.NotNull(doc.DefaultModelView);Assert.True(doc.ModelViews[doc.DefaultModelView!.Value.Value].IsSupported,doc.UnsupportedWarning);
        Directory.CreateDirectory(VisualReport.DirectoryPath);File.WriteAllText(Path.Combine(VisualReport.DirectoryPath,"camera-source-notes.txt"),doc.UnsupportedWarning??"none");
    });
    [Fact]
    public void PublicControlUsesSavedCameraAndRejectsFalseAnchors()=>TestSupport.Run("Public viewer restores saved model camera and rejects false XY anchors",()=>TestSupport.Sta(()=>
    {
        var doc=Load(Vport());using var view=new DxfView();Await(view.SetDocumentAsync(doc));
        Assert.Equal(doc.DefaultModelView,view.ActiveModelView);
        Assert.Throws<NotSupportedException>(()=>view.WorldToScreen(default));
        Assert.Throws<NotSupportedException>(()=>view.ScreenToWorld(default));
        Assert.Throws<NotSupportedException>(()=>view.Select(new(doc.Entities[0].Id)));
        var foreign=Load(Vport());Assert.Throws<ArgumentException>(()=>view.ActiveModelView=foreign.DefaultModelView);
        view.ActiveModelView=null;Assert.Null(view.ActiveModelView);view.Select(new(doc.Entities[0].Id));Assert.Single(view.Selected);
    }));
    [Fact]
    public void PublicSavedPlanMappingRoundTrips()=>TestSupport.Run("Public saved plan view target and twist retain XY coordinate mapping",()=>TestSupport.Sta(()=>
    {
        var doc=Load(Vport(direction:"0\n26\n0\n36\n1",extra:"17\n10\n27\n20\n51\n90\n"));using var view=new DxfView();Await(view.SetDocumentAsync(doc));
        var point=new Point(12,23);var recovered=view.ScreenToWorld(view.WorldToScreen(point));TestSupport.Near(point.X,recovered.X);TestSupport.Near(point.Y,recovered.Y);
        Assert.Null(doc.ModelViews[0].Window!.ProjectedEntities);
    }));
    [Fact]
    public void SavedDepthPassOccludesRegardlessOfSourceOrder()=>TestSupport.Run("GPU saved camera depth pass occludes farther surfaces",()=>TestSupport.Sta(()=>
    {
        string Solid(double z,int color,string handle)=>$"0\nSOLID\n5\n{handle}\n62\n{color}\n10\n-2\n20\n-2\n30\n{z}\n11\n2\n21\n-2\n31\n{z}\n12\n0\n22\n2\n32\n{z}\n";
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var view=new DxfView();var window=new Window{Content=view,Width=800,Height=600,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        try
        {
            window.Show();
            foreach(bool reverse in new[]{false,true})
            {
                string near=Solid(-5,3,"NEAR"),far=Solid(0,1,"FAR");var doc=Load(Vport(direction:"0\n26\n0\n36\n-10"),reverse?near+far:far+near);Await(view.SetDocumentAsync(doc));view.FitToDrawing();
                Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var engine=(DxfViewport)view.Child;var host=typeof(DxfViewport).GetField("host",flags)!.GetValue(engine)!;var renderer=(DrawingRenderer)host.GetType().GetProperty("Renderer")!.GetValue(host)!;
                var frame=VisualReport.Capture(renderer,"Saved camera near green surface occludes red, source order reversed="+reverse,()=>typeof(DxfViewport).GetMethod("RenderNow",flags)!.Invoke(engine,null));
                var pixel=new Point(view.Camera.Offset.X,view.Camera.Offset.Y);Assert.True(frame.Near((int)(pixel.X*frame.Width/view.ActualWidth),(int)(pixel.Y*frame.Height/view.ActualHeight),(r,g,b)=>g>r+30&&g>b+30));
                var buffer=typeof(DrawingRenderer).GetField("lineBuffer",flags)!.GetValue(renderer);view.ActiveModelView=null;view.RestoreSavedModelView();Assert.Same(buffer,typeof(DrawingRenderer).GetField("lineBuffer",flags)!.GetValue(renderer));
            }
        }
        finally{window.Close();}
    }));
    [Fact]
    public void PerspectivePipelineAndDepthClipping()=>TestSupport.Run("GPU public perspective camera with front/back clipping and depth foreshortening",()=>TestSupport.Sta(()=>
    {
        string Line(string handle,double x,double z,int color)=>$"0\nLINE\n5\n{handle}\n62\n{color}\n10\n{x}\n20\n-1\n30\n{z}\n11\n{x}\n21\n1\n31\n{z}\n";
        var doc=Load(Vport(direction:"0\n26\n0\n36\n10",extra:"71\n23\n43\n6\n44\n-1\n"),Line("FAR",-1,0,1)+Line("NEAR",1,5,3)+Line("CLIPPED",0,8,5)+Line("HIDDEN",0,0,7)+"0\nSOLID\n5\nOCCLUDER\n62\n3\n10\n-0.25\n20\n-0.25\n30\n5\n11\n0.25\n21\n-0.25\n31\n5\n12\n0\n22\n0.25\n32\n5\n");
        Assert.True(Assert.Single(doc.ModelViews).Perspective);Assert.Empty(doc.ModelViews[0].Window!.ProjectedEntities!.Single(e=>e.Handle=="CLIPPED").Curves);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var view=new DxfView();var window=new Window{Content=view,Width=900,Height=700,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        try
        {
            window.Show();Await(view.SetDocumentAsync(doc));Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Assert.Equal(doc.DefaultModelView,view.ActiveModelView);Assert.Throws<NotSupportedException>(()=>view.WorldToScreen(default));
            var engine=(DxfViewport)view.Child;var host=typeof(DxfViewport).GetField("host",flags)!.GetValue(engine)!;var renderer=(DrawingRenderer)host.GetType().GetProperty("Renderer")!.GetValue(host)!;
            var frame=VisualReport.Capture(renderer,"Perspective: far red segment, larger near green segment; blue segment clipped beyond front plane",()=>typeof(DxfViewport).GetMethod("RenderNow",flags)!.Invoke(engine,null));
            bool At(double x,double y,Func<byte,byte,byte,bool> predicate)
            {
                var c=view.Camera;double px=x*c.Scale+c.Offset.X,py=-y*c.Scale+c.Offset.Y;
                return frame.Near((int)(px*frame.Width/view.ActualWidth),(int)(py*frame.Height/view.ActualHeight),predicate);
            }
            double lens=20*50*Math.Sqrt(2)/42;
            Assert.True(At(-lens/10,0,(r,g,b)=>r>g+30&&r>b+30));Assert.True(At(lens/5,lens/10+1,(r,g,b)=>g>r+30&&g>b+30));
            Assert.False(At(-lens/10,lens/10+1,(r,g,b)=>r>100||g>100||b>100));Assert.True(At(0,0,(r,g,b)=>g>r+30&&g>b+30));Assert.True(At(0,2,(r,g,b)=>r>150&&g>150&&b>150));
        }
        finally{window.Close();}
    }));
    [Fact]
    public void ActualCompactSavedCameraAndTorus()=>TestSupport.Run("GPU actual compact solids saved POV and torus perspective camera",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var view=new DxfView();var window=new Window{Content=view,Width=1200,Height=800,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
        void Capture(string title)
        {
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);var engine=(DxfViewport)view.Child;
            var host=typeof(DxfViewport).GetField("host",flags)!.GetValue(engine)!;var renderer=(DrawingRenderer)host.GetType().GetProperty("Renderer")!.GetValue(host)!;
            VisualReport.Capture(renderer,title,()=>typeof(DxfViewport).GetMethod("RenderNow",flags)!.Invoke(engine,null));
        }
        try
        {
            window.Show();var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","3dsolids.dxf"));
            Assert.Equal(4,doc.Entities.Count);Assert.Empty(doc.Skipped);Assert.NotNull(doc.DefaultModelView);Assert.True(doc.ModelViews[doc.DefaultModelView!.Value.Value].IsSupported,doc.UnsupportedWarning);
            Assert.Contains(doc.Entities.SelectMany(e=>e.SpatialGeometry!.Lines),l=>Math.Abs(l.End.Z-l.Start.Z)>1);
            Await(view.SetDocumentAsync(doc));Assert.Equal(doc.DefaultModelView,view.ActiveModelView);Capture("Actual 3dsolids.dxf saved oblique model camera");
            view.ActiveModelView=null;view.FitToDrawing();Capture("Actual 3dsolids.dxf original XY measurement view");
            var torus=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","torus_r2000.dxf"));Assert.Empty(torus.Skipped);Await(view.SetDocumentAsync(torus));view.FitToDrawing();Capture("Actual torus_r2000.dxf retained XYZ surface in saved camera");
        }
        finally{window.Close();}
    }));
    [Fact]
    public void SpatialLimitsPreserveAnalyticDrawing()=>TestSupport.Run("Spatial approximation limits never discard valid analytic XY geometry",()=>
    {
        string model="0\nCIRCLE\n5\nC\n10\n0\n20\n0\n40\n1000000000\n0\nPOINT\n5\nP\n10\n1000000001\n20\n0\n";
        string named="0\nVIEW\n2\nOptional 3D\n11\n1\n21\n-1\n31\n1\n40\n2000000000\n41\n2000000000\n";
        var doc=Load(Vport(direction:"0\n26\n0\n36\n1")+named,model);Assert.Empty(doc.Skipped);Assert.Equal(2,doc.Entities.Count);
        Assert.True(doc.ModelViews[0].IsSupported);Assert.False(doc.ModelViews[1].IsSupported);TestSupport.Near(1000000000,doc.Entities[0].Curves[0].Radius);
        TestSupport.Near(1,doc.Measure(new(doc.Entities[0].Id),new(doc.Entities[1].Id)).Distance!.Value);
    });
}
