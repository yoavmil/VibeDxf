# Single-package Stage 1: public contract and baseline

Subsequent scope change: the user declined Stage 3. Per-entity visibility and edited-snapshot/removal proposals below are historical and are not included in the current package. See the active plan for current scope.

Date: 2026-10-22. Stage 1 approved by the user's "continue to next step" following the single-package plan rewrite. Status: design/baseline complete; the contract was accepted by subsequent Stage 2 approval. This historical Stage 1 report changed no production code. See [Stage 2 results](NUGET-SINGLE-STAGE2-RESULTS.md) for the implemented extraction and current state.

This document belongs to [the active single-package plan](NUGET-PLAN.md). The NUGET-STAGE1/2/3 documents from the cancelled two-package extraction are historical and do not define this API.

## Deliverable and dependency map

One Windows/WPF library, proposed project/assembly/package name `VibeDxf.Wpf`, namespace `VibeDxf.Wpf`, target `net10.0-windows`, `UseWPF=true`. Package name availability and preview version remain Stage 4 decisions. No separate Core library or public source/prepared-document adapter.

```text
Desktop app --------> VibeDxf.Wpf
Windows console ---> VibeDxf.Wpf
WPF host ----------> VibeDxf.Wpf
Regression tests --> VibeDxf.Wpf

Within that one library:
ASCII reader -> block/layout expansion -> text outlines + geometry -> immutable document
immutable document -> distance queries / derived document removal
immutable document -> per-control view state -> internal renderer and native host
```

| Current source | Intended responsibility in the single library |
| --- | --- |
| Geometry.cs | Public immutable Curve, DxfEntity and measurement geometry; WPF Point/Vector/Rect retain double precision |
| DxfReader.cs | Internal mutable loading builder, expansion, layers/layouts, diagnostics and identity assignment; publish one fully prepared DxfDocument |
| SplineGeometry.cs, DxfHatch.cs | Existing spline/hatch algorithms, limits and tolerance; implementation stays internal unless required by the agreed API |
| DxfPrimitives.cs, DxfMText.cs, DxfMLeader.cs | Keep font shaping, contours, transforms, fills and annotations in the same loading pipeline; no text-provider interface required of consumers |
| MeasurementTarget.cs | Separate public document-based anchors/results from private hover/snap/viewport presentation state; retain existing analytic algorithms |
| DxfViewport.cs | Public DxfView control and private interaction/native-host implementation; retain mouse transparency and GPU lifetime rules |
| DrawingRenderer.cs | Internal GPU buffers, shaders, visibility tables, D3D/Direct2D/Vortice resources |
| MainWindow, LayerPanel, App, Assets | Remain application UI/resources; consume the library's public API |
| Tests and tools/DemoDrawing | Replace linked production sources with the compiled library reference; retain fixture/report infrastructure and narrow test instrumentation |

Project layout: add `src/VibeDxf.Wpf/`; retain `src/VibeDxf.csproj` as the desktop application with the existing version and installer path/identity. Exclude the nested library's sources/resources from app default globs. Existing three Vortice 3.8.3 references move to the library; use their transitive dependencies in consumers. No new production dependency is proposed. Stage 2 must not add package metadata/publication machinery prematurely.

## Loading and the prepared document

Proposed public signatures, not implemented code:

```csharp
public sealed class DxfDocument
{
    public static Task<DxfLoadResult> LoadAsync(
        string path, CancellationToken cancellationToken = default);
    public static Task<DxfLoadResult> LoadAsync(
        Stream source, bool leaveOpen = true,
        CancellationToken cancellationToken = default);

    public Guid DocumentId { get; }  // Source lineage, shared by edited snapshots.
    public Guid RevisionId { get; }  // Unique immutable snapshot identity.
    public string Units { get; }
    public IReadOnlyList<DxfEntity> Entities { get; } // Model only.
    public IReadOnlyList<DxfLayout> Layouts { get; }
    public IReadOnlyList<DxfLayer> Layers { get; }
    public IReadOnlyList<DxfEntity> AllEntities { get; } // Model, then sheets in tab order.
    public Rect Bounds { get; } // Model fit bounds; empty is valid after editing.
    public IReadOnlyList<DxfDiagnostic> LoadDiagnostics { get; }
    public DxfEntity? FindEntity(DxfEntityId id);
    // Measure / WithoutEntities are specified below.
}

public sealed record DxfLoadResult(
    DxfDocument Document, IReadOnlyList<DxfDiagnostic> Diagnostics);
```

