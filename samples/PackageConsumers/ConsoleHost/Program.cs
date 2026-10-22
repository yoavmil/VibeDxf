using System.Text.Json;
using System.Windows;
using VibeDxf.Wpf;

var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (paths.Length == 0) throw new ArgumentException("Usage: ConsoleHost drawing.dxf [--smoke]");
foreach (var path in paths)
{
    var loaded = await DxfDocument.LoadAsync(path);
    var document = loaded.Document;
    Console.WriteLine($"{System.IO.Path.GetFileName(path)}: {document.Entities.Count} model entities, {document.Layouts.Count} layouts");
    foreach (var entity in document.AllEntities)
        Console.WriteLine($"  {entity.Kind} | {entity.Layer} | {entity.Handle} | {entity.Id}");

    if (document.Entities.Count >= 2)
    {
        var result = document.Measure(new(document.Entities[0].Id), new(document.Entities[1].Id));
        Console.WriteLine($"Measurement: {result.Status}, distance {result.Distance}, ΔX {result.DeltaX}, ΔY {result.DeltaY}");
    }
    if (args.Contains("--smoke"))
    {
        var first = document.Entities.Single(e => e.Handle == "A");
        var second = document.Entities.Single(e => e.Handle == "B");
        var text = document.Entities.Single(e => e.Handle == "T");
        var pair = document.Measure(new(first.Id), new(second.Id));
        var textPair = document.Measure(new(text.Id), new(first.Id));
        if (document.Entities.Count != 3 || pair.Status != MeasurementStatus.Success || Math.Abs(pair.Distance!.Value - 3) > 1e-9 ||
            text.Curves.Count == 0 || !textPair.IsApproximate || textPair.Status != MeasurementStatus.Success)
            throw new InvalidOperationException("Packed document API smoke check failed.");
        if (Application.Current != null || AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name!.StartsWith("Vortice.")))
            throw new InvalidOperationException("Document-only consumer initialized UI or library graphics.");
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, PackageAssembly = typeof(DxfDocument).Assembly.Location,
            ModelEntities = document.Entities.Count, Distance = pair.Distance, TextApproximate = textPair.IsApproximate, GraphicsInitialized = false }));
    }
}
