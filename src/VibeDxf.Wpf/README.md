# VibeDxf.Wpf

One Windows/WPF library for embedding a 2D DXF viewer and querying drawing geometry, including prepared text outlines. MIT licensed.

**Stable release:** `0.2.0`. Requires Windows 10 or later and the .NET 10 Desktop Runtime. Build hosts target `net10.0-windows` with `UseWPF=true`. The package supplies its Vortice 3.8.3 dependencies transitively. This is not a cross-platform or DWG library.

## Embed the viewer

```xml
<Window ... xmlns:dxf="clr-namespace:VibeDxf.Wpf;assembly=VibeDxf.Wpf">
    <dxf:DxfView x:Name="Viewer" />
</Window>
```

From an async host event handler on the WPF dispatcher:

```csharp
using VibeDxf.Wpf;

Viewer.MeasurementChanged += (_, args) =>
{
    if (args.Measurement is { Status: MeasurementStatus.Success } result)
        Console.WriteLine($"Distance {result.Distance}; ΔX {result.DeltaX}; ΔY {result.DeltaY}");
};

var loaded = await Viewer.LoadAsync(path, cancellationToken);
Viewer.FitToDrawing();
Viewer.SetLayerVisible(loaded.Document.Layers[0].Id, false);
Viewer.SetCategoryVisible(EntityCategory.Dimension, false);
```

The host owns file dialogs, drag/drop, menus and warning presentation. Inspect `loaded.Diagnostics` and handle `RenderingFailed` in your host. The control provides pan/zoom, selection, exact eligible segment-midpoint snapping, independent Closest/Center strategies, model/paper layout switching and layer/category visibility. `ActiveLayout=null` selects model space; assign a layout's `Id` to show a sheet.

## Load, traverse and measure without a viewer

```csharp
var loaded = await DxfDocument.LoadAsync(path, cancellationToken);
var document = loaded.Document;

foreach (var entity in document.AllEntities)
    Console.WriteLine($"{entity.Id}: {entity.Kind}, layer {entity.Layer}, handle {entity.Handle}");

var first = document.Entities[0];
var second = document.Entities[1]; // Check counts/kinds before selecting your targets.
var result = document.Measure(
    new MeasurementAnchor(first.Id), new MeasurementAnchor(second.Id),
    MeasurementStrategy.Closest, MeasurementStrategy.Center);
if (result.Status == MeasurementStatus.Success)
    Console.WriteLine($"{(result.IsApproximate ? "≈ " : "")}{result.Distance}");
```

Loading prepares text curves, bounds and fills on an internal STA worker; callers may await it from an ordinary console thread without an Application, window, GPU or extra geometry provider. The console host still needs Windows Desktop runtime and `UseWPF=true`. `Entities` is model space; `Layouts[i].Entities` is paper space; `AllEntities` includes both. Documents and nested geometry collections are read-only and safe for concurrent queries. Separate loads get distinct identities; handles and collection indices are not entity IDs.

Measurement results include status, closest endpoints, Euclidean distance, signed second-minus-first deltas and approximation information. Invalid/foreign/missing anchors and mixed coordinate contexts return failure status with no fabricated distance. Center only affects standalone analytic ARC/CIRCLE entities. Midpoint anchors require an eligible exact finite straight segment. Model geometry selected through paper viewports stays in model units. Whole annotation graphics can be measured programmatically but remain nonselectable interactively. Hidden layers do not prevent document queries.

## Supported drawing scope

The 3D polyline, mesh and solid projections below are unreleased source additions, not part of the published 0.2.0 package.

ASCII DXF with supported planar LINE, ARC, CIRCLE, 2D polylines/bulges, SPLINE, RAY/XLINE, POINT, SOLID, TEXT/MTEXT, block inserts/arrays/attributes, solid/patterned HATCH, saved dimension blocks, leaders/multileaders and plan-view layout viewports. Unsupported records and supported approximations produce diagnostics; this is not a full CAD editor or complete DXF implementation.

Legacy 3D POLYLINE, polygon meshes and polyface meshes support exact XY wireframe projection. Measurements and picking use projected visible edges; there is no 3D distance or filled-face containment. Negative polyface indices hide outgoing edges. Z-parallel edges become point markers. Fitted/smoothed legacy 3D curves/surfaces and malformed topology produce skips; projection diagnostics explicitly state that Z is ignored.

Text uses Arial substitution rather than original SHX fonts, and text-outline distances are approximate. Spline approximation is bounded by 0.0001 drawing units per spline, aside from floating-point effects; extreme zoom may reveal chords. Nonuniformly transformed arcs may also be approximate. Gradient/dense hatch patterns, general 3D/OCS geometry, standalone ellipses, DWG/binary DXF, external references, dynamic blocks, original linetypes/lineweights and some text layouts remain unsupported. No unit conversion is applied.

MESH entities expose the level-0 face/edge wireframe projected into XY; projected vertical edges also have point markers. Measurement is to projected edges, not a filled surface or a 3D distance. Subdivision smoothing uses the control cage with an approximation flag and rendering warning; per-face appearance overrides are not displayed.

Per-entity visibility, entity removal/editing, undo and writing/saving DXF files are not implemented. Visibility controls apply to layers/categories and respect per-viewport frozen layers. Source colors/saved layer flags remain unchanged by per-view settings.

`3DSOLID` supports XY boundary wireframes for bounded straight and elliptical ACIS SAT/SAB edges. Actual edge/vertex/point topology and each body's 3D transform are resolved before projection. These selectable/measurable edges have no filled region; distances and midpoint targets describe their projected XY boundaries, not 3D distances or solid interiors. Collapsed edges receive point markers. Circular XY projections retain analytic arcs; other elliptical projections use a 0.0001 drawing-unit chord-error bound before INSERT scaling, set `IsApproximate`, and do not offer midpoint anchors. Unsupported intersection/spline/procedural curves or topology/encodings are skipped as whole entities with explicit diagnostics rather than fabricated endpoint chords. This is not a general ACIS kernel, hidden-line remover or surface tessellator.

## Ownership and threading

Access the control on its STA WPF dispatcher and await asynchronous calls; do not block its dispatcher with Wait/Result. A prepared document can be assigned to multiple viewers; each owns its camera, selection, visibility, native surface and GPU resources. New loads supersede old ones; cancellation/preparation/allocation failure retains the installed drawing. Individual font/driver calls are not interruptible. `ClearDocument` releases drawing state; unload releases devices and reload recreates them; `Dispose` permanently cancels work/releases resources and must run on the dispatcher before shutdown. Device-loss recovery is not guaranteed.

Path loading owns its stream. `DxfDocument.LoadAsync(stream, leaveOpen: true, cancellationToken)` leaves a supplied stream open by default; keep it alive and avoid concurrent access until completion. Documents contain managed prepared data and need no Dispose.

The viewer uses HwndHost, so ordinary WPF controls cannot reliably overlay its native drawing surface. Put panels alongside it or use its rendered measurement overlays. Screen coordinates are control-relative DIPs; world coordinates use DXF XY/y-up values with double precision.

Package contents include the managed library, XML API documentation, this README, MIT license and third-party notices. The dependent Vortice packages retain their own licenses. Public DXF fixtures and application icons/installers are not included. See the repository's `samples/PackageConsumers` for independent WPF and console package consumers and `docs/NUGET-SINGLE-INTEGRATION.md` for more detail.