- Both overloads prepare text, glyph/decorative contours, curves, bounds and fill triangles before returning. No viewer HWND, D3D device, GPU buffer or Application instance is constructed by the document loader.
- Callers may await loading from any apartment/thread. Internally a dedicated STA worker owns WPF text objects; those objects are disposed and do not escape. Published snapshots contain only immutable numeric/metadata data, not Typeface/TextFormatter/Geometry/DispatcherObject instances. Safe concurrent reading/querying depends on making every nested collection immutable, not merely exposing List as IReadOnlyList.
- Reading, expansion, shaping and geometry preparation run off the caller's dispatcher. Triangulation can retain its current pure parallel work. Check cancellation while reading records, expanding blocks, flattening splines, building hatches, between text operations and within triangulation. An individual WPF shaping call is not interruptible; cancel before publication if requested during it.
- Path overload owns/disposes its stream. Stream overload leaves the caller's stream open by default on success, failure and cancellation; `leaveOpen=false` transfers disposal responsibility. The caller must keep an asynchronously loaded stream alive and avoid concurrent access until the task completes. No renderer or UI thread is needed to release a completed document; its managed data needs no Dispose.
- Cancellation throws OperationCanceledException. Unreadable/malformed input throws an appropriate exception. Valid unsupported features remain structured diagnostics with code, severity, phase, kind, instance handle/path when known, message and aggregate count. Missing identity in aggregate warnings is explicit rather than fabricated.
- Keep current parser support and the current rejection when a load produces no supported drawable geometry. Blank/inkless text is omitted, as in the original app; this plan does not reintroduce the abandoned split's retained blank text records. A later edit may validly produce an empty document.
- Geometry is prepared once and shared across views and derived snapshots. No duplicate public geometry model or public preparation step. Pure document operations use the Windows Desktop runtime and WPF assemblies but do not initialize graphics; document-only operation without graphics is an implementation acceptance test, not a claim established by today's baseline.

### Read-only data and colors

Public DxfEntity exposes Id, Kind, LayerId/Layer, Handle, InstancePath, CoordinateContext, Category, IsAnnotation, Text, IsApproximate, Bounds and read-only Curves/FillContours/RegionContours/RenderCurves/RenderDots. Each polygon is also read-only; do not expose writable arrays through nested interfaces. Triangle caches remain internal, computed before publication. Constructors/setters that would permit arbitrary CAD entity creation stay internal.

Expose source handle separately from expanded instance path; preserve the existing combined Handle value during migration and add SourceHandle rather than changing existing identity text silently. Neither handle is an entity ID.

WPF Point, Vector, Rect and Matrix are numeric values and remain appropriate in this Windows-only API. DXF XY is y-up; curve angles/sweeps are radians; Matrix methods follow WPF's existing degree/composition conventions. Rays/xlines keep finite base-point fit bounds and unbounded measurement domains. Keep the current spline tolerance (0.0001 drawing units) and algorithm-specific tolerances.

Entity Color is resolved packed RGBA before dark-background adjustment; source color mode/index/true-color metadata must be retained. `DxfDisplayColors.ForDarkBackground(uint)` supplies the current appearance policy for renderer and host layer swatches. Saved on/frozen layer state is source metadata, distinct from view visibility. This is one internal policy separation, not a new package or public document representation.

## Stable identity and revision rules

```csharp
public readonly record struct DxfEntityId(Guid DocumentId, long Value);
public readonly record struct DxfLayoutId(Guid DocumentId, int Value);
public readonly record struct DxfCoordinateContext(DxfLayoutId? LayoutId);
// null LayoutId means model space.
```

Assign occurrence IDs once, after expansion, across model and all retained paper layouts. Values do not encode collection offsets. Every fresh file/stream load gets a new DocumentId, including loading the same file twice. IDs are process/document identities, not persistent IDs to serialize back into DXF.

