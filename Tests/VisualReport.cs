using System.Globalization;
using System.IO;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeDxf.Wpf;
using Vortice.Direct3D11;
using Vortice.DXGI;

static class VisualReport
{
 private static readonly List<(string Title,string Html)> drawings=[];
 private static readonly StringBuilder log=new();
 private static void Illustration(string title,string html)
 {
  int index=drawings.FindIndex(d=>d.Title==title);
  if(index>=0)drawings[index]=(title,html);else drawings.Add((title,html));
 }
 public static string DirectoryPath {get;}=FindReportDirectory();
 private static string FindReportDirectory()
 {
  var requested=Environment.GetEnvironmentVariable("VIBEDXF_REPORT_DIRECTORY");
  if(!string.IsNullOrWhiteSpace(requested))return Path.GetFullPath(requested);
  for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir!=null;dir=dir.Parent)
   if(File.Exists(Path.Combine(dir.FullName,"VibeDxf.sln")))return Path.Combine(dir.FullName,"artifacts","test-report");
  return Path.GetFullPath("artifacts/test-report");
 }
 public static void RecordResult(string name,bool passed,string? details=null)
 {
  log.AppendLine((passed?"PASS ":"FAIL ")+name);
  if(details!=null)log.AppendLine(details);
 }
 private static string E(string text)=>WebUtility.HtmlEncode(text);
 private static string N(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
 public static IDisposable Start(bool captureConsole=true)
 {
  Directory.CreateDirectory(DirectoryPath);
  var output=Console.Out;var error=Console.Error;
  drawings.Clear();log.Clear();
  if(captureConsole){Console.SetOut(new Tee(output));Console.SetError(new Tee(error));}
  return new Session(output,error);
 }
 private sealed class Tee(TextWriter original):TextWriter
 {
  public override Encoding Encoding=>original.Encoding;
  public override void WriteLine(string? value){original.WriteLine(value);lock(log)log.AppendLine(value);}
  public override void Write(char value){original.Write(value);lock(log)log.Append(value);}
 }
 private sealed class Session(TextWriter output,TextWriter error):IDisposable
 {
  public void Dispose()
  {
   Console.SetOut(output);Console.SetError(error);
   var checks=log.ToString().Split('\n').Where(l=>l.StartsWith("PASS ")).ToArray();
   var failures=log.ToString().Split('\n').Count(l=>l.StartsWith("FAIL "));
   var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>VibeDxf offline checks</title><style>body{font:15px system-ui;background:#111820;color:#dce5ef;margin:32px}h1{margin-bottom:6px}p{color:#aebdcb}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:16px}article{background:#202c39;padding:16px;border-radius:10px;overflow:hidden}svg,img{width:100%;background:#111820}svg{height:240px}h2{font-size:16px}li{padding:5px;color:#8ee3b7}pre{white-space:pre-wrap;background:#202c39;padding:16px}input{padding:10px;width:320px;margin:15px 0}a{color:#58d7c1}</style>");
   html.Append($"<h1>VibeDxf offline checks</h1><p>{checks.Length} tests passed · {failures} failed · {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p><p>Diagrams illustrate geometry inputs and measurements. Cards labeled GPU frame are actual Direct3D/Direct2D readbacks. The list below includes nonvisual checks too.</p><div class='grid'>");
   foreach(var (title,content) in drawings)html.Append($"<article><h2>{E(title)}</h2>{content}</article>");
   html.Append("</div><h2>All passed checks</h2><input id='filter' placeholder='Filter checks and images…' oninput=\"document.querySelectorAll('li,article').forEach(e=>e.hidden=!e.textContent.toLowerCase().includes(this.value.toLowerCase()))\"><ol>");
   foreach(var check in checks)html.Append($"<li>{E(check.Trim())}</li>");
   html.Append($"</ol><details><summary>Full output / failure details</summary><pre>{E(log.ToString())}</pre></details>");
   File.WriteAllText(Path.Combine(DirectoryPath,"index.html"),html.ToString());
   output.WriteLine($"Visual report: {Path.Combine(DirectoryPath,"index.html")}");
  }
 }
 public static void Drawing(string title,IEnumerable<Curve> first,IEnumerable<Curve>? second=null,ClosestPair? pair=null)
 {
  var a=first.ToArray();var b=second?.ToArray()??[];
  var bounds=a.Concat(b).Aggregate(Rect.Empty,(r,c)=>{r.Union(c.Bounds);return r;});
  if(pair is {} m){bounds.Union(m.A);bounds.Union(m.B);}
  if(bounds.IsEmpty)return;
  double scale=Math.Min(310/Math.Max(bounds.Width,1),170/Math.Max(bounds.Height,1));
  string P(Point p)=>$"{N(180+(p.X-(bounds.Left+bounds.Width/2))*scale)},{N(110-(p.Y-(bounds.Top+bounds.Height/2))*scale)}";
  var svg=new StringBuilder("<svg viewBox='0 0 360 240' role='img'>");
  void Curves(Curve[] curves,string color)
  {
   foreach(var c in curves)
   {
    int steps=c.IsLine?1:96;
    svg.Append($"<polyline fill='none' stroke='{color}' stroke-width='2' points='");
    for(int i=0;i<=steps;i++)svg.Append(P(c.At((double)i/steps))+" ");
    svg.Append("'/>");
   }
  }
  Curves(a,"#ffa500");Curves(b,"#00bfff");
  if(pair is {} measurement)
  {
   svg.Append($"<polyline fill='none' stroke='#00fa9a' stroke-dasharray='4 3' points='{P(measurement.A)} {P(measurement.B)}'/>");
   foreach(var p in new[]{measurement.A,measurement.B})
   {
    var xy=P(p).Split(',');svg.Append($"<circle cx='{xy[0]}' cy='{xy[1]}' r='3' fill='#00fa9a'/>");
   }
   svg.Append($"<text x='10' y='228' fill='#dce5ef' font-size='12'>Distance {N(measurement.Distance)} · ΔX {N(measurement.B.X-measurement.A.X)} · ΔY {N(measurement.B.Y-measurement.A.Y)}</text>");
  }
  svg.Append("</svg>");Illustration(title,svg.ToString());
 }
 public sealed record Frame(int Width,int Height,byte[] Pixels)
 {
  public bool Near(int x,int y,Func<byte,byte,byte,bool> predicate)
  {
   for(int j=Math.Max(0,y-4);j<=Math.Min(Height-1,y+4);j++)
    for(int i=Math.Max(0,x-4);i<=Math.Min(Width-1,x+4);i++)
    {int at=(j*Width+i)*4;if(predicate(Pixels[at+2],Pixels[at+1],Pixels[at]))return true;}
   return false;
  }
 }
 public static Frame Capture(DrawingRenderer renderer,string title,Action render)
 {
  Frame? frame=null;
  const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
  object Field(string name)=>renderer.GetType().GetField(name,flags)!.GetValue(renderer)!;
  renderer.FrameRendered=()=>
  {
   var device=(ID3D11Device)Field("device");var context=(ID3D11DeviceContext)Field("context");
   var swap=(IDXGISwapChain1)Field("swapChain");
   using var source=swap.GetBuffer<ID3D11Texture2D>(0);
   var desc=source.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;
   desc.CPUAccessFlags=CpuAccessFlags.Read;desc.MiscFlags=ResourceOptionFlags.None;
   using var staging=device.CreateTexture2D(desc);context.CopyResource(staging,source);
   var map=context.Map(staging,0,MapMode.Read);
   try
   {
    int w=(int)desc.Width,h=(int)desc.Height;var pixels=new byte[w*h*4];
    for(int y=0;y<h;y++)Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,pixels,y*w*4,w*4);
    frame=new Frame(w,h,pixels);
    var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);
    int existing=drawings.FindIndex(d=>d.Title==$"GPU frame · {title}");
    string filename=$"frame-{(existing>=0?existing:drawings.Count):000}.png";
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
    using(var file=File.Create(Path.Combine(DirectoryPath,filename)))encoder.Save(file);
    Illustration($"GPU frame · {title}",$"<img alt='{E(title)}' src='{filename}'>");
   }
   finally{context.Unmap(staging,0);}
  };
  try{render();return frame??throw new Exception("No GPU frame captured.");}
  finally{renderer.FrameRendered=null;}
 }
}
