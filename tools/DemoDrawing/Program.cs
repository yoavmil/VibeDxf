using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeDxf.Wpf;

internal static class Program
{
 static void Await(Task task)
 {
  var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();
  _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
  if(!task.IsCompleted)Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
 }
 [STAThread]
 static void Main()
 {
  CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
  var root=Path.GetFullPath(".");var folder=Path.Combine(root,"samples");Directory.CreateDirectory(folder);
  Environment.SetEnvironmentVariable("VIBEDXF_REPORT_DIRECTORY",Path.Combine(root,"artifacts","demo-preview"));
  var output=new List<string>();int handle=0x100;
  void Pair(int code,object value){output.Add(code.ToString());output.Add(Convert.ToString(value,CultureInfo.InvariantCulture)!);}
  void Entity(string kind,string layer){Pair(0,kind);Pair(5,(handle++).ToString("X"));Pair(8,layer);}
  void PointCodes(Point p){Pair(10,p.X);Pair(20,p.Y);Pair(30,0);}
  void Line(Point a,Point b,string layer){Entity("LINE",layer);PointCodes(a);Pair(11,b.X);Pair(21,b.Y);Pair(31,0);}
  void Spline(Point[] ps,int degree,double[] knots,string layer)
  {
   Entity("SPLINE",layer);Pair(70,8);Pair(71,degree);Pair(72,knots.Length);Pair(73,ps.Length);Pair(74,0);
   foreach(var k in knots)Pair(40,k);foreach(var p in ps)PointCodes(p);
  }
  void Bezier(Point[] ps,string layer)
  {
   int degree=ps.Length-1;Spline(ps,degree,Enumerable.Repeat(0d,degree+1).Concat(Enumerable.Repeat(1d,degree+1)).ToArray(),layer);
  }
  Pair(0,"SECTION");Pair(2,"HEADER");Pair(9,"$ACADVER");Pair(1,"AC1015");Pair(9,"$INSUNITS");Pair(70,4);Pair(0,"ENDSEC");
  Pair(0,"SECTION");Pair(2,"ENTITIES");
  var face=new GlyphTypeface(new Uri("C:/Windows/Fonts/ARIALBLACKITALIC.TTF"));
  var family=new FontFamily(face.FamilyNames.Values.First());
  var typeface=new Typeface(family,FontStyles.Italic,FontWeights.Black,FontStretches.Normal);
  double x=0;Point innerCenter=default;double innerRadius=0;string selectedHandle="";
  foreach(char letter in "VibeDxf")
  {
   var text=new FormattedText(letter.ToString(),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,100,Brushes.White,1);
   var path=text.BuildGeometry(new Point()).GetOutlinedPathGeometry();
   // Apply an explicit drafting slant as well as requesting the italic face;
   // WPF can otherwise resolve Arial Black to an upright installed face.
   Point World(Point p)
   {
    double height=text.Baseline-p.Y;
    return new(x+p.X+.28*height,height);
   }
   var figures=path.Figures.OrderByDescending(f=>{var r=new PathGeometry([f]).Bounds;return r.Width*r.Height;}).ToArray();
   for(int index=0;index<figures.Length;index++)
   {
    var figure=figures[index];var layer=$"WORDMARK_{letter}";
    if(letter=='b' && index==1)
    {
     var box=new PathGeometry([figure]).Bounds;
     innerCenter=World(new Point(box.X+box.Width/2,box.Y+box.Height/2));
     innerRadius=Math.Min(box.Width,box.Height)*.47;
     // One complete circle makes the selected counter clear in the landing-page image.
     selectedHandle=handle.ToString("X");
     Entity("CIRCLE","MEASURE_B_INNER_CIRCLE");PointCodes(innerCenter);Pair(40,innerRadius);
     continue;
    }
    var current=World(figure.StartPoint);var start=current;
    foreach(var segment in figure.Segments)
    {
     switch(segment)
     {
      case LineSegment l:Line(current,World(l.Point),layer);current=World(l.Point);break;
      case PolyLineSegment ls:foreach(var p in ls.Points){Line(current,World(p),layer);current=World(p);}break;
      case BezierSegment b:Bezier([current,World(b.Point1),World(b.Point2),World(b.Point3)],layer);current=World(b.Point3);break;
      case PolyBezierSegment bs:for(int i=0;i<bs.Points.Count;i+=3){Bezier([current,World(bs.Points[i]),World(bs.Points[i+1]),World(bs.Points[i+2])],layer);current=World(bs.Points[i+2]);}break;
      case QuadraticBezierSegment q:Bezier([current,World(q.Point1),World(q.Point2)],layer);current=World(q.Point2);break;
      case PolyQuadraticBezierSegment qs:for(int i=0;i<qs.Points.Count;i+=2){Bezier([current,World(qs.Points[i]),World(qs.Points[i+1])],layer);current=World(qs.Points[i+1]);}break;
      default:throw new InvalidOperationException($"Unexpected font segment {segment.GetType()}");
     }
    }
    if(figure.IsClosed && (current-start).Length>1e-8)Line(current,start,layer);
   }
   x+=text.WidthIncludingTrailingWhitespace+1.5;
  }
  if(innerRadius==0)throw new Exception("No b counter found.");
  const int waves=8;double left=10,right=x-10,y=-17,amplitude=3.8,step=(right-left)/waves;
  var underline=new List<Point>{new(left,y)};
  for(int i=0;i<waves;i++)
  {
   double from=left+i*step,sign=i%2==0?1:-1;
   underline.AddRange([new(from+step/3,y+sign*amplitude*4/3),new(from+step*2/3,y+sign*amplitude*4/3),new(from+step,y)]);
  }
  var underlineKnots=new List<double>{0,0,0,0};for(int i=1;i<waves;i++)underlineKnots.AddRange([i,i,i]);underlineKnots.AddRange([waves,waves,waves,waves]);
  var underlineHandle=handle.ToString("X");Spline(underline.ToArray(),3,underlineKnots.ToArray(),"MEASURE_WAVY_UNDERLINE");
  Pair(0,"ENDSEC");Pair(0,"EOF");
  var dxf=Path.Combine(folder,"VibeDxf-demo.dxf");File.WriteAllLines(dxf,output);
  var load=DxfDocument.LoadAsync(dxf);Await(load);var doc=load.Result.Document;if(doc.Skipped.Count!=0)throw new Exception("Demo includes unsupported geometry.");
  var arc=doc.Entities.Single(e=>e.Handle==selectedHandle);var wave=doc.Entities.Single(e=>e.Handle==underlineHandle);
  var measurement=doc.Measure(new(wave.Id),new(arc.Id),MeasurementStrategy.Closest,MeasurementStrategy.Center);
  using var view=new DxfView();
  var window=new Window{Content=view,Width=1500,Height=500,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,
   WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  object? Call(string name,params object[] args)=>typeof(DxfViewport).GetMethod(name,flags)!.Invoke(view.Child,args);
  try
  {
   window.Show();Await(view.SetDocumentAsync(doc));Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
   view.Select(new(wave.Id));view.Select(new(arc.Id));
   view.SecondMeasurementStrategy=MeasurementStrategy.Center;
   if(view.Selected.Count!=2 || view.Selected[0].Entity!=wave || view.Selected[1].Entity!=arc || view.Measurement?.Pair?.B!=innerCenter)throw new Exception("Demo targets could not be selected.");
   var host=typeof(DxfViewport).GetField("host",flags)!.GetValue(view.Child)!;
   var renderer=(DrawingRenderer)host.GetType().GetProperty("Renderer")!.GetValue(host)!;
   Directory.CreateDirectory(VisualReport.DirectoryPath);
   VisualReport.Capture(renderer,"VibeDxf demo",()=>Call("RenderNow"));
   File.Copy(Path.Combine(VisualReport.DirectoryPath,"frame-000.png"),Path.Combine(folder,"VibeDxf-demo-preview.png"),true);
  }
  finally{window.Close();}
  File.WriteAllText(Path.Combine(folder,"README.md"),$"# VibeDxf demo drawing\n\nOpen `VibeDxf-demo.dxf` in the viewer and press F to fit. Click the wavy underline (layer MEASURE_WAVY_UNDERLINE, handle {underlineHandle}) first, then the circle inside **b** (layer MEASURE_B_INNER_CIRCLE, handle {selectedHandle}). Set the first strategy to Closest and the second to Center. The preview was rendered by the actual viewer with those two elements selected.\n\nThe letters are outlined geometry based on Arial Black Italic, so no font installation is needed to view the DXF. The b counter is one true CIRCLE entity; the underline is one cubic SPLINE. Units: mm. Initial spline-to-circle-center distance: approximately {measurement.Distance:0.######} mm.\n\nRegenerate from the repository root on Windows with Arial Black Italic installed:\n\n```powershell\ndotnet run --project tools/DemoDrawing/DemoDrawing.csproj -c Release\n```\n");
  Console.WriteLine($"Created {dxf}: {doc.Entities.Count} entities; distance {measurement.Distance:0.######} mm; font {family.Source}.");
 }
}
