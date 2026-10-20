# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CodexDXF is a small, dependency-free C# / WPF 2D DXF viewer with element-to-element distance measurement, targeting `net10.0-windows`. No NuGet packages are used — keep it that way. See [README.md](README.md) for user-facing controls and the supported/unsupported DXF feature scope.

## Commands

```powershell
# Build
dotnet build .\CodexDXF.sln -c Release

# Run the viewer (optional DXF path argument)
dotnet run --project .\CodexDXF.csproj -- "path\to\file.dxf"

# Run all checks (no test framework; a console app that throws on first failure)
dotnet run --project .\Tests\CodexDXF.Checks.csproj -c Release

# Also verify against the sample drawing (not in repo; expects exactly 66,377 entities, none skipped)
dotnet run --project .\Tests\CodexDXF.Checks.csproj -c Release -- "C:\Users\Yoav Miller\Downloads\182N-RND_REV-A.DXF"
```

There is no way to run a single check; the program runs every check in sequence and stops at the first exception. To focus on one, temporarily comment out the others in [Tests/Program.cs](Tests/Program.cs).

## Architecture

All app sources sit flat in the repo root under namespace `CodexDXF`:

- [Geometry.cs](Geometry.cs) — `Curve` (a single record representing both line segments and arcs; `Radius == 0` means line) with analytic `Closest(Point)` and `Curve.Distance(a, b)` returning a `ClosestPair`. Also `DxfEntity` (kind, layer, handle, list of curves). Every entity — including polylines with bulges — is decomposed into `Curve`s, and all measurement is done in DXF XY coordinates, independent of zoom or tessellation.
- [DxfReader.cs](DxfReader.cs) — ASCII-only parser. Reads all group-code pairs into memory, picks up `$INSUNITS`, then walks `0`-delimited records in the `ENTITIES` section. Unsupported entity types are counted in `DxfDocument.Skipped`. Polyline bulges become arc `Curve`s.
- [MeasurementTarget.cs](MeasurementTarget.cs) — a selection target is either a whole entity or the exact midpoint of one of its straight segments (`MidpointSegment` index). `MidpointIndex` keeps X- and Y-sorted arrays and binary-searches them for hover snapping instead of scanning every entity.
- [DxfViewport.cs](DxfViewport.cs) — custom `FrameworkElement` that renders with retained visuals: one frozen `StreamGeometry` for the whole drawing in `baseVisual`, plus separate `selectionVisual`, `measurementVisual` and `snapVisual`. Pan only updates `cameraTransform` and does not rebuild the base drawing. Zoom and fit refresh pen widths to keep strokes a constant screen width. Selection order matters: signed ΔX/ΔY go from the first (orange) target to the second (blue).
- [MainWindow.xaml.cs](MainWindow.xaml.cs) — file open, drag-drop, background-thread parsing, and the footer that shows status and measurement.

## Gotchas

- **The test project compiles the app sources by linking them.** [Tests/CodexDXF.Checks.csproj](Tests/CodexDXF.Checks.csproj) sets `EnableDefaultCompileItems=false` and lists each linked file explicitly. A new app source file that the viewport/geometry/reader depends on must be added there too. The main project in turn excludes `Tests\**`.
- **Viewport checks use reflection on private members.** [Tests/ViewportChecks.cs](Tests/ViewportChecks.cs) accesses the `baseVisual` and `snapVisual` fields and the `SelectAt(Point, bool)` and `SetHoveredMidpoint(MeasurementTarget)` methods by name. Renaming or changing the signature of any of these breaks the checks at runtime, not at compile time. These checks run on a dedicated STA thread.
- The checks also assert that hover and selection never replace the base drawing's `Pen` instance. This is a performance invariant: overlays must not rebuild `baseVisual`.
- `ALT-F2/` holds a Visual Studio diagnostics session (`.diagsession`) from profiling. It is not source.
