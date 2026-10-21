# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

VibeDxf is a small C# / WPF 2D DXF viewer with element-to-element distance measurement, targeting `net10.0-windows`. The drawing is rendered with Direct3D 11 and Direct2D through Vortice.Windows (`Vortice.Direct3D11`, `Vortice.Direct2D1`, `Vortice.D3DCompiler`). These are the only NuGet packages; don't add others without a good reason. See [README.md](README.md) for user-facing controls and the supported/unsupported DXF feature scope.

## Packaging and releases

See [RELEASING.md](RELEASING.md) for the release procedure, versioning, local installer builds, GitHub Actions publication, icons, runtime requirements, and verification/recovery. Keep these maintainer details there and keep README.md user-facing. Releases are framework-dependent Windows x64 builds: never bundle, download, or install the .NET runtime automatically.

## Commands

```powershell
# Build
dotnet build .\VibeDxf.sln -c Release

# Run the viewer (optional DXF path argument)
dotnet run --project .\src\VibeDxf.csproj -- "path\to\file.dxf"

# Run all checks (no test framework; a console app that throws on first failure)
dotnet run --project .\Tests\VibeDxf.Checks.csproj -c Release

# Also verify against the sample drawing (not in repo; expects exactly 66,377 entities, none skipped)
dotnet run --project .\Tests\VibeDxf.Checks.csproj -c Release -- "C:\Users\Yoav Miller\Downloads\182N-RND_REV-A.DXF"
```

There is no way to run a single check; the program runs every check in sequence and stops at the first exception. To focus on one, temporarily comment out the others in [Tests/Program.cs](Tests/Program.cs).

## Architecture

All app sources sit flat in [src/](src/) alongside [src/VibeDxf.csproj](src/VibeDxf.csproj), under namespace `VibeDxf`. The solution, [nuget.config](nuget.config) and docs stay in the repo root:

- [Geometry.cs](src/Geometry.cs) — `Curve` (a single record representing both line segments and arcs; `Radius == 0` means line) with analytic `Closest(Point)` and `Curve.Distance(a, b)` returning a `ClosestPair`. Also `DxfEntity` (kind, layer, handle, list of curves). Every entity — including polylines with bulges — is decomposed into `Curve`s, and all measurement is done in DXF XY coordinates, independent of zoom or tessellation.
- [DxfReader.cs](src/DxfReader.cs) — ASCII-only parser. Reads all group-code pairs into memory, picks up `$INSUNITS`, then walks `0`-delimited records in the `ENTITIES` section. Unsupported entity types are counted in `DxfDocument.Skipped`. Polyline bulges become arc `Curve`s.
- [MeasurementTarget.cs](src/MeasurementTarget.cs) — a selection target is either a whole entity or the exact midpoint of one of its straight segments (`MidpointSegment` index). `MidpointIndex` keeps X- and Y-sorted arrays and binary-searches them for hover snapping instead of scanning every entity.
- [DrawingRenderer.cs](src/DrawingRenderer.cs) — Direct3D 11 renderer on a flip-model swap chain. `SetDrawing` uploads the curves once per document into immutable GPU buffers: line segments as a `LineList`, and arcs as per-instance data that the `ArcVS` shader expands into line strips. Arcs are sorted by radius into power-of-two buckets, each drawn with just enough segments for ~¼ px chord error at the current zoom. Coordinates are stored relative to the drawing's centre (`Origin`) so float32 keeps precision. A frame only rewrites one dynamic constant buffer (camera + color) and issues the draw calls. Overlays are drawn afterwards with Direct2D/DirectWrite on the same back buffer, in DIPs. HLSL is compiled at startup with `D3DCompiler`.
- [DxfViewport.cs](src/DxfViewport.cs) — a WPF `Border` that holds `SwapChainHost`, an `HwndHost` child window owning the swap chain. The child returns `HTTRANSPARENT` from `WM_NCHITTEST`, so all mouse input (including the wheel) arrives as ordinary WPF events on `DxfViewport`. The viewport owns the camera (`scale`/`offset`, DIPs, y-up world), selection, hover snapping and measurement. Every change calls `Invalidate`, which coalesces into one `RenderNow` per dispatcher pass. Selected entities' outlines are rebuilt in screen space each frame as Direct2D path geometries (3 px strokes). Selection order matters: signed ΔX/ΔY go from the first (orange) target to the second (blue).
- [MainWindow.xaml.cs](src/MainWindow.xaml.cs) — file open, drag-drop, background-thread parsing, and the footer that shows status and measurement.

## Gotchas

- See [SPLINES.md](SPLINES.md) for supported spline data, the 0.0001-unit approximation bound, approximate measurements, and offline visual test reports. `SplineGeometry.cs` flattens NURBS once at load; spline chords must not become midpoint snap targets. Tests write `artifacts/test-report/index.html` with diagrams, actual GPU readbacks, and all check results. Run the console checks from the repo root; they are not discoverable in VS Test Explorer yet.
- The bottom-right Center/Closest toggles belong to the first (orange) and second (blue) selection slots. Only standalone CIRCLE/ARC entities use their underlying circle center; lines, polylines, and explicit midpoints keep their existing behavior. `DxfViewport.FirstStrategy` / `SecondStrategy` immediately recalculate and raise `SelectionChanged` so the footer and connector refresh without reselecting. Offline strategy checks live in [Tests/MeasurementStrategyChecks.cs](Tests/MeasurementStrategyChecks.cs); run the checks with `--no-restore` after dependencies are restored to avoid network access.
- **The test project compiles the app sources by linking them.** [Tests/VibeDxf.Checks.csproj](Tests/VibeDxf.Checks.csproj) sets `EnableDefaultCompileItems=false` and lists each linked file explicitly. A new app source file that the viewport/geometry/reader depends on must be added there too, and it repeats the Vortice `PackageReference`s.
- **Viewport checks use reflection on private members.** [Tests/ViewportChecks.cs](Tests/ViewportChecks.cs) accesses `DxfViewport`'s `host`, `offset`, `scale` and `hoveredMidpoint` fields, `SwapChainHost`'s auto-property backing field `<Renderer>k__BackingField`, `DrawingRenderer`'s `lineBuffer`/`arcBuffer`, and the `SelectAt(Point, bool)`, `SetHoveredMidpoint(MeasurementTarget)` and `RenderNow()` methods by name. Renaming or changing the signature of any of these breaks the checks at runtime, not at compile time. These checks run on a dedicated STA thread and show a real off-screen window, so they need a GPU or the WARP fallback.
- The checks assert that hover, selection, pan and zoom reuse the same GPU buffers. This is a performance invariant: only `SetDrawing` (a new document, or a renderer re-created on `Loaded`) may upload geometry.
- **Renderer lifetime follows the visual tree, not the HWND.** When the window closes, WPF parks `HwndHost` children instead of destroying them, so `DestroyWindowCore` isn't called. `SwapChainHost` disposes the renderer on `Unloaded` and re-creates it on `Loaded`.
- Don't remove `HTTRANSPARENT` or make the child window disabled. A disabled child drops clicks, and a hit-testable one takes mouse input away from WPF.
- [nuget.config](nuget.config) clears all package sources except nuget.org, because the machine-wide config has a private feed that fails with 401.
- `ALT-F2/` holds a Visual Studio diagnostics session (`.diagsession`) from profiling. It is not source.
