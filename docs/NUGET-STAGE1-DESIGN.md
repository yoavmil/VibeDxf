> Archived: the user cancelled the two-library extraction on 2026-10-22. Its code has been reverted to the original single-project application. This document describes the abandoned implementation, not current APIs.

# NuGet extraction: Stage 1 design

Historical design/baseline. Subsequent Stage 2 approval accepted this direction, and Stages 2-3 are complete. See [Stage 3 results](NUGET-STAGE3-RESULTS.md) for the implemented contract and current state.

Date: 2026-10-22. Source baseline: 8722a427be68133e156e9ddd17a3cf33b27ce1d0.
Status: proposed contract for review; no implementation changes. Approval to execute Stage 1 was the user's "go" after the staged plan. Approval of Stage 2 is still required.

## Current dependency map

| Source | Current coupling | Destination |
| --- | --- | --- |
| Geometry.cs, SplineGeometry.cs | WPF Point/Vector/Rect; entity mixes geometry, display curves and triangle cache | Core numeric geometry and document data; prepared presentation data separated |
| DxfReader.cs | WPF Matrix; directly calls text shaping; expands blocks, dimensions and leaders; triangulates on loading thread | Core parser/expansion; retain deferred text descriptions |
| DxfPrimitives.cs | Pure triangulation/transforms/decoding mixed with FormattedText/Typeface outline extraction | Pure parts in Core; font shaping in Wpf |
| DxfHatch.cs | WPF numeric types; hatch boundary/pattern construction | Core with independent double-precision types |
| DxfMText.cs | TextFormatter, font layout, outlines and DXF formatting parsing mixed together | Split semantic parsing in Core from WPF shaping/layout |
| DxfMLeader.cs | Delegates embedded MTEXT to the current shaping pipeline | Core leader geometry and deferred text; Wpf shapes its text |
| DxfColors.cs | ACI/BYLAYER/BYBLOCK resolution mixed with dark-background lightening | Core preserves color semantics; Wpf chooses display colors |
| MeasurementTarget.cs | Distance math, midpoint indexing and paper-display transforms mixed | Core geometric targets/distance; per-view selection/context and hover index in Wpf |
| DrawingRenderer.cs | Vortice, HWND, WPF numeric types, shader compilation and device resources | Wpf internal renderer |
| DxfViewport.cs | WPF control, native host, camera, visibility, GPU ownership, selection and overlays | Wpf public control; keep native/device details internal |
| MainWindow, LayerPanel, App | Menus, dialogs, panels, application resources | Existing application |
| Tests and tools/DemoDrawing | Explicit Compile links duplicate application sources; GPU tests use reflection and STA threads | Compiled project references; retain targeted internal access only where necessary |

Current graph: app, tests and demo each compile much of the same source. Desired graph: app -> Wpf -> Core; Core tests -> Core; Wpf tests -> Wpf/Core. Independent package consumers must restore nupkg files, not compile repository sources.

## Projects and framework targets

- Add src/VibeDxf.Core/VibeDxf.Core.csproj targeting net10.0, without UseWPF or Windows APIs. Namespace VibeDxf.Core, numeric types in VibeDxf.Core.Geometry.
- Add src/VibeDxf.Wpf/VibeDxf.Wpf.csproj targeting net10.0-windows with UseWPF; retain the existing Vortice 3.8.3 references. Namespace VibeDxf.Wpf.
- Retain src/VibeDxf.csproj as the app to avoid gratuitous installer/workflow path changes. App namespace can stay VibeDxf.
- Core supports the .NET 10 runtime family, not older frameworks in the first preview. A plain console consumer needs the normal .NET runtime, not Windows Desktop. Cross-platform Core is an architectural goal; do not claim Linux/macOS runtime validation until tested there.
- Two packages share a preview version, chosen later after checking NuGet ID availability. Do not change the current app version during extraction.

## Numeric and data contracts

Use purpose-specific double-precision Point2D, Vector2D, Bounds2D and Affine2D types. Do not use float-based Vector2 as authoritative geometry. Convert to float only for GPU buffers after subtracting the local origin.

Coordinates remain DXF XY/y-up. Angles are radians, positive counter-clockwise; signed arc sweeps retain their current meaning. Define Affine2D.Then(next) as next(this(point)) and test it with translation, rotation, scale, mirroring, inverses and model-to-paper mapping. Bounds2D has explicit Empty and zero-size states; infinite lines retain finite base-point bounds for fitting, with an independent IsUnbounded flag. Preserve the current spline tolerance of 0.0001 drawing units and existing algorithm-specific numeric tolerances during extraction; do not introduce a global epsilon that changes results.

Expose read-only document/entity/layer/layout collections, built internally before publication. Do not claim deep immutability while exposing mutable arrays or Lists. Stable entity IDs are document-local; preserve source handles plus block instance paths. Entity counts refer to expanded instances, not source records. A loaded document can be shared by viewers; selection, visibility, camera and GPU resources cannot be stored on it.

