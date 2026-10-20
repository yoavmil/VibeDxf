# VibeDxf

A 2D DXF viewer for Windows that measures the shortest distance between any two elements. Written in C# / WPF on .NET 10, with GPU rendering through Direct3D 11 and Direct2D ([Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)).

## Requirements

- Windows 10 or later
- .NET 10 SDK
- A Direct3D 11 capable GPU. Without one, the viewer falls back to WARP, Windows' software rasterizer.

## Build and run

Open `VibeDxf.sln` in Visual Studio, or from the repository root:

```powershell
dotnet build .\VibeDxf.sln -c Release
dotnet run --project .\src\VibeDxf.csproj -c Release -- "path\to\drawing.dxf"
```

The DXF path is optional; you can also open a file from the app. Package restore uses only nuget.org (see `nuget.config`).

## Controls

- File > Open, Ctrl+O, or drop one `.dxf` file to open it.
- Mouse wheel zooms around the pointer.
- Hold the middle or right mouse button and drag to pan.
- F fits the drawing to the window.
- Left click selects an element (orange), then a second element (blue).
- Hover within 8 screen pixels of a straight segment's midpoint to show a green triangle. Click to select that exact midpoint. Selected midpoints show as orange/blue triangles.
- Midpoints work on LINE entities and straight polyline segments. You can measure midpoint-to-midpoint, midpoint-to-element, or element-to-midpoint.
- Hold Shift while clicking to select the whole element even when near a midpoint. In dense drawings, zoom in to tell nearby midpoints apart.
- A green connector shows the shortest distance between the two selections.
- The footer shows the absolute distance and the signed ΔX / ΔY from the orange selection's closest point to the blue one's. Absolute distance = sqrt(ΔX² + ΔY²).
- Click a selected element to deselect it. A third selection starts a new pair.
- Click empty space or press Esc to clear the selection.

## Supported DXF

Reads ASCII DXF LINE, ARC, CIRCLE, LWPOLYLINE and legacy 2D POLYLINE entities, including curved bulge segments. Each polyline is one selectable element. Entity type, layer and handle are kept.

Not supported yet: INSERT/block references, text, dimensions, hatches, splines, ellipses, binary DXF, and 3D/OCS geometry. Unsupported entity types are counted in the footer. All supported ENTITIES records are shown together, without layout filtering, layer visibility, colors or linetypes. Non-ASCII layer names from legacy code pages may need encoding support.

Units come from `$INSUNITS` when present; otherwise the footer says the drawing units are unspecified. No unit conversion is applied.

## Measurement

Distances use analytic closest-point calculations on the actual lines and arcs, in DXF XY coordinates, so they don't depend on zoom or on how curves are drawn on screen. Intersecting elements measure zero. The closest pair can be non-unique (for example, two parallel lines), in which case ΔX / ΔY describe one valid closest pair.

## Rendering

The drawing is uploaded to the GPU once per file: line segments as one vertex buffer, and arcs as instances that a vertex shader expands, with more segments as you zoom in. Pan and zoom only change a camera constant, so frame cost doesn't grow with rebuilding geometry. Strokes stay 1 pixel wide at any zoom. Selection highlights, midpoint markers and the measurement label are drawn on top with Direct2D. Files are parsed off the UI thread, and hit testing checks entity bounds before evaluating curves.

## Project layout

```
VibeDxf.sln
src/      the viewer: VibeDxf.csproj, DXF reader, geometry, measurement, renderer, WPF window
Tests/    VibeDxf.Checks.csproj, a console app that runs all checks
```

## Checks

```powershell
dotnet run --project .\Tests\VibeDxf.Checks.csproj -c Release
```

The checks are a console app with no test framework. They stop at the first failure. They cover:

- distances between lines and arcs: crossing, parallel, overlapping and degenerate lines; circle/line intersections and tangency; nested and concentric circles; bounded and clockwise arcs
- DXF units, metadata and bulge parsing
- selection, snapping and measurement in the viewport
- overlay rendering in an off-screen window
- that pan, zoom, hover and selection reuse the uploaded GPU buffers

Optionally, pass the path to the reference drawing `182N-RND_REV-A.DXF` (not committed to this repository) as an extra argument. The checks then also expect exactly 66,377 supported entities, none skipped.
