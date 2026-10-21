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

# Run xUnit checks (also discoverable in Visual Studio Test Explorer)
dotnet test .\Tests\VibeDxf.Checks.csproj -c Release

```

Use Test Explorer to select individual named cases, or `dotnet test --filter "DisplayName~circle center / center"`. The 113 original offline checks are xUnit theory cases with string-only discovery data; an extra test ensures the catalog matches the scenario checks. Two public fixture cases always run from `Tests/Fixtures/Public`, checking exact supported/unsupported counts and GPU uploads. See its README for provenance and coverage. Runtime setup is rebuilt for each case; rendering cases run on dedicated STA threads. A shared collection fixture writes the HTML report at the end, with parallel execution disabled to keep GPU capture and report output deterministic.

## Architecture

All app sources sit flat in [src/](src/) alongside [src/VibeDxf.csproj](src/VibeDxf.csproj), under namespace `VibeDxf`. The solution, [nuget.config](nuget.config) and docs stay in the repo root:

- [Geometry.cs](src/Geometry.cs) — `Curve` (a single record representing both line segments and arcs; `Radius == 0` means line) with analytic `Closest(Point)` and `Curve.Distance(a, b)` returning a `ClosestPair`. Also `DxfEntity` (kind, layer, handle, list of curves). Every entity — including polylines with bulges — is decomposed into `Curve`s, and all measurement is done in DXF XY coordinates, independent of zoom or tessellation.
- [DxfReader.cs](src/DxfReader.cs) — ASCII-only parser. Reads all group-code pairs into memory, picks up `$INSUNITS`, then collects `0`-delimited records in ENTITIES and BLOCKS and recursively expands referenced blocks. Unsupported entity types are counted in `DxfDocument.Skipped` and produce a warning after opening. INSERT expansion protects against cycles, depth, array and entity-count limits. Child layer 0 inherits the insertion layer; handles contain the insertion path. `DxfPrimitives.cs` creates text contours, transforms curves and triangulates fills once at load/upload. Text substitutes Arial; nonuniform transformed arcs and glyph outlines are approximate. POINT coordinates remain exact; their markers are fixed-size in the GPU shader. Polyline bulges become arc `Curve`s.
- [MeasurementTarget.cs](src/MeasurementTarget.cs) — a selection target is either a whole entity or the exact midpoint of one of its straight segments (`MidpointSegment` index). `MidpointIndex` keeps X- and Y-sorted arrays and binary-searches them for hover snapping instead of scanning every entity.
- [DrawingRenderer.cs](src/DrawingRenderer.cs) — Direct3D 11 renderer on a flip-model swap chain. `SetDrawing` uploads the curves once per document into immutable GPU buffers: line segments as a `LineList`, SOLID/text fills as a `TriangleList`, fixed-size POINT crosses as a `LineList` with a point shader, and arcs as per-instance data that the `ArcVS` shader expands into line strips. Arcs are sorted by radius into power-of-two buckets, each drawn with just enough segments for ~¼ px chord error at the current zoom. Coordinates are stored relative to the drawing's centre (`Origin`) so float32 keeps precision. A frame only rewrites one dynamic constant buffer (camera + color) and issues the draw calls. Overlays are drawn afterwards with Direct2D/DirectWrite on the same back buffer, in DIPs. HLSL is compiled at startup with `D3DCompiler`.
- [DxfViewport.cs](src/DxfViewport.cs) — a WPF `Border` that holds `SwapChainHost`, an `HwndHost` child window owning the swap chain. The child returns `HTTRANSPARENT` from `WM_NCHITTEST`, so all mouse input (including the wheel) arrives as ordinary WPF events on `DxfViewport`. The viewport owns the camera (`scale`/`offset`, DIPs, y-up world), selection, hover snapping and measurement. Every change calls `Invalidate`, which coalesces into one `RenderNow` per dispatcher pass. Selected entities' outlines are rebuilt in screen space each frame as Direct2D path geometries (3 px strokes). Selection order matters: signed ΔX/ΔY go from the first (orange) target to the second (blue).
- [MainWindow.xaml.cs](src/MainWindow.xaml.cs) — file open, drag-drop, background-thread parsing, and the footer that shows status and measurement.

## Gotchas

- See [SPLINES.md](SPLINES.md) for supported spline data, the 0.0001-unit approximation bound, approximate measurements, and offline visual test reports. `SplineGeometry.cs` flattens NURBS once at load; spline chords must not become midpoint snap targets. Tests write `artifacts/test-report/index.html` with diagrams, actual GPU readbacks, and all test results. Build the solution to discover the xUnit cases in VS Test Explorer. Add new named checks to `Tests/CheckCatalog.cs`; the catalog completeness test catches missing discovery entries.
- The bottom-right Center/Closest toggles belong to the first (orange) and second (blue) selection slots. Only standalone CIRCLE/ARC entities use their underlying circle center; lines, polylines, and explicit midpoints keep their existing behavior. `DxfViewport.FirstStrategy` / `SecondStrategy` immediately recalculate and raise `SelectionChanged` so the footer and connector refresh without reselecting. Offline strategy checks live in [Tests/MeasurementStrategyChecks.cs](Tests/MeasurementStrategyChecks.cs); run the checks with `--no-restore` after dependencies are restored to avoid network access.
- **The test project compiles the app sources by linking them.** [Tests/VibeDxf.Checks.csproj](Tests/VibeDxf.Checks.csproj) sets `EnableDefaultCompileItems=false` and lists each linked file explicitly. A new app source file that the viewport/geometry/reader depends on must be added there too, and it repeats the Vortice `PackageReference`s.
- **Viewport checks use reflection on private members.** [Tests/ViewportChecks.cs](Tests/ViewportChecks.cs) accesses `DxfViewport`'s `host`, `offset`, `scale` and `hoveredMidpoint` fields, `SwapChainHost`'s auto-property backing field `<Renderer>k__BackingField`, `DrawingRenderer`'s `lineBuffer`/`arcBuffer`, and the `SelectAt(Point, bool)`, `SetHoveredMidpoint(MeasurementTarget)` and `RenderNow()` methods by name. Renaming or changing the signature of any of these breaks the checks at runtime, not at compile time. These checks run on a dedicated STA thread and show a real off-screen window, so they need a GPU or the WARP fallback.
- The checks assert that hover, selection, pan and zoom reuse the same GPU buffers. This is a performance invariant: only `SetDrawing` (a new document, or a renderer re-created on `Loaded`) may upload geometry.
- **Renderer lifetime follows the visual tree, not the HWND.** When the window closes, WPF parks `HwndHost` children instead of destroying them, so `DestroyWindowCore` isn't called. `SwapChainHost` disposes the renderer on `Unloaded` and re-creates it on `Loaded`.
- Don't remove `HTTRANSPARENT` or make the child window disabled. A disabled child drops clicks, and a hit-testable one takes mouse input away from WPF.
- [nuget.config](nuget.config) clears all package sources except nuget.org, because the machine-wide config has a private feed that fails with 401.
- `ALT-F2/` holds a Visual Studio diagnostics session (`.diagsession`) from profiling. It is not source.

- Public primitive tests live in `Tests/PrimitiveChecks.cs`, with `Tests/Fixtures/primitives-and-blocks.dxf`. They are direct xUnit tests (not catalog scenarios) and add GPU captures to the HTML report. Both the checks project and demo generator link `DxfPrimitives.cs`.

- `DxfHatch.cs` parses hatch paths and clips stored pattern definitions once at load. `Curves` holds measurement/selection boundaries; `RenderCurves` overrides visible strokes, `FillContours` holds solid fill, and `RegionContours` handles both hatch kinds. `RenderDots` uses a small fixed-size GPU cross. Gradient and density-limit cases warn instead of silently showing a wrong fill. Triangulation splits at edge intersections and rejects numerical sliver strips. `Tests/HatchChecks.cs` covers all boundary types, island styles, concavity, dash phases, dots, block transforms, and all 77 public sample hatch instances. It also generates a zoomed GPU atlas in the HTML report. Search the report for a hatch handle to see its orange hatch strokes and stored contour against the white drawing. Sample hatch 95F2 and outline 14C2 are explicitly offset in the source file; do not invent an association to move it.

- `DxfMText.cs` uses WPF `TextFormatter` at load time to shape/wrap native text runs, then extracts vector glyph and decoration geometry for immutable GPU buffers. DXF fractions and stretched/tracked/slanted spans use embedded text objects. It retains DXF baseline spacing and attachment transforms. It supports chunks (group 3 + 1), paragraphs, wrapping, attachments, planar WCS direction/normal, formatting scopes, fractions, height/width/tracking/slant and bold/italic flags with Arial substitution. MTEXT is WCS-based: do not apply the reader's OCS mirror to it a second time. `DxfDocument.RenderingWarnings` reports partial formatting limitations without counting loaded text as skipped. Columns/vertical/3D layouts remain explicit skips. Tests in `Tests/MTextChecks.cs` include GPU captures; `Tests/Fixtures/mtext.dxf` is a public synthetic fixture. Link this source in the test and demo projects when changing their explicit compile items.

- MTEXT tags after group 101 (`Embedded Object`) and group 1001 (XDATA) must not be read as main entity properties. Embedded group 10/20 is a direction, while group 11/21 is a repeated insertion point. `uncommon.dxf` has this layout on all 19 labels; interpreting 11/21 as the axis causes false 34–69 degree rotations. Single embedded columns work; multiple columns remain unsupported. TextFormatter uses WrapWithOverflow so unbreakable CAD labels are not split into individual letters.