Core preserves layer on/frozen states and viewport frozen-layer references. Store raw color mode/index/true-color information plus inherited resolution as needed; ACI 7 remains a theme-sensitive color. Dark-background brightening and monochrome are viewer policies. Preserve today's appearance through default viewer settings.

## Loading and diagnostics API sketch

Names below are proposals, not implemented signatures:

```csharp
DxfLoadResult DxfReader.Load(string path, DxfLoadOptions? options = null);
DxfLoadResult DxfReader.Load(Stream source, DxfLoadOptions? options = null);
Task<DxfLoadResult> DxfReader.LoadAsync(
    string path, DxfLoadOptions? options = null,
    CancellationToken cancellationToken = default);
```

DxfLoadResult contains Document and immutable Diagnostics. A diagnostic has a stable code, severity, phase, entity kind, source/instance identity where available, message and count. Keep unsupported records distinct from supported-but-approximate rendering and deferred presentation. The app formats these as user-facing warnings; Core never opens dialogs. Preserve existing protective expansion/segment limits. Cancellation must be checked during reading, expansion and expensive geometry work, not only before starting a background task. Path overload owns its stream; stream overload leaves the caller's stream open by default with an explicit documented ownership option. Malformed/unreadable input remains an exception; unsupported valid features produce diagnostics.

A document containing only supported text is valid even before text outlines exist. Core bounds for unprepared text are explicitly incomplete, not fabricated as exact. Fit in Wpf uses prepared bounds.

## Text and geometry preparation boundary

This is the principal extraction risk. A direct move of DxfMText to Wpf while the Core reader still invokes it would create a dependency cycle.

1. Core parses TEXT/ATTRIB/MTEXT semantics: original content, formatting tokens, placement/alignment, wrapping reference, font request, height/width/tracking/slant, paragraph/fraction data and composed instance transform. Dimension/leader text follows the same path.
2. Preserve the operation order: shape text in its local text coordinates, then apply placement and block transforms exactly once. Nonuniform scaling and mirroring must not be collapsed into a single rotation/height descriptor. Retain block paths and annotation category for deferred text. Preserve the existing embedded MTEXT metadata rules.
3. Wpf prepares glyph outlines with the current Arial substitution and WPF engines, using STA-affine workers where required. Do not run TextFormatter arbitrarily on thread-pool threads. Preparation precedes GPU upload and geometric text picking.
4. Core owns a presentation-independent GeometrySnapshot contract mapping entity IDs to curves, filled regions, approximation information and bounds. Core algorithms operate on such snapshots; Core constructs them directly for non-text geometry. Wpf supplies prepared text geometry using those same neutral types.
5. A Core-only consumer can measure supported non-text entities immediately. Text-outline distance requires an explicit prepared snapshot supplied by a text implementation; if absent, return GeometryNotPrepared with a diagnostic, never zero, a bounding-box substitute or silently skipped text. Wpf preserves today's text measurement behavior using its prepared snapshot.
6. Fill triangulation remains a pure Core utility. Keep CPU preparation off the UI thread where safe and preserve ordering/determinism. Triangle caches are derived data, not mutable fields on shared source documents. GPU buffer layouts stay internal to Wpf.

Text descriptors are semantic input, not an attempt to support previously unsupported fonts/features. Existing unsupported modes retain diagnostics. Compare placements and outlines for single labels, rotation/mirrors, nested inserts, fractions and the public drawings before considering this boundary complete.

## Measurement contract sketch

```csharp
MeasurementResult Measure(
    GeometrySnapshot geometry,
    MeasurementAnchor first, MeasurementAnchor second,
    MeasurementStrategy firstStrategy, MeasurementStrategy secondStrategy);
```

MeasurementAnchor identifies an entity or a finite straight-segment midpoint. Strategies remain Closest/Center; Center only affects standalone ARC/CIRCLE targets. Results include endpoints, signed DeltaX/DeltaY (second minus first), Euclidean Distance, approximation status and success/failure reason. Absolute axis components may be derived by consumers. Preserve filled-region containment and exact ray/xline distance semantics.

CoordinateContext identifies model space or a particular paper layout. Different viewport windows into the same model still measure in model units; the window association controls overlay placement/clipping, not the mathematical coordinate system. Mixing model and paper, or different paper layouts, is rejected/reset as today. Wpf's selection target carries its view-window association separately from the Core geometric anchor. Annotations remain display-only under existing policy.

## Viewer API sketch

Expose a DxfView WPF control; prefer dependency properties for bindable state and EventHandler<TEventArgs> for notifications. Keep DrawingRenderer, native handles, shader buffers and Vortice interfaces internal rather than freezing the current low-level renderer API.

```csharp
await view.SetDocumentAsync(document, cancellationToken);
view.FitToDrawing();
view.FirstMeasurementStrategy = MeasurementStrategy.Center;
view.SecondMeasurementStrategy = MeasurementStrategy.Closest;
view.SetLayerVisible(layerId, false);
view.SelectionChanged += OnSelectionChanged;
view.MeasurementChanged += OnMeasurementChanged;
```

