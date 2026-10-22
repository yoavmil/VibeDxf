using System.Globalization;
using System.IO;
using System.Windows;
using VibeDxf.Wpf;

static class SplineChecks
{
 public static List<CheckCase> BuildCases()
 {
  var cases=new List<CheckCase>();
  void Verify(bool ok,string name) => cases.Add(new(name,ok));
  var points=new[]{new Point(0,0),new Point(0,2),new Point(2,-2),new Point(2,0)};
  var knots=new double[]{0,0,0,0,1,1,1,1};
  var cubic=SplineGeometry.Flatten(3,points,knots);
  Verify(cubic.Count>2 && cubic[0].Start==points[0] && cubic[^1].End==points[^1],"cubic spline preserves endpoints and S-shaped curvature");
  void Compare(int degree,Point[] ps,double[] ks,double[]? ws,string label)
  {
   var curves=SplineGeometry.Flatten(degree,ps,ks,ws);
   VisualReport.Drawing(label,curves);
   // Independent Cox-de Boor basis evaluation checks the production knot-insertion
   // and rational Bezier subdivision against the original NURBS definition.
   double Basis(int i,int p,double t)
   {
    if(p==0)return ks[i]<=t && t<ks[i+1] ? 1 : 0;
    var d1=ks[i+p]-ks[i];var d2=ks[i+p+1]-ks[i+1];
    return (d1>0 ? (t-ks[i])/d1*Basis(i,p-1,t):0)+(d2>0 ? (ks[i+p+1]-t)/d2*Basis(i+1,p-1,t):0);
   }
   double error=0;
   for(int j=0;j<300;j++)
   {
    double t=ks[degree]+(ks[ps.Length]-ks[degree])*(j+.5)/300;
    double x=0,y=0,w=0;
    for(int i=0;i<ps.Length;i++){var b=Basis(i,degree,t)*(ws?[i]??1);x+=ps[i].X*b;y+=ps[i].Y*b;w+=b;}
    var at=new Point(x/w,y/w);
    error=Math.Max(error,curves.Min(c=>(c.Closest(at)-at).Length));
   }
   Verify(error<=SplineGeometry.Tolerance*1.001,label);
  }
  Compare(3,points,knots,null,"cubic approximation stays within tolerance");
  var quarter=SplineGeometry.Flatten(2,[new(1,0),new(1,1),new(0,1)],[0,0,0,1,1,1],[1,Math.Sqrt(.5),1]);
  Compare(2,[new(1,0),new(1,1),new(0,1)],[0,0,0,1,1,1],[1,Math.Sqrt(.5),1],"rational weighted spline follows quarter circle");
  Verify(Math.Abs(quarter.Min(c=>(c.Closest(new Point(2,2))-new Point(2,2)).Length)-(Math.Sqrt(8)-1))<SplineGeometry.Tolerance,
   "distance to rational spline agrees with analytic circle");
  var inner=new DxfEntity("SPLINE","0","I",quarter);
  var outer=new DxfEntity("SPLINE","0","O",SplineGeometry.Flatten(2,[new(3,0),new(3,3),new(0,3)],
   [0,0,0,1,1,1],[1,Math.Sqrt(.5),1]));
  var pair=inner.Distance(outer);
  Verify(Math.Abs(pair.Distance-2)<=2*SplineGeometry.Tolerance,"curved spline to spline distance");
  VisualReport.Drawing("Spline to spline distance",inner.Curves,outer.Curves,pair);
  var arc=new DxfEntity("ARC","0","A",[Curve.Arc(new(0,0),3,0,Math.PI/2)]);
  pair=inner.Distance(arc);
  Verify(Math.Abs(pair.Distance-2)<=SplineGeometry.Tolerance,"curved spline to arc distance");
  VisualReport.Drawing("Spline to arc distance",inner.Curves,arc.Curves,pair);
  Compare(2,[new(0,0),new(1,3),new(2,-1),new(4,1),new(6,0)],[0,0,0,.2,.7,1,1,1],null,"nonuniform multi-span spline");
  Compare(2,[new(0,0),new(2,0),new(2,2),new(0,2),new(0,0),new(2,0)],
   [0,1,2,3,4,5,6,7,8],null,"periodic unclamped spline");
  var discontinuous=SplineGeometry.Flatten(1,[new(0,0),new(1,0),new(5,0),new(6,0)],[0,0,.5,.5,1,1]);
  Verify(discontinuous.Count==2 && discontinuous.All(c=>c.End.X-c.Start.X==1),"repeated knots do not bridge discontinuities");
  var degenerate=SplineGeometry.Flatten(2,[new(3,4),new(3,4),new(3,4)],[0,0,0,1,1,1]);
  Verify(degenerate.Count==1 && degenerate[0].Start==degenerate[0].End,"degenerate spline is safe");
  bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
  Verify(Reject(()=>SplineGeometry.Flatten(3,points,[0,0,0,1])) &&
   Reject(()=>SplineGeometry.Flatten(3,points,[0,0,0,0,.8,.7,1,1])) &&
   Reject(()=>SplineGeometry.Flatten(3,points,knots,[1,0,1,1])),"malformed knots and unsupported weights are rejected");
  var path=Path.GetTempFileName();
  try
  {
   var pairs=new List<string>{"0","SECTION","2","ENTITIES","0","SPLINE","5","S1","8","CURVES",
    "70","8","71","3","72","8","73","4"};
   foreach(var k in knots){pairs.Add("40");pairs.Add(k.ToString(CultureInfo.InvariantCulture));}
   foreach(var p in points){pairs.AddRange(["10",p.X.ToString(CultureInfo.InvariantCulture),"20",p.Y.ToString(CultureInfo.InvariantCulture),"30","0"]);}
   pairs.AddRange(["0","ENDSEC","0","EOF"]);File.WriteAllLines(path,pairs);
   var doc=DxfReader.Load(path);var spline=doc.Entities.Single();
   Verify(spline.Kind=="SPLINE" && spline.Handle=="S1" && spline.Layer=="CURVES" && doc.Skipped.Count==0,
    "DXF spline parser keeps one selectable entity with metadata");
   Verify(new MidpointIndex([spline]).Find(spline.Curves[0].At(.5),1)==null,
    "spline tessellation does not create artificial midpoint snaps");
   var target=new MeasurementTarget(spline);
   var line=new MeasurementTarget(new DxfEntity("LINE","0","",[Curve.Line(new(-1,3),new(3,3))]));
   Verify(target.Center==null && target.Distance(line)==target.Distance(line,MeasurementStrategy.Center),
    "center strategy does not affect spline measurement");
   pairs.InsertRange(4,["0","SPLINE","71","3","72","0","73","0"]);File.WriteAllLines(path,pairs);
   var mixed=DxfReader.Load(path);
   Verify(mixed.Entities.Count==1 && mixed.Skipped.Values.Sum()==1,"invalid spline does not discard valid drawing geometry");
  }
  finally{File.Delete(path);}
  return cases;
 }
}
