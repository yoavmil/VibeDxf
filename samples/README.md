# VibeDxf demo drawing

Open `VibeDxf-demo.dxf` in the viewer and press F to fit. Click the wavy underline (layer MEASURE_WAVY_UNDERLINE, handle 167) first, then the circle inside **b** (layer MEASURE_B_INNER_CIRCLE, handle 11D). Set the first strategy to Closest and the second to Center. The preview was rendered by the actual viewer with those two elements selected.

The letters are outlined geometry based on Arial Black Italic, so no font installation is needed to view the DXF. The b counter is one true CIRCLE entity; the underline is one cubic SPLINE. Units: mm. Initial spline-to-circle-center distance: approximately 39.874871 mm.

Regenerate from the repository root on Windows with Arial Black Italic installed:

```powershell
dotnet run --project tools/DemoDrawing/DemoDrawing.csproj -c Release
```