Additional contract: read-only selection snapshot, active layout ID, visibility/theme settings, ClearSelection, layer/category filtering, saved-state reset, camera access and screen/world conversion with explicit coordinate context. SetDocumentAsync stages text/preparation and commits the new document only on success; define whether to retain the old drawing on failure (proposed: retain it). A generation token prevents a slower old load replacing a newer document. Event payloads include current state so hosts need not query internal fields. Strategy changes immediately recalculate the measurement without reselection.

Document assignment, dependency properties and events belong to the UI dispatcher. A control instance owns its prepared view state and GPU resources; source documents are shareable. Unload releases native/device resources and reload can recreate them. Pending operations are cancelled on disposal/replacement. No static device or view state may couple two controls. Document replacement and a public ClearDocument operation must release prior allocations. HwndHost airspace limits WPF overlays; keep overlays in the renderer or place host controls beside the viewport as the existing app does.

The host owns file dialogs, drag/drop policy, menus, panels and message boxes. Provide a minimal sample of host-side loading rather than hard-coding a file dialog into the control.

## Tests, packaging and release risks

- Migrate linked-source tests to project references. Use narrow InternalsVisibleTo for device instrumentation if unavoidable; public integration tests must exercise the package API. Do not expose private renderer fields only to preserve reflection tests.
- Preserve xUnit/VS discovery, STA rendering execution, offline fixtures and HTML screenshots. Split pure tests from Windows/GPU tests; run a plain Core consumer independently.
- Keep exact fixture/skipped counts, but migrate representation-sensitive assertions deliberately: deferred text stays in the source document and expanded-instance counts must still be explainable.
- No root project LICENSE was found. THIRD-PARTY-NOTICES explicitly licenses dependencies only. User must choose the project's license before public distribution; preserve existing commit attribution, including Claude co-author trailers. GitHub contributor attribution is separate from this license decision. Do not add a license unilaterally.
- Keep test fixture provenance/notices; do not put reference drawings into runtime NuGets.
- Existing release workflow fires on main changes under packaging/ or .github/workflows/release.yml, pushed v* tags and manual dispatch. It can create tags/releases. Stage 1 edits docs only; CI/package publication changes require later approval. Do not repurpose app tags or overwrite published packages.

## Decision status

Proposed defaults: exactly two packages; Core net10.0, Wpf net10.0-windows; double precision; read-only document data; prepared text geometry supplied by Wpf; same-repository sample and versioning. User approval of Stage 2 should explicitly accept or amend these defaults. The source license remains a required user decision before publication, not a blocker to this design stage. No NuGet ID availability or cross-platform runtime claim has been verified in Stage 1.

## Stage 1 baseline results

Completed 2026-10-22 against commit 8722a427be68133e156e9ddd17a3cf33b27ce1d0; only untracked planning documents and the existing .claude directory were present before this work. No production code changed.

Commands (separate output directories avoid a running app's executable lock):

```powershell
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore -p:OutputPath=bin/Stage1Baseline/ --logger "trx;LogFileName=stage1-baseline.trx"
dotnet build src/VibeDxf.csproj -c Release --no-restore -p:OutputPath=bin/Stage1Baseline/ -p:UseAppHost=false
```

- xUnit: 217 passed, 0 failed, 0 skipped; reported test duration 39 seconds.
- App build: succeeded, 0 errors, 1 existing CS0108 warning at DxfViewport.cs:133 (IsVisible hides UIElement.IsVisible). Test compilation reports the same warning because it compiles linked sources.
- Public fixture actual outputs from TRX: api-cw750-details.dxf has 4,313 model entities, no splines, 3DSOLID=3 and VIEWPORT (3D view)=1 skipped; layouts Sheet 1=63 entities/2 viewports, Sheet 2=73/1, Sheet 3=74/2, Sheet 4=62/2. uncommon.dxf has 148 model entities, 1 spline, 3D POLYLINE=2, 3DSOLID=1 and MESH=1 skipped, no retained layouts. GPU-upload assertions passed.
- TRX: Tests/TestResults/stage1-baseline.trx. HTML: artifacts/test-report/index.html; regenerated with 217 passing checks and actual GPU readbacks. These generated artifacts are not package contents. This baseline verifies automated rendering assertions and captures; it does not establish pixel-perfect commercial-renderer equivalence or a measured performance benchmark.
- No runtime cross-platform tests, new dependencies, code moves, package builds, Git commits, tags or publication performed in Stage 1.

Stage 1 exit criteria met for a proposed design: the dependency graph is acyclic and deferred text/measurement preparation has an explicit boundary. User acceptance of the proposed contract is pending with Stage 2 approval. Main effort risk remains semantic extraction of MTEXT and exact preservation of transform order. Initial estimate is unchanged, subject to findings during implementation.
