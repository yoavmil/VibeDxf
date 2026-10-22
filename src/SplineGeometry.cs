using System.Windows;
namespace VibeDxf;
public static class SplineGeometry
{
 public const double Tolerance=VibeDxf.Core.SplineGeometry.Tolerance;
 public static List<Curve> Flatten(int degree,IReadOnlyList<Point> points,IReadOnlyList<double> knots,IReadOnlyList<double>? weights=null)=>
   VibeDxf.Core.SplineGeometry.Flatten(degree,points.Select(CoreDocumentAdapter.Core).ToArray(),knots,weights).Select(CoreDocumentAdapter.Wpf).ToList();
}
