using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VibeDxf;

static class ViewportChecks
{
 public static int Run()
 {
  var count=0;
  void Verify(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
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
  var offsetField=view.GetType().GetField("offset",flags)!;
  offsetField.SetValue(view,(Vector)offsetField.GetValue(view)!+new Vector(40,-25));
  view.GetType().GetField("scale",flags)!.SetValue(view,1e4);
  Call(view,"RenderNow");
  Verify(ReferenceEquals(lines,Field(renderer!,"lineBuffer")) && ReferenceEquals(arcs,Field(renderer!,"arcBuffer")),
   "hover, selection, pan and zoom reuse the uploaded GPU buffers");
  window.Close();Pump();
  Verify(Field(Field(view,"host")!,"<Renderer>k__BackingField")==null,"closing the window disposes the renderer");
  return count;
 }
}
