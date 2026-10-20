using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexDXF;

static class ViewportChecks
{
 public static int Run()
 {
  var count=0;
  void Verify(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  void Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
  object Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target)!;
  var a=new DxfEntity("LINE","0","1",[Curve.Line(new Point(0,0),new Point(10,0))]);
  var b=new DxfEntity("LINE","0","2",[Curve.Line(new Point(0,10),new Point(10,10))]);
  var doc=new DxfDocument();doc.Entities.AddRange([a,b]);
  var view=new DxfViewport();view.Measure(new Size(900,600));view.Arrange(new Rect(0,0,900,600));view.SetDocument(doc);view.UpdateLayout();
  var baseVisual=(DrawingVisual)Field(view,"baseVisual");
  var pen=((GeometryDrawing)baseVisual.Drawing.Children.Single()).Pen;
  Call(view,"SelectAt",new Point(5.01,.01),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==new Point(5,0),"viewport snaps to exact midpoint");
  Call(view,"SelectAt",new Point(0,10),true);
  Verify(view.Measurement is {Distance:10} m && m.A==new Point(5,0) && m.B==new Point(5,10),"viewport midpoint to element measurement");
  Call(view,"SelectAt",new Point(5,10),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==new Point(5,10),"third target starts a new selection pair");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Measurement is { } mm && mm.A==new Point(5,10) && mm.B==new Point(5,0),"viewport midpoint pair preserves selection order");
  var worldPoint=new Point(5,0);
  Call(view,"SetHoveredMidpoint",new MeasurementTarget(a,0));
  Verify(((DrawingVisual)Field(view,"snapVisual")).Drawing.Children.Count>0,"midpoint hover marker is rendered");
  var bmp=new RenderTargetBitmap(900,600,96,96,PixelFormats.Pbgra32);view.UpdateLayout();bmp.Render(view);
  Verify(bmp.PixelWidth==900,"midpoint overlays render without error");
  view.ClearSelection();
  Verify(view.Selected.Count==0 && view.Measurement==null && ((DrawingVisual)Field(view,"snapVisual")).Drawing==null,"clear removes measurements and snap marker");
  Call(view,"SelectAt",new Point(5,0),false);
  Verify(view.Selected.Single().Midpoint==null && view.Selected[0].Entity==a,"whole-element click bypasses midpoint snap");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Selected.Count==2 && view.Measurement is {Distance:0},"whole line and its midpoint can be selected together");
  Call(view,"SelectAt",new Point(5,0),true);
  Verify(view.Selected.Count==1 && view.Selected[0].Midpoint==null,"clicking midpoint again deselects only midpoint");
  Verify(ReferenceEquals(pen,((GeometryDrawing)baseVisual.Drawing.Children.Single()).Pen),"midpoint hover and selection retain base drawing");
  return count;
 }
}

