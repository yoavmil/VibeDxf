using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class DxfCameraProjectionChecks
{
    private static DxfModelView Camera(bool perspective=false,double lens=29.698484809834994,bool front=false,bool back=false,bool atEye=true,double frontZ=0,double backZ=0)=>
        new("test",DxfModelViewSource.NamedView,new(0,0,10),default,default,10,1,perspective:perspective,lensLength:lens,
            frontClipping:front,backClipping:back,frontAtEye:atEye,frontClip:frontZ,backClip:backZ);
    private static DxfEntity Entity(IEnumerable<DxfSpatialLine>? lines=null,IEnumerable<DxfSpatialTriangle>? triangles=null,IEnumerable<Point3D>? dots=null)=>
        new("LINE","0","P",[]){SpatialGeometry=new(lines??[],triangles??[],dots??[])};
    private static void Near(double expected,double value)=>TestSupport.Near(expected,value,1e-8);

    [Fact]
    public void SpatialInputsAreCopied()=>TestSupport.Run("Spatial display snapshots own their XYZ inputs",()=>
    {
        var input=new List<DxfSpatialLine>{new(new(1,2,3),new(4,5,6))};
        var geometry=new DxfSpatialGeometry(input,[],[]);input.Clear();Assert.Single(geometry.Lines);
        Assert.Throws<NotSupportedException>(()=>((IList<DxfSpatialLine>)geometry.Lines).Clear());
    });

    [Fact]
    public void PlanarCurvesLiftToTheirElevation()=>TestSupport.Run("Spatial planar arc flattening keeps elevation and sagitta bound",()=>
    {
        var entity=new DxfEntity("ARC","0","A",[Curve.Arc(default,3,0,Math.PI)]);
        var geometry=DxfSpatialGeometry.FromPlanar(entity,7);Assert.True(geometry.Approximate);Assert.True(geometry.Lines.Count>10);
        Assert.All(geometry.Lines,l=>
        {
            Near(7,l.Start.Z);Near(7,l.End.Z);
            var mid=new Point((l.Start.X+l.End.X)/2,(l.Start.Y+l.End.Y)/2);
            Assert.InRange(3-(mid-new Point()).Length,0,SplineGeometry.Tolerance+1e-12);
        });
    });

    [Fact]
    public void SpatialPlacementIncludesZ()=>TestSupport.Run("Spatial placement preserves line domains and transforms XYZ",()=>
    {
        var source=new DxfSpatialGeometry([new(new(1,2,3),new(2,2,3),LineExtent.Ray)],[],[]);
        var result=source.Transform(new Matrix(2,0,0,-1,10,20),-3,100);var line=Assert.Single(result.Lines);
        Assert.Equal(new Point3D(12,18,91),line.Start);Assert.Equal(new Point3D(14,18,91),line.End);Assert.Equal(LineExtent.Ray,line.Extent);
    });

    [Fact]
    public void PerspectiveUsesEyeDistanceAndLens()=>TestSupport.Run("Perspective camera uses eye distance and calibrated lens magnification",()=>
    {
        var source=Entity(dots:[new(1,0,0),new(1,0,5)]);
        var view=Camera(true);var projected=DxfCameraProjection.Project(source,view).DisplayGeometry3D!;
        Near(1,projected.Dots[0].X);Near(2,projected.Dots[1].X);Assert.True(projected.Dots[1].Z>projected.Dots[0].Z);
        var longer=DxfCameraProjection.Project(source,Camera(true,lens:view.LensLength*2)).DisplayGeometry3D!;
        Near(2,longer.Dots[0].X);Near(4,longer.Dots[1].X);
    });

    [Fact]
    public void EyeCrossingIsClippedBeforeDivision()=>TestSupport.Run("Perspective eye crossing clips without reflected or nonfinite geometry",()=>
    {
        var source=Entity(lines:[new(new(0,0,0),new(0,0,20))],dots:[new(0,0,20)]);
        var geometry=DxfCameraProjection.Project(source,Camera(true)).DisplayGeometry3D!;
        Assert.Empty(geometry.Lines);var marker=Assert.Single(geometry.Dots);
        Near(0,marker.X);Near(0,marker.Y);Assert.InRange(marker.Z,0,1);
    });

    [Fact]
    public void FrustumAndDepthClipIndependentSegments()=>TestSupport.Run("Camera clipping preserves independent line segments and depth planes",()=>
    {
        var source=Entity(lines:[new(new(-10,1,0),new(10,1,0)),new(new(-10,-1,0),new(10,-1,0))],dots:[new(0,0,-3),new(0,0,0),new(0,0,3)]);
        var clipped=DxfCameraProjection.Project(source,Camera(front:true,back:true,atEye:false,frontZ:2,backZ:-2)).DisplayGeometry3D!;
        Assert.Equal(2,clipped.Lines.Count);Assert.Single(clipped.Dots);
        Assert.All(clipped.Lines,l=>{Near(-5,l.Start.X);Near(5,l.End.X);Near(l.Start.Y,l.End.Y);});
        Near(1,clipped.Lines[0].Start.Y);Near(-1,clipped.Lines[1].Start.Y);
    });

    [Fact]
    public void FrontAtEyeIgnoresStoredFrontOffset()=>TestSupport.Run("Camera VIEWMODE front-at-eye ignores FRONTZ offset",()=>
    {
        var source=Entity(dots:[new(0,0,3),new(0,0,11)]);
        Assert.Single(DxfCameraProjection.Project(source,Camera(front:true,atEye:true,frontZ:2)).DisplayGeometry3D!.Dots);
        Assert.Empty(DxfCameraProjection.Project(source,Camera(front:true,atEye:false,frontZ:2)).DisplayGeometry3D!.Dots);
    });

    [Fact]
    public void TriangleCrossingCameraIsClippedAsPolygon()=>TestSupport.Run("Perspective triangles are clipped before division and remain inside frame",()=>
    {
        var source=Entity(triangles:[new(new(-2,-2,0),new(2,-2,0),new(0,2,20))]);
        var result=DxfCameraProjection.Project(source,Camera(true)).DisplayGeometry3D!;
        Assert.NotEmpty(result.Triangles);
        foreach(var p in result.Triangles.SelectMany(t=>new[]{t.A,t.B,t.C}))
        {Assert.InRange(p.X,-5-1e-8,5+1e-8);Assert.InRange(p.Y,-5-1e-8,5+1e-8);Assert.InRange(p.Z,0,1);}
    });

    [Fact]
    public void OrthographicDepthIsSharedAcrossEntities()=>TestSupport.Run("Camera depth normalization is scene-wide, preserving occlusion ordering",()=>
    {
        var far=Entity(lines:[new(new(-1,0,-3),new(1,0,-3))]);var close=Entity(lines:[new(new(-1,0,3),new(1,0,3))]);long work=0;
        var result=DxfCameraProjection.Prepare(Camera(),new[]{far,close},ref work);
        Near(0,result[0].DisplayGeometry3D!.Lines[0].Start.Z);Near(1,result[1].DisplayGeometry3D!.Lines[0].Start.Z);
    });

    [Fact]
    public void PerspectiveRayEndsAtItsVanishingPoint()=>TestSupport.Run("Perspective unbounded rays end at their finite vanishing point",()=>
    {
        var source=Entity(lines:[new(new(1,1,0),new(1,1,-10),LineExtent.Ray)]);
        var line=Assert.Single(DxfCameraProjection.Project(source,Camera(true)).DisplayGeometry3D!.Lines);
        Near(1,line.Start.X);Near(1,line.Start.Y);Near(0,line.End.X);Near(0,line.End.Y);Near(0,line.End.Z);
    });

    [Fact]
    public void ProjectionKeepsSourceMetadata()=>TestSupport.Run("Camera display snapshots preserve source groups and identity metadata",()=>
    {
        var source=new DxfEntity("LINE","metal","A",[]){SpatialGeometry=new([new(new(-1,0,0),new(1,0,0))],[],[]),Group=7,Color=0xff112233,SourceHandle="SOURCE",InstancePath="I/A"};
        var result=DxfCameraProjection.Project(source,Camera());
        Assert.Equal(source.Group,result.Group);Assert.Equal(source.Color,result.Color);Assert.Equal(source.SourceHandle,result.SourceHandle);Assert.Equal(source.InstancePath,result.InstancePath);
    });

    [Fact]
    public void ModelViewsRetainFiniteGeometryOutsideSavedFrame()=>TestSupport.Run("Model camera snapshots retain finite offscreen geometry for later pan and fit",()=>
    {
        var source=Entity(dots:[new(20,0,0)]);long work=0;
        Assert.Empty(DxfCameraProjection.Project(source,Camera()).DisplayGeometry3D!.Dots);
        var result=DxfCameraProjection.Prepare(Camera(),new[]{source},ref work,clipLateral:false);
        Near(20,Assert.Single(result[0].DisplayGeometry3D!.Dots).X);
    });

    [Fact]
    public void SpatialPreparationHonorsCancellation()=>TestSupport.Run("Spatial and camera preparation honor loader cancellation",()=>
    {
        var source=Entity(dots:[default]);using var cancellation=new CancellationTokenSource();cancellation.Cancel();LoadingContext.Token=cancellation.Token;
        try{Assert.Throws<OperationCanceledException>(()=>DxfCameraProjection.Project(source,Camera()));}
        finally{LoadingContext.Token=default;}
        Assert.Throws<InvalidDataException>(()=>new DxfSpatialGeometry([],[],[new(double.NaN,0,0)]));
    });
}
