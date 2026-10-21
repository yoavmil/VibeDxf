using System.Windows;
using VibeDxf;

static class MeasurementStrategyChecks
{
 public static int Run()
 {
  int count=0;
  void Verify(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
  MeasurementTarget Round(string kind,Point center,double radius,double start=0,double sweep=Math.Tau) =>
   new(new DxfEntity(kind,"0","",[Curve.Arc(center,radius,start,sweep)]));
  var closest=MeasurementStrategy.Closest;var center=MeasurementStrategy.Center;
  void Pair(MeasurementTarget a,MeasurementTarget b,MeasurementStrategy first,MeasurementStrategy second,
   double distance,string name)
  {
   var result=a.Distance(b,first,second);
   VisualReport.Drawing(name,a.Entity.Curves,b.Entity.Curves,result);
   var reverse=b.Distance(a,second,first);
   Verify(Math.Abs(result.Distance-distance)<1e-8 && Math.Abs(reverse.Distance-distance)<1e-8,name);
   Verify((a.FixedPoint(first) is Point p ? (result.A-p).Length : a.Entity.Curves.Min(c=>(c.Closest(result.A)-result.A).Length))<1e-8 &&
    (b.FixedPoint(second) is Point q ? (result.B-q).Length : b.Entity.Curves.Min(c=>(c.Closest(result.B)-result.B).Length))<1e-8,
    name+" endpoints match strategies");
  }
  var a=Round("CIRCLE",new(3,4),1);var b=Round("CIRCLE",new(9,12),2);
  Pair(a,b,closest,closest,7,"circle closest / closest");
  Pair(a,b,center,closest,8,"circle center / closest");
  Pair(a,b,closest,center,9,"circle closest / center");
  Pair(a,b,center,center,10,"circle center / center");
  var delta=a.Distance(b,center,center);
  Verify(delta.B.X-delta.A.X==6 && delta.B.Y-delta.A.Y==8,"center measurement preserves signed dx and dy");
  var reverseDelta=b.Distance(a,center,center);
  Verify(reverseDelta.B.X-reverseDelta.A.X==-6 && reverseDelta.B.Y-reverseDelta.A.Y==-8,"reversed center measurement reverses dx and dy");
  var arcA=Round("ARC",new(0,0),2,Math.PI/2,Math.PI/2);
  var arcB=Round("ARC",new(10,0),3,0,Math.PI/2);
  Pair(arcA,arcB,closest,closest,Math.Sqrt(101),"bounded arc closest / closest");
  Pair(arcA,arcB,center,closest,Math.Sqrt(109),"bounded arc center / closest");
  Pair(arcA,arcB,closest,center,Math.Sqrt(104),"bounded arc closest / center");
  Pair(arcA,arcB,center,center,10,"bounded arc center / center");
  Pair(arcA,Round("CIRCLE",new(10,0),3),center,closest,7,"arc center to circle closest");
  Pair(Round("ARC",new(10,0),3,Math.PI/2,-Math.PI/2),arcA,closest,center,Math.Sqrt(109),"clockwise arc closest to center");
  var concentric=Round("CIRCLE",new(3,4),4);
  Pair(a,concentric,center,center,0,"concentric centers measure zero");
  Pair(a,concentric,center,closest,4,"center inside circle measures to circumference");
  Pair(a,concentric,closest,closest,3,"concentric closest measures radial gap");
  var line=new MeasurementTarget(new DxfEntity("LINE","0","",[Curve.Line(new(8,4),new(8,10))]));
  var circle=Round("CIRCLE",new(0,0),2);
  Pair(line,circle,center,center,Math.Sqrt(80),"center strategy leaves a line as a whole line");
  Pair(line,circle,center,closest,Math.Sqrt(80)-2,"line to circle closest is unchanged");
  var midpoint=new MeasurementTarget(line.Entity,0);
  Pair(midpoint,circle,center,center,Math.Sqrt(113),"explicit midpoint remains fixed with center strategy");
  var poly=new MeasurementTarget(new DxfEntity("LWPOLYLINE","0","",arcA.Entity.Curves));
  Verify(poly.Center==null && poly.Distance(arcB,center,closest)==poly.Distance(arcB,closest,closest),
   "arc-shaped polylines ignore center strategy");
  Verify(a.Describe(center)=="CIRCLE center" && a.Describe(closest)=="CIRCLE" && midpoint.Describe(center)=="LINE midpoint",
   "selection descriptions identify active centers only");
  return count;
 }
}
