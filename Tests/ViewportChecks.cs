using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf;

static class ViewportChecks
{
 public static List<CheckCase> BuildCases(string? splineSample=null)
 {
  var cases=new List<CheckCase>();
  void Verify(bool ok,string name) => cases.Add(new(name,ok));
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
  object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
  void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
  var a=new DxfEntity("LINE","0","1",[Curve.Line(new Point(0,0),new Point(10,0))]);
  var b=new DxfEntity("LINE","0","2",[Curve.Line(new Point(0,10),new Point(10,10))]);
  var arc=new DxfEntity("CIRCLE","0","3",[Curve.Arc(new Point(5,5),2,0,Math.Tau)]);
  var doc=new DxfDocument();doc.Entities.AddRange([a,b,arc]);
  // A real off-screen window, so the swap chain host creates its HWND and the Direct3D renderer.
  var view=new DxfViewport();
  var window=new Window{Content=view,Width=900,Height=600,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,
   WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
  try
  {
  window.Show();view.SetDocument(doc);Pump();
  var renderer=(DrawingRenderer?)Field(Field(view,"host")!,"<Renderer>k__BackingField");
  Verify(renderer!=null,"viewport creates the Direct3D renderer");
  var lines=Field(renderer!,"lineBuffer");var arcs=Field(renderer!,"arcBuffer");
  Verify(lines!=null && arcs!=null,"lines and arcs are uploaded to GPU buffers");
  Call(view,"SelectAt",new Point(5.01,.01),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==new Point(5,0),"viewport snaps to exact midpoint");
  Call(view,"SelectAt",new Point(0,10),true);
  Verify(view.Measurement is {Distance:10} m && m.A==new Point(5,0) && m.B==new Point(5,10),"viewport midpoint to element measurement");
  Call(view,"SelectAt",new Point(5,10),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==new Point(5,10),"third target starts a new selection pair");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Measurement is { } mm && mm.A==new Point(5,10) && mm.B==new Point(5,0),"viewport midpoint pair preserves selection order");
  Call(view,"SetHoveredMidpoint",new MeasurementTarget(a,0));
  Verify(Equals(Field(view,"hoveredMidpoint"),new MeasurementTarget(a,0)),"midpoint hover is tracked");
  Call(view,"RenderNow");
  Verify(true,"midpoint overlays and measurement render without error");
  view.ClearSelection();
  Verify(view.Selected.Count==0 && view.Measurement==null && Field(view,"hoveredMidpoint")==null,"clear removes measurements and snap marker");
  Call(view,"SelectAt",new Point(5,0),false);
  Verify(view.Selected.Single().Midpoint==null && view.Selected[0].Entity==a,"whole-element click bypasses midpoint snap");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Selected.Count==2 && view.Measurement is {Distance:0},"whole line and its midpoint can be selected together");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==null,"clicking midpoint again deselects only midpoint");
  Call(view,"SelectAt",new Point(7,5),false);
  Call(view,"RenderNow");
  Verify(view.Selected.Count==2 && view.Selected[1].Entity==arc,"selected arc outline renders without error");
  Verify(view.FirstStrategy==MeasurementStrategy.Closest && view.SecondStrategy==MeasurementStrategy.Closest &&
   Math.Abs(view.Measurement!.Value.Distance-3)<1e-8,"both measurement strategies default to closest");
  var firstSelection=view.Selected[0];var secondSelection=view.Selected[1];var strategyEvents=0;
  view.SelectionChanged+=()=>strategyEvents++;
  view.SecondStrategy=MeasurementStrategy.Center;
  Verify(view.Measurement is {Distance:5} centered && centered.A==new Point(5,0) && centered.B==new Point(5,5) &&
   strategyEvents==1 && ReferenceEquals(firstSelection,view.Selected[0]) && ReferenceEquals(secondSelection,view.Selected[1]),
   "changing second strategy immediately recalculates and notifies without reselecting");
  Call(view,"RenderNow");
  view.FirstStrategy=MeasurementStrategy.Center;
  Verify(view.Measurement!.Value.Distance==5,"first strategy does not turn a line into a center");
  view.SecondStrategy=MeasurementStrategy.Closest;
  view.FirstStrategy=MeasurementStrategy.Closest;
  Verify(Math.Abs(view.Measurement!.Value.Distance-3)<1e-8,"switching back restores closest distance");
  // Check the actual Direct2D highlight path against analytic curve points. A full
  // circle alone cannot detect a reversed sweep, so include partial arcs of both signs.
  var fixture=DxfReader.Load(System.IO.Path.Combine(AppContext.BaseDirectory,"Fixtures","arc-highlight.dxf"));
  var fixtureArc=fixture.Entities.Single(e=>e.Kind=="ARC").Curves.Single();
  foreach(var curve in new[]{fixtureArc,
   Curve.Arc(new Point(5,5),2,.3,Math.PI/2),
   Curve.Arc(new Point(5,5),2,.3,-Math.PI/2),
   Curve.Arc(new Point(5,5),2,.3,Math.PI*1.5),
   Curve.Arc(new Point(5,5),2,.3,-Math.PI*1.5),
   Curve.Arc(new Point(5,5),2,.3,Math.Tau),
   Curve.Arc(new Point(5,5),2,.3,-Math.Tau)})
  {
   using var highlight=(Vortice.Direct2D1.ID2D1PathGeometry)Call(view,"BuildScreenGeometry",(object)new[]{curve})!;
   bool followsCurve=new[]{.125,.5,.875}.All(t=>
   {
    var screen=(Point)Call(view,"Screen",curve.At(t))!;
    return highlight.StrokeContainsPoint(new System.Numerics.Vector2((float)screen.X,(float)screen.Y),3);
   });
   Verify(followsCurve,$"arc highlight follows curve (sweep {curve.Sweep:0.###})");
  }
  var offsetField=view.GetType().GetField("offset",flags)!;
  offsetField.SetValue(view,(Vector)offsetField.GetValue(view)!+new Vector(40,-25));
  view.GetType().GetField("scale",flags)!.SetValue(view,1e4);
  Call(view,"RenderNow");
  Verify(ReferenceEquals(lines,Field(renderer!,"lineBuffer")) && ReferenceEquals(arcs,Field(renderer!,"arcBuffer")),
   "hover, selection, pan and zoom reuse the uploaded GPU buffers");
  view.SetDocument(fixture);Pump();
  Call(view,"SelectAt",fixtureArc.At(.5),false);
  Call(view,"RenderNow");
  Verify(view.Selected.Count==1 && view.Selected[0].Entity.Kind=="ARC",
   "reproduction DXF arc can be selected and highlighted");
  var circles=new DxfDocument();
  circles.Entities.AddRange([new DxfEntity("CIRCLE","0","A",[Curve.Arc(new Point(0,0),2,0,Math.Tau)]),
   new DxfEntity("CIRCLE","0","B",[Curve.Arc(new Point(10,0),3,0,Math.Tau)])]);
  view.SetDocument(circles);Pump();
  Call(view,"SelectAt",new Point(2,0),false);Call(view,"SelectAt",new Point(7,0),false);
  Verify(Math.Abs(view.Measurement!.Value.Distance-5)<1e-8,"circle pair starts with closest distance");
  view.FirstStrategy=MeasurementStrategy.Center;
  Verify(Math.Abs(view.Measurement!.Value.Distance-7)<1e-8,"first circle strategy updates independently");
  view.SecondStrategy=MeasurementStrategy.Center;
  Verify(view.Measurement is {Distance:10} centers && centers.A==new Point(0,0) && centers.B==new Point(10,0),
   "second circle strategy updates to center-to-center without clicks");
  view.FirstStrategy=MeasurementStrategy.Closest;
  Verify(Math.Abs(view.Measurement!.Value.Distance-8)<1e-8,"mixed closest-to-center recalculates immediately");
  view.ClearSelection();view.SecondStrategy=MeasurementStrategy.Closest;
  Verify(view.Measurement==null && view.Selected.Count==0,"changing strategy with no selections is safe");
  var splineCurves=SplineGeometry.Flatten(3,[new(0,0),new(0,2),new(2,-2),new(2,0)],[0,0,0,0,1,1,1,1]);
  var spline=new DxfEntity("SPLINE","0","S",splineCurves);
  var reference=new DxfEntity("LINE","0","R",[Curve.Line(new(-1,3),new(3,3))]);
  var splineDoc=new DxfDocument();splineDoc.Entities.AddRange([spline,reference]);
  view.SetDocument(splineDoc);Pump();
  var frame=VisualReport.Capture(renderer!,"Unselected cubic spline",()=>Call(view,"RenderNow"));
  var probe=new Point(.3125,.5625); // Cubic at parameter .25.
  bool Pixel(VisualReport.Frame captured,Point world,Func<byte,byte,byte,bool> color)
  {
   var screen=(Point)Call(view,"Screen",world)!;
   return captured.Near((int)(screen.X*captured.Width/view.ActualWidth),
    (int)(screen.Y*captured.Height/view.ActualHeight),color);
  }
  Verify(Pixel(frame,probe,(r,g,b)=>r>120 && g>120 && b>120),"spline is visible in actual GPU frame");
  Call(view,"SelectAt",probe,true);
  Verify(view.Selected.Count==1 && view.Selected[0].Entity==spline && view.Selected[0].Midpoint==null,
   "clicking a spline selects the whole curve without artificial midpoint snapping");
  frame=VisualReport.Capture(renderer!,"Selected cubic spline",()=>Call(view,"RenderNow"));
  Verify(Pixel(frame,probe,(r,g,b)=>r>200 && g>90 && g<220 && b<70),"spline highlight follows the rendered curve in GPU frame");
  Call(view,"SelectAt",new Point(3,3),false);
  Verify(view.Measurement is {} sm && Math.Abs(sm.Distance-(3-1/Math.Sqrt(3)))<SplineGeometry.Tolerance*2,
   "selected curved spline to line distance matches analytic cubic maximum");
  frame=VisualReport.Capture(renderer!,"Spline to line distance",()=>Call(view,"RenderNow"));
  Verify(Pixel(frame,view.Measurement!.Value.A,(r,g,b)=>r<80 && g>190 && b>90),
   "spline measurement connector reaches the closest point in GPU frame");
  var splineBuffer=Field(renderer!,"lineBuffer");
  view.FirstStrategy=MeasurementStrategy.Center;Call(view,"RenderNow");
  offsetField.SetValue(view,(Vector)offsetField.GetValue(view)!+new Vector(20,-10));Call(view,"RenderNow");
  Verify(ReferenceEquals(splineBuffer,Field(renderer!,"lineBuffer")),"spline selection, strategies and pan reuse GPU geometry");
  if(splineSample!=null)
  {
   var sampleDoc=DxfReader.Load(splineSample);view.SetDocument(sampleDoc);Pump();
   var expectedVertices=sampleDoc.Entities.Where(e=>e.Kind!="POINT").SelectMany(e=>e.RenderCurves??e.Curves).Count(c=>c.IsLine&&!c.IsUnbounded)*2;
   Verify((int)Field(renderer!,"lineVertexCount")! == expectedVertices,
    "public drawing: all supported line and spline segments are uploaded to GPU");
   var expectedRays=sampleDoc.Entities.SelectMany(e=>e.RenderCurves??e.Curves).Count(c=>c.IsUnbounded);
   Verify((int)Field(renderer!,"rayInstanceCount")! == expectedRays,"public drawing: all rays and xlines are uploaded to GPU");
   var expectedArcs=sampleDoc.Entities.SelectMany(e=>e.RenderCurves??e.Curves).Count(c=>!c.IsLine);
   var buckets=(System.Collections.IEnumerable)Field(renderer!,"buckets")!;
   var uploadedArcs=buckets.Cast<object>().Sum(b=>(int)b.GetType().GetProperty("Count")!.GetValue(b)!);
   Verify(uploadedArcs==expectedArcs,"public drawing: all supported arcs are uploaded to GPU");
   VisualReport.Capture(renderer!,System.IO.Path.GetFileName(splineSample)+" (supported geometry)",()=>Call(view,"RenderNow"));
  }
  window.Close();Pump();
  Verify(Field(Field(view,"host")!,"<Renderer>k__BackingField")==null,"closing the window disposes the renderer");
  return cases;
  }
  finally { window.Close();Pump(); }
 }
}
