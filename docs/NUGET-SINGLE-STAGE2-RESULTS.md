# Single-package Stage 2 results

Subsequent scope change: Stage 3 was explicitly declined; per-entity visibility/removal are excluded. Stage 4 packaging was approved with MIT and VibeDxf.Wpf 0.2.0-preview.1.

Date: 2026-10-22. Approval: the user's Hebrew-layout text `בםמאןמוק` maps to "continue"; the assistant stated that interpretation and proceeded with Stage 2 after the Stage 1 report/approval request. Status: complete. This is the one-package plan, not a resumption of the cancelled Core/Wpf split.

## Delivered

- Added the one compiled `src/VibeDxf.Wpf/VibeDxf.Wpf.csproj` library targeting `net10.0-windows`/WPF with the existing Vortice 3.8.3 references. Packing remains disabled. No Core project, new numeric layer, deferred-text bridge or second parser/renderer implementation.
- Moved parsing, block/layout/annotation expansion, spline/hatch/math, glyph shaping, triangulation, native host, renderer and interactions into that library. Low-level renderer/host/parser/builders are internal.
- Added the public immutable DxfDocument model with asynchronous path/stream loading on an internal STA worker, structured diagnostics, source color metadata, document/revision GUIDs, occurrence entity IDs, layout IDs, coordinate contexts, source/expanded handles, layer IDs, traversal/lookup and explicit viewport-frame links.
- Deep-copied/read-only nested contours and collections; prepared glyphs/triangles are completed before publication. No WPF text-layout objects, GPU buffers or mutable source lists are exposed on completed documents. Blank text continues to be omitted as in the original app; public fixture counts are unchanged.
- Added document.Measure anchors/results, Closest/Center strategies, midpoint validation, approximation flags, signed deltas and explicit foreign/missing/invalid/context/no-geometry statuses. Whole annotation graphics can be queried in code; interactive annotation selection remains disabled.
- Added DxfView with document assignment/convenience loading, dependency properties, source-based selection/event snapshots, public programmatic selection, layer/category controls, camera/conversions, clear/dispose and RenderingFailed. The control has no app dialogs/resources dependency.
- Implemented generation-based replacement and cancellation across load/assignment, STA text preparation, explicit dispatcher installation, independent per-view state, candidate GPU allocation/rollback and cleanup on unload/reload/disposal. The app cancels a pending load on close and disposes its viewer.
- Migrated desktop app, WPF regression suite and demo to compiled project references/public hosting APIs. Removed all linked production-source compilation; the demo still links its existing test-report helper, not production code. Narrow test friendship supports synthetic fixtures/device instrumentation; demo friendship is only for its existing GPU capture.
- Added `tools/DocumentSmoke`, a project-reference Windows console consumer proving document-only text traversal/measurement without a WPF Application/viewer or Vortice initialization. No STA setup or text-provider implementation is required from the console caller.
- Added nine public API integration tests and [current integration/lifetime documentation](NUGET-SINGLE-INTEGRATION.md). Updated active tracking and maintainer guidance.

Per-entity visibility and WithoutEntities/edited snapshots are deliberately absent until Stage 3. No placeholder API claims to implement them.

## Validation

```powershell
dotnet restore VibeDxf.sln --disable-build-servers --ignore-failed-sources -p:NuGetAudit=false
dotnet build VibeDxf.sln -c Release --no-restore --disable-build-servers -p:UseAppHost=false
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore --disable-build-servers --logger "trx;LogFileName=single-package-stage2.trx"
dotnet restore tools/DocumentSmoke/DocumentSmoke.csproj --disable-build-servers --ignore-failed-sources -p:NuGetAudit=false
dotnet run --project tools/DocumentSmoke/DocumentSmoke.csproj -c Release --no-restore -- Tests/Fixtures/Public/api-cw750-details.dxf Tests/Fixtures/Public/uncommon.dxf
dotnet restore tools/DemoDrawing/DemoDrawing.csproj --disable-build-servers --ignore-failed-sources -p:NuGetAudit=false
dotnet build tools/DemoDrawing/DemoDrawing.csproj -c Release --no-restore --disable-build-servers -p:UseAppHost=false
dotnet tools/DemoDrawing/bin/Release/net10.0-windows/DemoDrawing.dll
```

- Final full suite: **226 passed, 0 failed, 0 skipped**, duration 47 seconds; all 217 previous cases plus nine public API cases. TRX: `Tests/TestResults/single-package-stage2.trx`; full results/captures in `artifacts/test-report/index.html`.
- Public checks exercise prepared text/annotation measurements without a control; deep collection immutability and internal API boundaries; foreign/missing/invalid anchors and coordinate mismatch; stream success/failure/cancellation ownership; source colors/instance paths; two viewers with independent state/resources; strategy events; layer visibility/query independence; camera conversion; resize/simulated DPI; unload/reload/clear; load supersession/failure retention/disposal; paper viewport freezes and source contexts; candidate GPU replacement rollback using an injected pre-commit failure.
- All **130 baseline GPU PNG captures are byte-for-byte identical** to the preserved Stage 1 report, matched by capture title/occurrence. Comparison results: `artifacts/single-package-stage2-visual-comparison.json`. This is a saved-baseline rendering comparison, not equivalence to commercial CAD output or a hardware performance benchmark.
- Solution/app/demo builds succeeded with **0 warnings, 0 errors**. The previous member-hiding warning was removed by naming the private visibility method explicitly. Restores used cached dependencies with audit disabled; no successful vulnerability audit is claimed.
- Public fixture exact counts: api-cw750 has 4,313 model entities; four sheets at 63/2, 73/1, 74/2, 62/2 entities/viewports; skipped 3DSOLID=3 and VIEWPORT (3D view)=1. uncommon has 148 model entities, one spline, no retained sheets; skipped 3D POLYLINE=2, 3DSOLID=1, MESH=1. Existing pixel/GPU-upload assertions passed.
- DocumentSmoke loaded those same counts and measured prepared MTEXT successfully (sample distances 170.776371 and 11.172903). Its process had no WPF Application and loaded no Vortice assemblies. The host still requires Windows Desktop runtime; this is not a platform-independent library.
- Demo executed through the public load/assignment/selection/measurement API and retained **104 entities, 39.874871 mm**. GPU preview is in `artifacts/demo-preview/frame-000.png`; validation-generated tracked demo outputs were restored to avoid unrelated asset changes.
- No production-source compile links, Core references or old adapter remain. No packaging/workflow/release/installer version, public README or dependency notices changed. Whitespace checks passed for the edited source/project files.

## Repository state and limits

Work remains uncommitted on `nuget-library-split`; its historical HEAD is `77ac185`. Status includes deletion of the previously reverted Core extraction as well as the new single library. No new commit, push, tag, pack, publication, release, license decision or credential change was performed. Leave `.claude/` user-local. The abandoned extraction snapshot remains in `artifacts/nuget-split-before-revert.zip`; do not restore it over current work.

Individual WPF text operations and driver calls are not interruptible; cancellation is checked between/around them. Pending UI operations need a live dispatcher for cleanup. Physical movement across monitors with different DPI, real device loss, packed NuGet consumption and installer execution were not tested in this stage. Narrow internal constructors/builders remain for the parser and existing synthetic regression helpers; they are not exposed as public CAD editing APIs.

Stage 2 exit criteria are met: the app consumes one library through public APIs, and document-only loading/queries include prepared text. Stage 3 requires fresh approval for entity visibility and batch removal into immutable edited snapshots. Commits remain separately authorized.
