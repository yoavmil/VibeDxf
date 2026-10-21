using System.Windows;
using System.IO;
using VibeDxf;

static class GeometryChecks
{
 public static List<CheckCase> BuildCases()
 {
var cases = new List<CheckCase>();
void Check(string name, Curve a, Curve b, double expected)
{
    var actual=Curve.Distance(a,b);var reverse=Curve.Distance(b,a);
    VisualReport.Drawing(name,[a],[b],actual);
    cases.Add(new(name,Math.Abs(actual.Distance-expected)<1e-8 && Math.Abs(reverse.Distance-expected)<1e-8 &&
        (a.Closest(actual.A)-actual.A).Length<1e-7 && (b.Closest(actual.B)-actual.B).Length<1e-7));
}
Curve L(double x, double y, double x2, double y2) => Curve.Line(new(x, y), new(x2, y2));
Curve C(double x, double y, double r) => Curve.Arc(new(x,y),r,0,Math.Tau);
Check("parallel lines", L(0,0,10,0), L(0,3,10,3), 3);
Check("crossing lines", L(0,0,10,10), L(0,10,10,0), 0);
Check("collinear overlapping", L(0,0,10,0), L(5,0,15,0), 0);
Check("endpoint diagonal", L(0,0,1,0), L(4,4,5,4), 5);
Check("point entity", L(0,0,0,0), L(3,4,3,4), 5);
Check("line circle", L(-10,5,10,5), C(0,0,2), 3);
Check("line circle intersection", L(-10,0,10,0), C(0,0,2), 0);
Check("line circle tangent", L(-10,2,10,2), C(0,0,2), 0);
Check("inside circle", L(-1,0,1,0), C(0,0,3), 2);
Check("separate circles", C(0,0,2), C(10,0,3), 5);
Check("nested circles", C(0,0,10), C(1,0,2), 7);
Check("concentric circles", C(0,0,10), C(0,0,2), 8);
Check("intersecting circles", C(0,0,3), C(4,0,3), 0);
Check("arc endpoint", Curve.Arc(new(0,0),2,0,Math.PI/2), L(-2,-2,-2,-2), Math.Sqrt(20));
Check("clockwise arc", Curve.Arc(new(0,0),2,0,-Math.PI/2), L(0,-4,0,-4), 2);
void Verify(bool ok, string name) => cases.Add(new(name,ok));
var firstLine = new DxfEntity("LINE", "0", "A", [L(0,0,10,0)]);
var secondLine = new DxfEntity("LINE", "0", "B", [L(8,4,8,10)]);
var firstMid = new MeasurementTarget(firstLine, 0);
var secondMid = new MeasurementTarget(secondLine, 0);
var midpointPair = firstMid.Distance(secondMid);
Verify(midpointPair.A == new Point(5,0) && midpointPair.B == new Point(8,7) && Math.Abs(midpointPair.Distance - Math.Sqrt(58)) < 1e-10, "exact midpoint to midpoint");
var toElement = firstMid.Distance(new MeasurementTarget(secondLine));
Verify(toElement.A == new Point(5,0) && toElement.B == new Point(8,4) && toElement.Distance == 5, "midpoint to closest point on line");
var fromElement = new MeasurementTarget(secondLine).Distance(firstMid);
Verify(fromElement.A == toElement.B && fromElement.B == toElement.A, "element to midpoint preserves signed delta orientation");
var circleEntity = new DxfEntity("CIRCLE", "0", "C", [C(0,0,2)]);
Verify(Math.Abs(firstMid.Distance(new MeasurementTarget(circleEntity)).Distance - 3) < 1e-10, "midpoint to circle");
Verify(firstMid.Distance(new MeasurementTarget(firstLine)).Distance == 0 && firstMid != new MeasurementTarget(firstLine), "midpoint and whole parent are distinct targets");
var polyEntity = new DxfEntity("LWPOLYLINE", "0", "P", [L(20,0,30,0), Curve.Arc(new(30,5),5,-Math.PI/2,Math.PI)]);
var midpointIndex = new MidpointIndex([firstLine,secondLine,circleEntity,polyEntity]);
Verify(midpointIndex.Find(new Point(5.1,0), .2) == firstMid && midpointIndex.Find(new Point(5.3,0), .2) == null, "midpoint snap tolerance");
Verify(midpointIndex.Find(new Point(25,0), .01) == new MeasurementTarget(polyEntity,0) && midpointIndex.Find(new Point(35,5), .1) == null, "straight polyline segments snap; arc segments do not");
Verify(new MidpointIndex([]).Find(new Point(), 10) == null && midpointIndex.Find(new Point(5,0),0) == firstMid, "empty midpoint index and exact lookup");
var fixture = Path.GetTempFileName();
try
{
    // A semicircular polyline bulge and explicit units exercise the ASCII parser.
    File.WriteAllLines(fixture, new[] { "0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "4", "0", "ENDSEC",
        "0", "SECTION", "2", "ENTITIES", "0", "LWPOLYLINE", "5", "A1", "8", "TEST", "70", "0",
        "10", "-1", "20", "0", "42", "1", "10", "1", "20", "0", "0", "ENDSEC", "0", "EOF" });
    var parsed = DxfReader.Load(fixture);
    var entity = parsed.Entities.Single();
    Verify(parsed.Units == "mm" && entity.Layer == "TEST" && entity.Handle == "A1" && parsed.Skipped.Count == 0 &&
        Math.Abs(entity.Curves.Single().Radius-1)<1e-10 && Math.Abs(entity.Curves.Single().Sweep-Math.PI)<1e-10,
        "parser units, metadata and bulge");
}
finally { File.Delete(fixture); }

 return cases;
 }
}