Derived documents share DocumentId and unchanged entity/layout IDs but receive a new RevisionId; siblings may contain different subsets. An anchor from an older revision is accepted if that exact entity still exists in the queried snapshot and its midpoint remains valid. A removed entity returns EntityNotFound. An unrelated lineage returns ForeignDocument even if numeric Value/handle matches. Default/invalid IDs are rejected. This resolves "stale" targets through membership, without needlessly invalidating surviving anchors after a deletion.

FindEntity returns null for a nonmember/foreign ID; measurement and edit operations have stricter reporting below. Layout IDs stay stable; collections preserve tab order. Layer IDs are stable file-order indices within a lineage; callers access them through the assigned document. Private renderer group indices and compact GPU entity slots are not public identities.

## Programmatic distance API

```csharp
public readonly record struct MeasurementAnchor(DxfEntityId EntityId, int? MidpointSegment = null);
public enum MeasurementStrategy { Closest, Center }
public enum MeasurementStatus
{
    Success, ForeignDocument, EntityNotFound, InvalidAnchor,
    CoordinateMismatch, NoGeometry
}
public sealed record MeasurementResult(
    MeasurementStatus Status, ClosestPair? Pair, bool IsApproximate,
    DxfCoordinateContext? CoordinateContext)
{
    // Distance, DeltaX and DeltaY are nullable derived properties of Pair.
}

MeasurementResult DxfDocument.Measure(
    MeasurementAnchor first, MeasurementAnchor second,
    MeasurementStrategy firstStrategy = MeasurementStrategy.Closest,
    MeasurementStrategy secondStrategy = MeasurementStrategy.Closest);
```

The result has a pair/context only on Success. It never represents invalid targets as a zero distance. Status precedence: invalid/default/foreign ID, missing member, invalid midpoint/strategy, incompatible context, no usable geometry, then calculation. Validate enum values explicitly. These are expected query failures, not exceptions. Argument exceptions remain appropriate for structurally invalid API inputs such as a null batch.

Decisions:

- Whole-entity queries work for every loaded occurrence with measurable geometry, including annotation geometry. Interactive dimensions/leaders/multileaders/viewport frames remain nonselectable; document queries do not inherit that UI restriction. Annotation measurements are to the displayed prepared graphics, not semantic dimension values.
- Visibility is irrelevant to document queries. Hidden entities remain measurable until removed from the document being queried.
- Midpoints are exact finite, nondegenerate straight segments of eligible entities. Reject annotation, text, hatch, spline, POINT and any approximate/unbounded segment midpoint, matching current snapping policy. Curve indices are safe across deletion revisions because surviving geometry is unchanged.
- Center changes only a standalone ARC/CIRCLE with its one analytic circular curve. For other valid targets it falls back to Closest, preserving today's strategies; a nonuniformly transformed approximate circle has no fabricated exact center. An explicit midpoint takes precedence over strategy. POINT uses its exact location.
- Model targets seen through different viewports still share model coordinates. A paper entity and model entity, or paper entities from different sheets, yield CoordinateMismatch. Signed deltas run from first to second. A viewport association belongs only to a selection overlay.
- Reuse existing filled-region containment and analytic curve algorithms. Spline geometry retains its bounded approximation; glyphs and nonuniform curve transforms remain approximate without inventing a certified font-outline error bound. Endpoint locations/deltas need not be unique for tied closest pairs. Traversal/curve ordering remains deterministic.
- Queries read immutable data and need no viewer/dispatcher/GPU. Do not promise an indexed or constant-time distance API: the existing pairwise curve algorithm can be expensive for detailed text. Performance work beyond preserving current behavior needs evidence before expanding scope.

## Visibility contract and renderer changes

Public methods on DxfView:

```csharp
void SetEntityVisible(DxfEntityId entityId, bool visible);
bool IsEntityEnabled(DxfEntityId entityId); // Entity toggle only.
bool IsEntityVisible(DxfEntityId entityId, DxfEntityId? viewportFrameId = null);
void ResetEntityVisibility();
void SetLayerVisible(int layerId, bool visible);
void SetLayersVisible(IEnumerable<int> layerIds, bool visible);
void SetCategoryVisible(EntityCategory category, bool visible);
void ShowAll();
void ApplySavedLayerState();
void IsolateLayer(int layerId);
```

