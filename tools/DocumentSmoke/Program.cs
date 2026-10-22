using System.Windows;
using VibeDxf.Wpf;

if (args.Length == 0) throw new ArgumentException("Provide one or more DXF paths.");
foreach (var path in args)
{
    var result = await DxfDocument.LoadAsync(path);
    var document = result.Document;
    var text = document.Entities.First(e => e.Kind == "MTEXT");
    var other = document.Entities.First(e => e.Id != text.Id);
    var measurement = document.Measure(new(text.Id), new(other.Id));
    if (measurement.Status != MeasurementStatus.Success || text.Curves.Count == 0)
        throw new InvalidOperationException("Text geometry was not usable after loading.");
    Console.WriteLine($"{System.IO.Path.GetFileName(path)}: {document.Entities.Count} model entities, {document.Layouts.Count} layouts, text distance {measurement.Distance:0.######} ({measurement.Status}).");
}
if (Application.Current != null) throw new InvalidOperationException("A WPF Application was created.");
var graphics = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Vortice.")).ToArray();
if (graphics.Length != 0) throw new InvalidOperationException("Document-only loading initialized Vortice assemblies.");
Console.WriteLine("Document-only host: no Application, viewer, HWND or Vortice initialization.");