using System.Runtime.CompilerServices;
// Existing regression scenarios inspect device resources and construct synthetic drawings.
[assembly: InternalsVisibleTo("VibeDxf.Checks")]
// Demo friendship is limited to GPU capture for the existing preview/report.
[assembly: InternalsVisibleTo("DemoDrawing")]