Effective visibility is entity enabled AND layer enabled AND category enabled AND not viewport-frozen. IsEntityVisible evaluates that mask, not geometric occlusion or whether it lies inside the camera/frame. For a model entity on a sheet, pass the active sheet's viewport-frame ID; without it the query reports global masks. Validate that a supplied viewport belongs to the active layout and that its target is a model entity. Invalid/foreign/nonmember IDs throw ArgumentException for these imperative view operations rather than silently changing unrelated state.

All entities start enabled. ShowAll enables all layers/categories/entities, while viewport freezes still apply. ResetEntityVisibility only clears entity overrides. ApplySavedLayerState changes layers only; IsolateLayer changes layers only. Per-entity overrides and category choices remain independent unless ShowAll is requested. Monochrome changes color policy only.

Hiding a model entity hides all its projected appearances in that viewer. Hiding a viewport frame changes the frame stroke only; the window's model content stays visible, as with today's frame-category toggle. To remove the whole window use document removal. Hiding a selected entity removes its selection/snap and recalculates measurement; event snapshots reflect the final state.

Implementation direction for Stage 3: retain the layer/category group table and add a compact per-view entity-enabled table. Carry an entity slot in each line/fill/point/dot/ray/arc vertex or instance and apply both masks in every shader path. Viewport-frozen layer masks continue to use the existing group mechanism. Stable public IDs map to private compact slots. Update only these small tables for visibility; never rebuild or reupload geometry for hide/show, pan/zoom, layout switching or monochrome. Validate slot/table size limits and all primitive paths. Avoid making every entity a separate draw call or overloading public layer groups as identity.

## Removal contract

```csharp
DxfDocument DxfDocument.WithoutEntities(IEnumerable<DxfEntityId> entityIds);
```

- Validate/materialize the whole batch before producing a result. Duplicates collapse. A foreign, default or already-absent ID throws ArgumentException and no partial result is produced. Empty input returns the same snapshot. A nonempty successful batch creates a new RevisionId while retaining DocumentId.
- Remove the named expanded occurrences only. One INSERT child does not remove siblings or other insertions. One child of a DIMENSION expansion does not delete the whole dimension; MULTILEADER is currently one combined entity, so removing it removes that occurrence's lines/arrows/text together. No implicit annotation/block-group cascade.
- Every loaded viewport definition has an explicit FrameEntityId linking it to the generated paper-space frame. Removing that frame removes its associated viewport definition/window in the derived snapshot; it never removes the model entities that window displayed. Other frames/windows/layouts remain. The link must be assigned during parsing, not inferred from possibly duplicate handles.
- Keep layouts/layers in their original stable order, even when emptied by editing. The parser's initial unused-default-layout filtering still applies at load only; editing must not silently drop/renumber sheet identities. Recompute entity counts, per-layer/category counts and model/paper bounds from survivors plus remaining viewport rectangles. Retain source layer/color metadata.
- Reuse survivor immutable geometry and prepared glyphs/triangles. Removal requires no font shaping, native resources or dispatcher. It is safe to create an edited snapshot while another thread/view uses the original. Empty documents are valid; fitting them uses a finite neutral camera.
- LoadDiagnostics remain source-load history. Current counts and Contains/Find/Measure operate on active survivors. A retained original can be reassigned to restore content; no undo manager or filesystem action is introduced.
- Installing an edited revision clears selections, measurements and snap indexes; rebuilds the affected renderer ranges once; releases previous geometry allocations after successful replacement. Surviving entity visibility, layer/category state, monochrome, active layout and camera are preserved for the same lineage. New unrelated documents initialize saved layer state, default entity/category masks and fitted camera, matching ordinary file-open behavior. Strategies remain control preferences. Restored entities from an older same-lineage snapshot start enabled if their overrides were pruned on deletion; document this rather than keeping hidden tombstone state.

## Public viewer and events

