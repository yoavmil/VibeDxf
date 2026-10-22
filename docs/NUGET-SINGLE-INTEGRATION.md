# Single-library integration (project references)

Stage 2, 2026-10-22. This is the implemented single Windows/WPF library, not the archived Core/Wpf split. Stage 4 adds the local MIT-licensed VibeDxf.Wpf 0.2.0-preview.1 package, XML documentation and independent consumers in samples/PackageConsumers. The user declined per-entity visibility and in-memory removal; those APIs are not implemented.

Reference `src/VibeDxf.Wpf/VibeDxf.Wpf.csproj` from a `net10.0-windows` project with `UseWPF=true`. The one library includes parsing, text shaping, numeric geometry, measurements and the viewer. Its Vortice 3.8.3 references flow transitively; no separate Core dependency or text-preparation provider is needed.

[VibeDxf.Wpf 0.2.0-preview.1 is now public on NuGet.org](https://www.nuget.org/packages/VibeDxf.Wpf/0.2.0-preview.1); see [publication and automation results](NUGET-STAGE6-RESULTS.md) and the package consumer examples for public-feed restore instructions.

## Document-only loading and measurements

```csharp
using VibeDxf.Wpf;

var result = await DxfDocument.LoadAsync(path, cancellationToken);
var document = result.Document;

foreach (var entity in document.Entities) // Model space only.
    Console.WriteLine($"{entity.Id}: {entity.Kind}, {entity.Layer}, {entity.SourceHandle}");

foreach (var layout in document.Layouts)
    foreach (var entity in layout.Entities) { /* Paper coordinates for that sheet. */ }

var first = document.Entities.First();
var second = document.Entities.Skip(1).First();
var measurement = document.Measure(new(first.Id), new(second.Id));
if (measurement.Status == MeasurementStatus.Success)
    Console.WriteLine($"Distance {measurement.Distance}; ΔX {measurement.DeltaX}; ΔY {measurement.DeltaY}");
```

`AllEntities` traverses model, then retained paper layouts in tab order. Entity IDs contain a document GUID plus an occurrence value. They are distinct across separate loads, even for the same file. Source handles and expanded instance paths are metadata, not unique IDs. `FindEntity` returns null for nonmembers. Viewport definitions link to their generated frame through `FrameEntityId`.

Loading returns only after text curves/contours, bounds and triangles are prepared. TEXT/MTEXT/ATTRIB use the existing Arial substitution; glyph distances are marked approximate. Spline geometry retains its 0.0001-unit bound. Point/line/arc/ray/xline measurement remains analytic where supported. Center affects standalone analytic ARC/CIRCLE entities only; finite exact midpoint anchors use a curve index. Unsupported midpoints or strategies return InvalidAnchor. Query failures have null distance/endpoints, never a fabricated zero. Mixed model/paper/different-sheet pairs return CoordinateMismatch. Queries may measure annotation graphics even though annotations remain excluded from interactive selection.

Documents and every nested geometry collection are read-only. Numeric WPF Point/Vector/Rect/Matrix values remain double precision. Color is source RGBA without theme adjustment; `ColorSource` retains ACI/true-color metadata. `DxfDisplayColors.ForDarkBackground` supplies the viewer policy for host swatches. Source layer saved on/frozen flags never change when a view is toggled.

Document loading internally uses an STA worker; callers can await from an ordinary console/thread-pool thread without constructing a WPF Application or control. Completed documents can be read and measured concurrently without a dispatcher, viewer HWND or library graphics initialization. Windows and the .NET 10 Desktop Runtime are still required. See `tools/DocumentSmoke` for a console consumer that loads public fixtures, measures text, and verifies no Application or Vortice assemblies were initialized.

Path loading owns its stream. `DxfDocument.LoadAsync(stream, leaveOpen: true, cancellationToken)` leaves the caller's stream open on success, failure and cancellation; pass false to transfer disposal responsibility. Keep the stream alive and do not access it concurrently until completion. Cancellation is checked during reading, expansion and geometry work. A single font-shaping operation cannot be interrupted; cancellation prevents publishing its result. Empty/unsupported-only loads retain the original parser's error behavior. Structured load diagnostics describe supported approximations and unsupported records separately from exceptions for unreadable/malformed input.

## Embedding the control

```xml
<dxf:DxfView x:Name="Viewer"
    xmlns:dxf="clr-namespace:VibeDxf.Wpf;assembly=VibeDxf.Wpf" />
```

On the host dispatcher:

```csharp
await Viewer.SetDocumentAsync(document, cancellationToken);
Viewer.FitToDrawing();
Viewer.FirstMeasurementStrategy = MeasurementStrategy.Closest;
Viewer.SecondMeasurementStrategy = MeasurementStrategy.Center;
Viewer.MeasurementChanged += (_, args) =>
{
    // args.Document / RevisionId identify the committed snapshot.
    // args.Selection is an immutable ordered selection snapshot.
    // args.Measurement includes status, endpoints, distance, deltas and approximation.
};
```

`await Viewer.LoadAsync(path, token)` is a convenience that uses the same document loader and then assigns the result. The host owns dialogs, drag/drop, menus, application shortcuts, layer panels and warning presentation. The library never displays a MessageBox or uses application resources. The existing app and demo use this public API.

`Document` is a read-only dependency property. `FirstMeasurementStrategy`, `SecondMeasurementStrategy`, `ActiveLayout` and `Monochrome` are bindable dependency properties. ActiveLayout is a document layout ID, or null for model space. Strategy changes recalculate without reselection. `Select(anchor, viewportFrameId)` obeys current interactive visibility/annotation policy; model selections through paper windows carry the source frame ID while measurement stays in model units.

`SetLayerVisible`, `SetLayersVisible`, `SetCategoryVisible`, `ShowAll`, `IsolateLayer` and `ApplySavedLayerState` control each viewer independently. Per-viewport frozen layers continue to affect drawing and picking. Hidden entities remain available for programmatic document measurements. Entity-specific switches and removal/edited snapshots were declined by the user and are excluded from the current package.

Camera stores finite positive scale and a DIP offset. `WorldToScreen` / `ScreenToWorld` use model/paper coordinates for the active space; pass a source viewport frame ID to convert model coordinates through that window. Invalid layout/window/selection IDs are rejected, rather than silently selecting geometry from another document.

DocumentChanged, SelectionChanged, MeasurementChanged, LayoutChanged and VisibilityChanged carry committed state on the owning dispatcher. Selection and visibility payload collections are immutable snapshots. Handle RenderingFailed to report native/driver/rendering errors in the host. Device-loss recovery is not implemented.

## Threading and lifetime

Construct and access controls on their STA WPF dispatcher. Await async calls; do not block the dispatcher with Result/Wait. Document preparation does not require a caller synchronization context; final control assignment explicitly dispatches to the owning dispatcher.

A new load/assignment supersedes an old one across both parsing and installation. ClearDocument and Dispose cancel pending generations. Cancellation and preparation/allocation failure retain the previous installed document. Candidate GPU buffers are allocated before replacing existing geometry; cancellation/failure disposes the candidate resources. Driver/device failure during rendering is reported separately and is not a promise to restore a working device.

The document may be shared by two controls; selection, camera, visibility, native HWND and device resources are independent. Geometry upload occurs on assignment or renderer recreation. Pan, zoom, hover, layout changes, layer/category visibility and monochrome reuse geometry buffers.

Unloading releases graphics resources and preserves document/view state so reloading can recreate the renderer. ClearDocument releases the drawing geometry and source references and permits a later load. Dispose permanently cancels work, clears references and prevents recreation; call it on the dispatcher when finished. Async cancellation cleanup needs a live dispatcher, so dispose before shutting it down. The desktop app cancels its load when its window closes and disposes its viewer.

`HwndHost` airspace prevents ordinary WPF overlays reliably drawing over the native surface. Place host UI alongside the viewer or use its rendered overlays. The child HWND stays mouse-transparent so WPF receives input. Resize and simulated DPI notifications are tested; physical multi-monitor transitions have not been exercised in this session.
