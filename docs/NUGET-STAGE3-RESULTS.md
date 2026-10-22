> Archived: the user cancelled the two-library extraction on 2026-10-22. Its code has been reverted to the original single-project application. This document describes the abandoned implementation, not current APIs.

# NuGet extraction: Stage 3 results

Date: 2026-10-22. Approval was recorded in the prior handoff: "before, create a branch for this, and commit the intermediate changes, then continue to stage 3".

## Checkpoint and repository state

Reused `nuget-library-split`. Created the authorized Stage 1-2 checkpoint `77ac185` ("Extract platform-independent DXF core and document staged NuGet plan"). The ordinary staging command encountered `.git/index.lock` permission denial; the supported elevated execution approval succeeded. No permissions or Git internals were manually modified. `.claude/` was excluded and remains user-local.

Stage 3 changes remain uncommitted for review. No push, tag, package build/publication, installer release or license choice was performed. Stages 4-6 remain unapproved.

## Delivered

- Added compiled `src/VibeDxf.Wpf/VibeDxf.Wpf.csproj`, targeting `net10.0-windows`, referencing Core and the same three Vortice 3.8.3 dependencies. Packing remains disabled.
- Moved the single renderer, native swap-chain host, WPF interaction and glyph shaping into the library. They are internal. The desktop app now contains only application UI and references the library.
- Replaced the Stage 2 public WPF document projection with private `PreparedDrawing`/`PreparedEntity`/`PreparedCurve` rendering data. `CoreDocumentAdapter` is gone. `GeometryPreparation` prepares per-view data and supplies an independent neutral Core `GeometrySnapshot`; public selections identify original Core entities and document-local anchors.
- Added public `DxfView`, `DxfSelection`, selection/measurement event snapshots, strategy dependency properties, layout/visibility operations, programmatic selection, camera and coordinate conversion, preparation diagnostics, clear and disposal APIs. The control owns no file dialogs, menus, drag/drop policy, application resources or warning UI.
- Text preparation uses a dedicated STA worker. Final installation explicitly invokes the control dispatcher. Replacement generation checks, cooperative cancellation, failure retention and disposal prevent stale loads from installing. Candidate GPU buffers are allocated before replacing old buffers; failed allocations dispose their candidate resources.
- Core source text remains deferred and shareable. Prepared text participates in both picking and Core snapshot measurements; layer/color/camera/selection changes do not mutate Core data. Eight blank text records in the public sheets remain source records without visible geometry, preserving the Stage 2 source/visible count distinction.
- App, WPF checks and demo now reference compiled projects; no production source compile links remain. The demo uses public document assignment, selection and measurement APIs. Its narrow friendship remains solely for existing GPU report capture. Wpf's test friendship supports internal renderer regression instrumentation; neither low-level renderer nor prepared entity models are public.
- Removed production Core friendships. The sole Core friend is `VibeDxf.Checks`, for direct offline hatch/multileader parser helpers and malformed-document tests. Those parser shims now live in Tests, not the production viewer. Core's semantic `DxfTextFormatting` parser is public so WPF consumes it without internal access.
- Updated CLAUDE.md and added [integration and lifecycle documentation](WPF-INTEGRATION.md).

## Validation

```powershell
dotnet restore VibeDxf.sln --ignore-failed-sources -p:NuGetAudit=false
dotnet restore tools/DemoDrawing/DemoDrawing.csproj --ignore-failed-sources -p:NuGetAudit=false
dotnet build VibeDxf.sln -c Release --no-restore -p:UseAppHost=false
dotnet test Tests/Core/VibeDxf.Core.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=core-stage3.trx"
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore --logger "trx;LogFileName=stage3-regression.trx"
dotnet build tools/DemoDrawing/DemoDrawing.csproj -c Release --no-restore -p:UseAppHost=false
dotnet run --project tools/DemoDrawing/DemoDrawing.csproj -c Release --no-restore
```

- WPF suite: **222 passed, 0 failed, 0 skipped**; 217 existing cases plus five public integration cases. Final full run duration: 45 seconds. Covers existing GPU pixels/buffer reuse, text, hatches, blocks, layouts, viewport-frozen layers, coordinate contexts, midpoint and Center/Closest behavior. TRX: `Tests/TestResults/stage3-regression.trx`.
- New public checks exercise two instances sharing a source document with independent selection/camera/visibility/resources; prepared text and event snapshots; strategy dependency-property recalculation; model coordinates and frozen layers through paper viewports; cancellation, supersession, validation failure retention and disposal; resize, simulated DPI notification, unload/reload/reopen, clear and GPU buffer reuse.
- Core suite: **21 passed, 0 failed, 0 skipped**. TRX: `Tests/Core/TestResults/core-stage3.trx`. Total: **243 cases**.
- Solution and demo builds succeeded with **0 warnings and 0 errors**. The previous member-hiding warning was removed by naming the private visibility method explicitly. Cached dependencies restored successfully with audit disabled; this is not a vulnerability-audit result.
- Public fixture visible model counts remain 4,313 and 148, including the same exact skipped counts and four-sheet visible entity counts (63/73/74/62). Core source paper counts remain 64/76/77/63. Existing GPU upload assertions passed.
- HTML report regenerated at `artifacts/test-report/index.html`, including the five new API results and existing rendered captures. Inspected actual public-drawing and demo GPU images. The demo ran through `DxfView` and retained **104 entities and 39.874871 mm** spline-to-circle-center distance. This is automated rendering/measurement evidence and representative visual inspection, not a pixel-perfect saved-baseline or commercial-renderer comparison.
- `git diff --check` passed. No new dependencies or release/installer configuration changes.

## Limits and next stage

Physical movement between monitors with different DPI was not tested; the check invokes a DPI notification and verifies buffer retention. Actual Linux/macOS Core runtime execution, device-loss recovery, installation, packed NuGet consumption and CI publication remain outside this stage's validation. Individual WPF shaping calls are not interruptible; cancellation is observed between entities and within triangulation. Pending operations need a live dispatcher for final cleanup.

Stage 3 is complete at the project/API boundary. Stage 4 requires explicit approval: package metadata/licensing, local packing, independent package consumers and integration documentation validation. The source license remains the user's decision before distribution. No Stage 3 commit is authorized by the stage approval alone.