```csharp
public sealed class DxfView : /* WPF control */, IDisposable
{
    public DxfDocument? Document { get; } // Read-only dependency property.
    public Task SetDocumentAsync(DxfDocument document, CancellationToken cancellationToken = default);
    public Task<DxfLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
    public void ClearDocument();
    public void ClearSelection();
    public void FitToDrawing();
    public void Select(MeasurementAnchor anchor, DxfEntityId? viewportFrameId = null);
    public IReadOnlyList<DxfSelection> Selected { get; }
    public MeasurementResult? Measurement { get; }
    public MeasurementStrategy FirstMeasurementStrategy { get; set; } // Dependency property.
    public MeasurementStrategy SecondMeasurementStrategy { get; set; } // Dependency property.
    public DxfLayoutId? ActiveLayout { get; set; } // Dependency property; null = model.
    public bool Monochrome { get; set; } // Dependency property.
    public DxfCamera Camera { get; set; }
    public Point WorldToScreen(Point world, DxfEntityId? viewportFrameId = null);
    public Point ScreenToWorld(Point screen, DxfEntityId? viewportFrameId = null);
    // Visibility methods and EventHandler<T> events specified in the text.
}
public sealed record DxfCamera(double Scale, Vector Offset);
public sealed record DxfSelection(MeasurementAnchor Anchor, DxfEntity Entity, DxfEntityId? ViewportFrameId);
```

Camera scale is finite and positive within the existing safe zoom limits; offsets and converted coordinates must be finite. Screen units are control-relative DIPs; model/paper coordinates remain y-up. Viewport conversion uses only a validated source window of the active sheet. Programmatic selection obeys interactive visibility/annotation/context policy and toggles selections as today; a third target starts a new pair, and mixing paper/model resets the pair rather than creating an invalid interactive measurement. Invalid programmatic selection throws ArgumentException.

Events: DocumentChanged (old/new snapshot), SelectionChanged (read-only ordered selections), MeasurementChanged (current nullable result), VisibilityChanged (read-only layer/category/entity settings), LayoutChanged (old/new layout IDs). Payloads carry the final Document/RevisionId so hosts can associate events with state. Strategy changes recalculate without reselection. Raised on the owning dispatcher after state commit; no halfway-loaded document is published. Publish a new read-only selection/settings snapshot rather than exposing mutable backing collections. A completed assignment publishes document change, then selection/measurement, layout and visibility notifications; hosts should inspect payload state rather than rely on event handlers mutating the control during publication.

SetDocumentAsync receives already prepared immutable data; it does not shape text again. Prepare candidate GPU buffers before releasing the current set; if allocation fails, retain the previous document/resources and fail the task. A control not yet loaded can commit CPU data and defer upload until renderer creation. ClearDocument cancels pending loads/assignments, releases old drawing geometry, clears source references and interaction state, and emits changes. Same-snapshot reassignment can be a no-op; callers use ClearSelection/Fit explicitly.

Viewer LoadAsync is a convenience around the same DxfDocument loader plus assignment. New load/assignment/clear/dispose invalidates older generations across both steps. Cancellation is checked before GPU installation and before publication; an individual driver call is not interruptible. Discarded candidate allocations must be disposed. Explicitly invoke the dispatcher for installation rather than assuming a caller captured a synchronization context. Device loss after a successful commit is a renderer error, not guaranteed automatic recovery; error delivery must be host-visible (a RenderingFailed event with the exception), without a library MessageBox.

Construct and mutate the control on its WPF STA dispatcher. Unload releases device resources while preserving the source snapshot/view settings; reload recreates them. Dispose permanently cancels work, clears source references and prevents recreation; it is idempotent and dispatcher-affine. Hosts should dispose before shutting down the dispatcher. Two controls share only immutable data, never native handles or device/visibility state. HwndHost airspace still prevents ordinary WPF overlays over the native surface; host controls belong alongside it or in rendered overlays.

## Baseline evidence

Production source, tests, demo and solution match `8722a427be68133e156e9ddd17a3cf33b27ce1d0` (`git diff --quiet 8722a42 -- src Tests tools VibeDxf.sln` returned 0). Current Git HEAD remains extraction checkpoint `77ac185`; its reversal and planning changes are uncommitted. Do not mistake HEAD's historical split code for the current working-tree baseline.

Commands executed in this Stage 1 (no restore/network required):

