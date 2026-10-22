# Plan: one reusable Windows/WPF DXF package

Date: 2026-10-22. Status: Stages 1-2 complete; Stage 3 explicitly declined by the user. Stage 4 complete; see [Stage 4 results](NUGET-SINGLE-STAGE4-RESULTS.md). Selected MIT license and VibeDxf.Wpf 0.2.0-preview.1; desktop app stays 0.2.0.

This replaces the cancelled [two-package extraction plan](NUGET-SPLIT-PLAN.md). The user requested one library that keeps text shaping and geometry together, and requested programmatic entity traversal and distance queries. The user subsequently declined Stage 3: per-entity visibility and in-memory removal are excluded from the current package. The user subsequently approved Stage 1 with "continue to next step". Its [contract and fresh baseline](NUGET-SINGLE-STAGE1-DESIGN.md) were accepted with subsequent Stage 2 approval (Hebrew-layout "continue"). The one-library extraction is complete. The user declined Stage 3 and explicitly approved Stage 4; do not implement the withdrawn editing/entity-visibility proposals.

## Product and boundaries

Deliver one public NuGet, approved ID `VibeDxf.Wpf`, containing the ASCII DXF loader, document/entity model, double-precision geometry, text shaping, measurements and embeddable viewer. The user selected MIT and package version 0.2.0-preview.1 during Stage 4. A public registry lookup returned HTTP 404 for the package ID on 2026-10-22; availability is not a reservation. Keep the existing desktop application in the same repository as a host of that library.

| Component | Responsibility |
| --- | --- |
| One library project | Loading, prepared text and geometry, document inspection, distance queries, viewer/rendering and per-view state |
| Existing desktop application | File dialogs, drag/drop, menus, layer panel, status/warning presentation, installer and application settings |
| Regression tests and independent sample | Verify existing behavior and demonstrate consuming the single package through public APIs |

Initial target: `net10.0-windows`, WPF, with the existing Vortice 3.8.3 dependencies. Windows Desktop runtime is required even for a console caller using the document API. No separate Core project, platform-independent geometry layer or cross-platform runtime promise. Public WPF numeric types may be used where they preserve today's double precision; GPU float conversion remains internal.

Primary capability: embed a DXF viewer with pan/zoom, model/layout switching, layer controls, picking, midpoint snapping and element-to-element measurement. Programmatic document operations are first-class features of the same package; they must work without creating a viewer window or graphics device, although loading text uses WPF on an STA worker.

Keep current supported entities, text substitution, spline tolerance, layout transforms and protective parsing limits. DXF writing, DWG, other UI frameworks, arbitrary CAD editing, undo/redo, font fidelity improvements and new entity support are outside this plan. Entity removal/editing and per-entity visibility are excluded at the user's request.

## Current public contract

The implemented API covers loading, traversal, measurement and viewer/layer/category controls. See [integration](NUGET-SINGLE-INTEGRATION.md) and [independent consumers](../samples/PackageConsumers/README.md). Historical Stage 1 editing/entity-visibility sketches were declined and are excluded.

```csharp
var result = await DxfDocument.LoadAsync(path, cancellationToken);
var document = result.Document; // Includes prepared text geometry.

foreach (var entity in document.Entities) // Model space.
    Console.WriteLine($"{entity.Id}: {entity.Kind}, {entity.Layer}, {entity.Handle}");
foreach (var layout in document.Layouts)
    foreach (var entity in layout.Entities) { /* paper-space entity */ }

var measurement = document.Measure(
    new MeasurementAnchor(first.Id),
    new MeasurementAnchor(second.Id),
    MeasurementStrategy.Closest,
    MeasurementStrategy.Center);

await viewer.SetDocumentAsync(document, cancellationToken);
viewer.SetLayerVisible(layerId, false);

```

The viewer also offers a convenience loader, `await viewer.LoadAsync(path, token)`, using the exact same loader/preparation pipeline. Host code must still be able to obtain the loaded document and structured diagnostics. File selection remains the host's responsibility.

### Traversal and identity

- Expose read-only collections for model entities, layouts, paper entities, layers and viewport definitions. Entity inspection includes kind, document-local ID, original handle, expanded block-instance path, layer/category, source color, geometry, bounds and approximation information.
- Preserve the original double-precision coordinate conventions and explicit model/paper context. Traversal enumerates expanded entity occurrences, matching today's viewer, not editable block definitions or raw DXF records. A model entity seen through several viewports is one entity; its projected appearances are not separate document entities.
- Entity IDs identify occurrences in an immutable loaded document. Distinguish unrelated documents even if their numeric IDs match; reject foreign/stale targets explicitly. Do not identify entities solely by handles or list positions.
- Source collections are not writable lists. A document is a prepared, shareable snapshot; per-view selection, visibility, camera and GPU state are never stored on it.

