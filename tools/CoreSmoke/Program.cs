using VibeDxf.Core;
using VibeDxf.Core.Geometry;
var pair=Curve.Distance(Curve.Line(new(0,0),new(10,0)),Curve.Line(new(0,3),new(10,3)));
if(Math.Abs(pair.Distance-3)>1e-12)throw new Exception("Distance failed.");
foreach(var path in args)
{
 var loaded=await DxfReader.LoadAsync(path);var doc=loaded.Document;
 Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {File=Path.GetFileName(path),Model=doc.Entities.Count,
  Deferred=doc.Entities.Count(e=>e.DeferredText.Count>0),Skipped=doc.Skipped,Layouts=doc.Layouts.Select(l=>new {l.Name,Entities=l.Entities.Count,Viewports=l.Viewports.Count})}));
}
Console.WriteLine("Core smoke passed: plain .NET host; distance = "+pair.Distance);