```powershell
dotnet build VibeDxf.sln -t:Rebuild -c Release --no-restore --disable-build-servers -p:OutputPath=bin/SinglePackageStage1/ -p:UseAppHost=false
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore --disable-build-servers -p:OutputPath=bin/SinglePackageStage1Tests/ --logger "trx;LogFileName=single-package-stage1.trx"
```

- Full rebuild succeeded: 0 errors, three reports of the existing CS0108 warning (`DxfViewport.IsVisible(DxfEntity)` hides UIElement.IsVisible) across app temporary/final markup compilation and linked test compilation. Initial incremental build was also successful; its 0-warning output was not evidence of a warning-free rebuild.
- xUnit: **217 executed, 217 passed, 0 failed, 0 skipped**, duration 38 seconds. Named discovery/catalog completeness, GPU upload/pixels, selection, text, hatch, block, layout and visibility checks ran. No new production code or tests were added in this design stage.

Actual public-fixture output from this run's TRX:

| Drawing | Model entities | Splines | Skipped | Paper layouts (entities/viewports) |
| --- | ---: | ---: | --- | --- |
| api-cw750-details.dxf | 4,313 | 0 | 3DSOLID=3; VIEWPORT (3D view)=1 | Sheet 1:63/2; Sheet 2:73/1; Sheet 3:74/2; Sheet 4:62/2 |
| uncommon.dxf | 148 | 1 | 3D POLYLINE=2; 3DSOLID=1; MESH=1 | None retained |

Model-kind totals: api-cw750 has LINE=3278, LWPOLYLINE=296, HATCH=73, CIRCLE=58, SOLID=42, MTEXT=322, ARC=75, MULTILEADER=58, ATTRIB=43, TEXT=57, LEADER=11. uncommon has LINE=59, MTEXT=35, SPLINE=1, SOLID=21, CIRCLE=1, LWPOLYLINE=7, ARC=10, MULTILEADER=2, RAY=3, POINT=6, ATTRIB=1, XLINE=2. These describe supported expanded/renderable occurrences, not complete support for either drawing.

TRX: `Tests/TestResults/single-package-stage1.trx`. Generated report: `artifacts/test-report/index.html`. Preserved a separate comparison copy, including adjacent images and TRX, in `artifacts/single-package-stage1/visual-report/index.html` and `artifacts/single-package-stage1/single-package-stage1.trx`. Visually inspected real GPU captures `frame-111.png` (Sheet 1, title block and model windows) and `frame-166.png` (uncommon geometry/text/annotations); selected-spline captures are also retained. This is representative inspection plus numerical/GPU assertions, not a pixel-perfect commercial-renderer reference or performance benchmark.

## Release and license inventory

Inspected the existing local `.github/workflows/release.yml`: pushes to main changing `packaging/**` or that workflow, `v*` tags and manual dispatch can run the Windows app-release job. It builds/tests/packages, can create/push an annotated tag, creates or updates a draft release/assets and publishes it as latest. No workflow/packaging changes were made. NuGet CI/publication must be separate and explicitly approved in later stages.

No root project LICENSE was found. THIRD-PARTY-NOTICES licenses named dependencies only; public fixtures carry their own adjacent dxf-parser/ezdxf notices. A source-license choice remains required before package distribution, not a blocker to local design/extraction. No license was selected, package ID availability checked, dependency versions changed, package packed, commit/tag created or publication performed here.

## Acceptance and remaining risks

Stage 1 delivers the requested dependency map, concrete contract, settled editing/annotation/visibility policies and fresh baseline. Stage 2 approval accepts this contract or amends it before implementation. Entity visibility and removal implementation remain Stage 3; their types can be planned now without shipping placeholders that pretend to work in Stage 2.

Principal risks: deep freezing of existing mutable lists; preserving original text transforms and fixture counts; exposing raw colors while retaining appearance; assigning viewport frame links and stable identities; making generation/cancellation and candidate GPU replacement atomic; adding entity-slot filtering to every shader path without hurting buffer reuse. Derived-document behavior and document-only operation without graphics have a defined acceptance test but are not implemented/tested in this baseline. No physical multi-monitor DPI, independent package consumption, installer change, runtime portability or new performance claim is established by Stage 1.