### Programmatic measurements

- Use the same prepared geometry for code queries, picking and interactive measurements. TEXT/MTEXT/ATTRIB outlines are ready when loading completes; no second public geometry provider or deferred-text preparation API is required of consumers.
- Results include success/failure status, closest endpoints, Euclidean distance, signed delta X/Y (second minus first), coordinate context and approximation information. Do not return zero for an invalid, missing, foreign or geometry-less target.
- Preserve analytic line/arc/ray/xline calculations, filled-region containment and the 0.0001-unit spline approximation bound. Prepared glyph measurements remain approximate and use the existing Arial substitution.
- Support whole entities and eligible exact straight-segment midpoints. Center applies only to standalone ARC/CIRCLE entities. Spline/glyph tessellation must not introduce artificial midpoint targets.
- Different model viewport appearances still measure in model units. Reject model/paper or different-paper-layout pairs. An optional viewport association affects display overlays only.
- Interactive annotations remain display-only. Programmatic whole-entity queries can measure prepared annotation graphics; invalid/geometry-less targets return explicit statuses.
- Viewer visibility affects interactive picking but does not prevent a host from measuring an existing hidden entity by code.

### Visibility and excluded editing

The package supports per-view layer/category visibility, saved-layer-state reset, monochrome and per-viewport frozen-layer rules. Hidden geometry remains available for document queries while being excluded from interactive picking/selection. Visibility changes preserve geometry buffers.

The user explicitly declined Stage 3. Per-entity visibility, entity removal/derived edited snapshots, undo/redo and DXF writing are not part of the current package. Historical Stage 1 sketches describe those discarded proposals but do not authorize implementation.

### Viewer and resource ownership

- Public control exposes document assignment/loading, selection and measurement event snapshots, clear selection/document, fit, layout, camera, coordinate conversion and visibility APIs. Prefer dependency properties for bindable settings and `EventHandler<T>` for events carrying state.
- Host owns all dialogs and warning presentation. Keep native HWNDs, Vortice interfaces, GPU buffers and renderer classes internal.
- Document loading/preparation runs on a controlled STA worker and completes only after glyphs/bounds are usable. No window or GPU creation is required for document-only traversal and measurement. Specify path/stream ownership, cancellation checkpoints and structured unsupported/approximation diagnostics.
- Control access and final assignment occur on its dispatcher. New assignments cancel/supersede old ones; retain the old document on cancellation/preparation failure. Specify behavior for GPU allocation failure and device loss without claiming unimplemented recovery.
- Unload releases device resources; reload recreates them. Clear releases prior drawing allocations; dispose cancels pending work and permanently releases resources. Document two independent viewers and `HwndHost` airspace limitations.

## Approval and execution rules

Ask for explicit approval before each implementation stage and wait for a reply. The prior "ok, proceed" authorized the rewrite, and the subsequent "continue to next step" authorized Stage 1. Stage 2 was subsequently approved by Hebrew-layout "continue". Stage 3 was explicitly declined; Stage 4 was approved with "do stage 4". Do not reuse approvals from the cancelled extraction. Report delivered changes, validation and remaining risks after each stage. Material scope expansion requires a new decision.

Stage approvals do not independently authorize commits, pushes, tags, license selection, package publication or application releases. Those require explicit authorization. Do not rewrite Git history to hide the cancelled work.

## Stage 1 — finalize the single-package API and baseline

Approval: user said "continue to next step" on 2026-10-22. Status: complete. See [single-package Stage 1 design/baseline](NUGET-SINGLE-STAGE1-DESIGN.md): 217 checks passed; full rebuild passed with existing CS0108 warnings; production code unchanged. Contract acceptance accompanies Stage 2 approval.

- Review the sketches above against current source dependencies, text preparation, linked-source tests/demo and document mutability. Keep the implementation in place during this design stage.
- Settle namespace/type names, load/measurement signatures, document identity/revisions, removal semantics (especially viewport frames and expanded children), annotation query policy and visibility precedence. Present actual API sketches with failure cases.
- Specify which document data is immutable, how prepared text is shared safely across threads/views, and what reading/querying without a control requires. Avoid a redundant public source/prepared-document split.
- Record build, all original xUnit/GPU checks, exact public-fixture loaded/skipped/layout counts and representative visual captures. The restored baseline previously passed 217 cases; rerun and record actual results rather than assuming that number.
- Map current release triggers and confirm license status. A source license has not been selected; do not invent one.

Exit: reviewable contract and dependency map for one Windows/WPF library, accepted before code extraction.

## Stage 2 — extract one library and migrate the existing app

