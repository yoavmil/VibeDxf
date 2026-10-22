> Archived: the user cancelled the two-library extraction on 2026-10-22. Its code has been reverted to the original single-project application. This document describes the abandoned implementation, not current APIs.

# Embedding the WPF viewer

Stage 3 project integration, 2026-10-22. NuGet packing and independent package consumers are Stage 4 work and are not implemented yet.

Reference `src/VibeDxf.Wpf/VibeDxf.Wpf.csproj` from a `net10.0-windows` WPF host. Its project reference supplies `VibeDxf.Core`; Vortice 3.8.3 remains the renderer dependency. Core alone targets `net10.0` and needs no Windows Desktop runtime. Packing stays disabled in both libraries.

```xml
<dxf:DxfView x:Name="Viewer"
    xmlns:dxf="clr-namespace:VibeDxf.Wpf;assembly=VibeDxf.Wpf" />
```

Load from a host event handler on the WPF dispatcher:

```csharp
var result = await VibeDxf.Core.DxfReader.LoadAsync(path, cancellationToken);
await Viewer.SetDocumentAsync(result.Document, cancellationToken);
Viewer.FitToDrawing();
Viewer.MeasurementChanged += (_, args) =>
{
    // args.Selection is a read-only snapshot of source entities and anchors.
    // A successful args.Measurement includes Pair, Distance, DeltaX/DeltaY
    // and IsApproximate. Both deltas run from first selection to second.
};
```

The host owns file dialogs, drag/drop, keyboard shortcuts, menus, layer panels and warning presentation. Use the load result's diagnostics and the view's `PreparationDiagnostics` to present warnings. The control does not open dialogs or depend on the application's resources. The existing desktop app is one host; the demo also loads and selects through the public API.

## Documents and text

`Document` is the original Core document, which may be shared by multiple controls. Selection, camera, visibility, prepared glyphs and GPU resources belong to each control. Changing a view never edits source entity geometry, saved layer state or raw DXF colors. `DxfDisplayColors.ForDarkBackground` exposes the current display policy for host layer swatches.

`SetDocumentAsync` shapes text on a dedicated STA worker, converts geometry into private prepared rendering data and triangulates before upload. No glyph shaping happens while panning or zooming. The public `PreparedGeometry` is a neutral Core `GeometrySnapshot`, allowing the same prepared text measurements outside the control. Source text entities remain deferred. Blank text records remain in the source document but produce no visible prepared entity; counts in `Document.Entities` can therefore differ from visible geometry counts.

`Selected` and event payloads identify source entities, document-local anchors and optional source viewport definitions. The snapshot cannot be changed by casting it to a mutable list. Programmatic `Select(anchor, viewport)` validates current visibility, annotation policy, midpoint eligibility and coordinate context. A third selection starts a new pair; selecting an existing target toggles it off. Text and spline chords do not become midpoint targets.

`FirstMeasurementStrategy` and `SecondMeasurementStrategy` are WPF dependency properties using Core's `MeasurementStrategy`. Changing either recalculates without reselection. Center applies only to standalone ARC/CIRCLE entities. Measurement events carry `MeasurementResult`; approximation flags and signed deltas come from Core's prepared snapshot.

## Threading, cancellation and lifetime

Construct and access the control on an STA WPF dispatcher. Document assignment explicitly dispatches its final installation even when the caller has no WPF synchronization context. Await asynchronous calls; do not block the dispatcher with `.Wait()` or `.Result`.

A newer assignment cancels an older preparation; generation checks prevent stale completion from replacing the newer drawing. Cancellation is checked between entities and during triangulation. A single font-shaping operation is not interruptible. The old document stays installed on cancellation or preparation/snapshot-validation failure. GPU buffers are allocated before replacing old buffers so allocation failures dispose the candidate buffers. Graphics-device loss during rendering is not an automatic device-recovery contract.

`ClearDocument()` cancels pending replacement, clears selection and prepared data and releases the previous drawing's geometry buffers. A later assignment is supported. Unload releases device resources; reload recreates them from retained CPU preparation. `Dispose()` permanently cancels pending work, clears data and prevents device recreation; it is idempotent and must run on the dispatcher. Unload is reversible; dispose only when the host is finished with that instance. Pending operations also need a live dispatcher to complete cancellation cleanup; dispose before shutting down the dispatcher.

`ActiveLayout` is -1 for model space or a source layout index. Layer/category visibility and `Monochrome` are per-view settings. Layout changes clear selection. Saved file visibility is applied on document assignment; `ShowAll()` and `ApplySavedLayerState()` are explicit host choices. Per-viewport frozen layers affect both drawing and picking.

`Camera` contains a finite positive scale and DIP offset. `WorldToScreen`/`ScreenToWorld` use the displayed model/paper space; pass a source viewport to convert model coordinates through a paper window. Measurements inside viewports remain in model units. Combining paper and model targets starts a new selection rather than measuring incompatible coordinates.

The native child window has the usual `HwndHost` airspace constraint: ordinary WPF controls cannot reliably overlay its drawing. Put host panels alongside it or use the viewer's rendered overlays. Its child HWND remains mouse-transparent so WPF receives input. Resize and DPI changes redraw using current dimensions; geometry uploads occur only on assignment or renderer recreation. Automated checks simulate a DPI notification; physical transitions between monitors with different DPI have not been validated in this session.
