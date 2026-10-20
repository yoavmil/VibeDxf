# CodexDXF

A dependency-free C# / WPF DXF viewer for Windows, targeting .NET 10.

## Run

Open `CodexDXF.sln` in Visual Studio, or:

```powershell
dotnet run --project .\CodexDXF.csproj -- "C:\Users\Yoav Miller\Downloads\182N-RND_REV-A.DXF"
```

The sample remains at its original location and is not committed to this repository.

## Controls

- File > Open, Ctrl+O, or drop one `.dxf` file to open it.
- Mouse wheel zooms around the pointer.
- Hold the middle or right mouse button and drag to pan.
- F fits the drawing to the window.
- Left click selects an element (orange), then a second element (blue).
- Hover within 8 screen pixels of a straight segment midpoint to show a green triangle. Click to select that exact midpoint. Selected midpoints have orange/blue triangles.
- Midpoints work on LINE entities and straight polyline segments. You can measure midpoint-to-midpoint, midpoint-to-element, or element-to-midpoint. ΔX and ΔY follow the selection order.
- Hold Shift while clicking to select the whole element even when near a midpoint. In dense drawings, zoom in to distinguish nearby midpoints.
- A green connector shows the shortest distance between the two geometries.
- The footer displays the absolute distance and signed ΔX / ΔY from the orange element's closest point to the blue element's closest point. Absolute distance = sqrt(ΔX² + ΔY²).
- Click a selected element to deselect it. A third selection starts a new pair.
- Click empty space or press Esc to clear selection.

## Initial scope

Reads ASCII DXF LINE, ARC, CIRCLE, LWPOLYLINE and legacy 2D POLYLINE entities, including curved bulge segments. Each polyline is one selectable element. Keeps entity type, layer and handle. Uses analytic closest-point calculations for the supported curves; measurements do not depend on zoom or display tessellation.

Drawing geometry is cached in a frozen WPF StreamGeometry and retained in a DrawingVisual. Panning updates a persistent parent transform without replacing the base drawing commands. Selection and screen-space measurement annotations use separate visuals. Zoom and fit refresh pen widths to maintain constant screen-space stroke thickness. File parsing runs off the UI thread. Hit testing first checks entity bounds, then evaluates the underlying curves.

This is an initial 2D XY viewer, not a full CAD engine. It does not expand INSERT/block references or render text, dimensions, hatches, splines, ellipses, binary DXF, or 3D/OCS geometry correctly. Unsupported entity types are counted in the footer. It combines supported ENTITIES records without layout filtering and does not yet apply layer visibility, colors or linetypes. Non-ASCII layer names from legacy code pages may need encoding support.

Units are read from `$INSUNITS` when present; otherwise the footer explicitly says drawing units are unspecified. No unit conversion is applied. Intersections have zero distance. Closest points can be nonunique (for example parallel lines), so ΔX / ΔY describe one valid closest pair.

## Verify

```powershell
dotnet build .\CodexDXF.sln -c Release
dotnet run --project .\Tests\CodexDXF.Checks.csproj -c Release -- "C:\Users\Yoav Miller\Downloads\182N-RND_REV-A.DXF"
```

The console checks cover crossing, parallel, overlapping and degenerate lines, circle/line intersections and tangency, nested and concentric circles, bounded arcs, clockwise arcs, parser units/bulges and the example drawing. No test packages or external NuGet dependencies are needed.

The example's ENTITIES section contains 66,377 supported elements: 66,365 lines and 12 arcs, all on layer `0`. It declares millimeters through `$INSUNITS`. The block definitions are not inserted into this drawing's ENTITIES section.