Approval: user typed בםמאןמוק ("continue" in Hebrew keyboard layout) after the Stage 1 report; the assistant explicitly stated this interpretation before work. Status: complete 2026-10-22. See [single-package Stage 2 results](NUGET-SINGLE-STAGE2-RESULTS.md): 226 checks passed, 130 baseline GPU captures identical, app/demo builds passed, document-only text queries verified.

- Add one library project containing parsing, geometry, WPF text shaping, measurement, interaction, renderer and native host. Keep the app project/path/version/installer identity stable.
- Implement the approved loading/document/viewer boundary, prepared text lifecycle, structured diagnostics and stable identities. Do not add platform-neutral numeric types merely to simulate a second package boundary.
- Migrate app and tests/demo to compiled project references; remove production-source compile links. Keep a single parser, text pipeline and renderer implementation. Keep low-level internals private; narrowly justify any test-only access.
- Preserve current desktop behavior and all measurement/layout/layer/text/hatch semantics. Expose traversal and programmatic measurement through the agreed public API.
- Document threading, ownership, cancellation/failure retention, replacement, disposal and native-window constraints.

Validation: complete regression suite and visual review; public API tests for prepared-text queries without a window/GPU; two viewers sharing a document; center/closest, midpoints and coordinate mismatch; resize/DPI notification; unload/reload, disposal and buffer reuse. State clearly which physical DPI/platform conditions were actually tested.

Exit: the desktop app consumes the one library through its public API, and document queries include text without extra consumer preparation.

## Stage 3 — entity visibility and document removal (skipped)

Status: explicitly declined by the user on 2026-10-22: "no, don't do those." Do not implement per-entity visibility or in-memory removal as part of Stage 4 or later publication work. Existing layer/category controls remain supported.

## Stage 4 — package and validate independent consumption

Approval: user said "do stage 4" on 2026-10-22. Status: complete. User selected MIT and VibeDxf.Wpf 0.2.0-preview.1. See [Stage 4 results](NUGET-SINGLE-STAGE4-RESULTS.md): packed/inspected package, two isolated consumers, actual installer build, 226 checks passed.

- Confirm the user-selected source license and dependency/fixture notices. Agree on package ID/version and add package metadata, XML documentation and a package README.
- Pack one NuGet into a local feed; inspect its contents and transitive dependencies. Exclude app assets, fixtures, private drawings, source/test links and generated output.
- Build/run a separate minimal WPF host restoring the package, not project references. Demonstrate load, traversal, prepared-text measurements and existing layer/category visibility. Per-entity visibility/removal remain excluded. Also exercise document-only queries in a Windows console host with the documented Desktop runtime/STA requirements.
- Verify app installer builds remain framework-dependent and retain the existing identity. Local verification must not trigger publication.

Exit: an independent developer can follow the documentation and use the package with no repository source, reflection or extra text-preparation implementation.

## Stage 5 — CI and publication preparation

Approval: pending. Status: not started.

- Add build/test/pack and independent-consumer CI checks; retain TRX and visual reports. Confirm existing application-release triggers before touching packaging/workflow files.
- Define preview versioning, API compatibility policy, source-commit verification, release notes and publication recovery. Keep NuGet publication separate from app installer releases.
- Prepare publication with approved authentication; do not create credentials, alter security settings, tag or publish. Present the exact package contents, version, commit and destination for final approval.

Exit: publication artifacts and workflow are reviewable and cannot accidentally publish through an app release trigger.

## Stage 6 — explicitly approved publication and download verification

Approval: pending. Status: not started.

- Publish the approved single package and agreed source tag only after explicit authorization for those exact actions. App publication is separate.
- Restore from the public feed into clean consumers and verify the documented examples and package/source/version agreement.
- Report partial failures honestly; never overwrite a published package version or move a published tag.

Exit: one publicly available package works through the documented APIs and its source/version can be verified.

## Current tracking

The rewrite and Stages 1-2 are complete under user approvals. Stage 3 was declined; Stage 4 is complete; Stages 5-6 remain pending. The active contract is docs/NUGET-SINGLE-STAGE1-DESIGN.md and implemented integration is docs/NUGET-SINGLE-INTEGRATION.md. The user subsequently explicitly authorized committing the completed work. Stage 5, entity visibility/removal, pushes and publication remain unauthorized. The original application was restored to baseline `8722a42` before this new extraction. The current working tree now contains the single Wpf library; checkpoint `77ac185` remains in Git history; this commit records its reversal and the completed single-package work. A local source snapshot of the abandoned extraction exists at `artifacts/nuget-split-before-revert.zip`. Preserve `.claude/` as user-local configuration. Do not resume the old two-package stages.
