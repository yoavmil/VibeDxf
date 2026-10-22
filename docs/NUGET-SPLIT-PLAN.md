# Plan: split VibeDxf into two NuGet packages

Superseded by [the single-package plan](NUGET-PLAN.md). This document is retained only as cancelled history; none of its prior stage approvals apply to the replacement plan.

Status: CANCELLED by user on 2026-10-22. Extraction code was restored to the pre-split baseline (8722a42). Keep one Windows/WPF project for now; any future reusable library should keep text preparation and geometry together. The stage history below is archival, not authorization to resume.

## Approval rule

Before starting EACH numbered stage below, ask Yoav for explicit approval and wait for his answer. Approval for one stage does not approve the next. After each stage, report the changes, checks, remaining risks and any changes proposed to this plan. If scope changes materially within a stage, stop and ask before expanding it. Do not infer approval from elapsed time or silence.

Writing this plan is authorized. Implementation, commits, tags, package publication and app releases are not authorized by this document. Ask separately for those actions unless Yoav explicitly includes them in a stage approval. Record approval and completion evidence in this document as work proceeds.

## Intended result

Keep one repository and produce two packages:

| Component | Responsibilities | Dependency direction |
| --- | --- | --- |
| VibeDxf.Core | ASCII DXF parsing, document/entity model, double-precision geometry, layers/layout definitions, structured diagnostics and distance calculations | No WPF, Windows-only APIs, Vortice or GPU dependencies |
| VibeDxf.Wpf | Embeddable WPF viewer, Direct3D/Direct2D rendering, text shaping and outlines, camera, picking, selection and measurement presentation | References Core and the existing Vortice packages |
| Existing desktop app | Menus, file dialogs, drag/drop policy, panels, settings, installer and other application UI | Consumes the packages/projects through their public API |

Core must be usable without a window, graphics device or installed Windows Desktop runtime. The WPF package remains Windows-only. Start with the existing .NET 10 target generation; decide minimum supported framework versions in Stage 1 rather than silently widening scope. Cross-platform rendering, other UI frameworks, DWG support and new DXF features are outside this extraction.

Both NuGets initially share a version and are released together. Keep the existing application working throughout migration; preserve its installer identity and framework-dependent runtime policy.

## Stage 1 — map dependencies and agree on the public contract

Approval: user replied "go" on 2026-10-22. Status: completed 2026-10-22; proposed contract awaits acceptance before Stage 2. See [Stage 1 design and baseline](NUGET-STAGE1-DESIGN.md).

- Inventory source dependencies, linked-source test/demo projects, current APIs, licenses and release workflow triggers.
- Establish a clean baseline: build, complete xUnit suite, public-fixture supported/skipped counts and visual report. Record the actual counts at that time, not an assumed fixed total.
- Propose project folders, namespaces, framework targets and a small public API for loading documents, diagnostics, viewer document assignment, selection events, measurements, layers and layouts.
- Decide numeric types and affine-transform conventions, preserving double precision, y-up DXF coordinates and current tolerance semantics.
- Resolve text explicitly: Core preserves TEXT/MTEXT content, formatting and placement; WPF shapes it into outlines. Define how blocks, nonuniform transforms, measurement bounds and diagnostics work before implementation. A Core-only load must retain text instead of silently dropping it.
- Separate raw DXF colors from display policies such as dark-background brightening. Separate saved layer state from viewer visibility state.
- Define immutable/shared document data versus per-viewer state and resource ownership. Decide what is public and what stays internal.

Deliverable: dependency map and agreed API sketches in docs, plus baseline evidence. No production restructuring in this stage.

Exit check: the proposed dependency graph has no Core-to-WPF cycle; text and measurement behavior have an explicit migration design.

## Stage 2 — establish independent geometry types and extract Core

Approval: user replied "go" on 2026-10-22 after the Stage 1 completion report. Status: implementation complete; see [Stage 2 results](NUGET-STAGE2-RESULTS.md).

- Introduce independent double-precision point/vector/rectangle/affine types with documented angle, composition, bounds and empty-value conventions.
- Create VibeDxf.Core and move geometry, spline math, parsing, document/layer/layout data and distance algorithms behind the Stage 1 contract.
- Remove WPF references and text-outline generation from the parser. Preserve text descriptors and structured warnings; move presentation-only state out of Core.
- Keep geometry generation such as hatch boundaries and triangulation in Core where it needs no UI; expose data independently of GPU buffer formats.
- Adapt the existing app through temporary internal adapters as needed so it remains usable. Document and time-limit any transitional bridge; do not publish it as a permanent API by accident.
- Preserve entity identity, block instance paths, coordinate contexts, approximation markers and unsupported-entity accounting.

Validation: Core builds without UseWPF or a Windows target; geometry/parser tests reference the compiled Core project. Run the existing app regression suite. Exercise Core in a plain console host without constructing WPF objects. Add meaningful tests for transform conversion and retained text descriptors.

Exit check: no System.Windows, Vortice, window or graphics initialization dependency in Core; no lost DXF content or unreported text limitations.

## Stage 3 — extract the WPF viewer and integrate the desktop app

Approval: explicitly granted on 2026-10-22: "before, create a branch for this, and commit the intermediate changes, then continue to stage 3". Status: completed 2026-10-22. Authorized checkpoint commit 77ac185 succeeded through the supported execution approval flow. See [Stage 3 results](NUGET-STAGE3-RESULTS.md).

- Create VibeDxf.Wpf and move the renderer, native swap-chain host, text layout/vectorization and viewer interaction into it.
- Consume Core documents through explicit preparation of renderable geometry. Keep prepared geometry and GPU resources separate from source document data; clarify how prepared text participates in picking and distance calculations.
- Expose the agreed document, selection, measurement, visibility, layout and camera APIs. Host applications own dialogs, menus and warning presentation.
- Keep midpoint snapping, independent center/closest strategies, layout coordinate rules and layer visibility behavior unchanged.
- Update the existing application and demo tool to use project references and public APIs. Avoid duplicating source through compile links or maintaining two renderer implementations.
- Document UI-thread/STA requirements, disposal, document replacement, cancellation/error handling and HwndHost overlay limitations.

Validation: run the complete suite and visual comparisons, including text, hatches, blocks, layouts, viewport-frozen layers and measurement overlays. Verify two viewer instances, resize/DPI changes, load/unload/reopen and resource cleanup. Confirm pan/zoom still reuses geometry buffers.

Exit check: desktop features remain intact and the viewer has no dependency on MainWindow, application resources or app-specific dialogs.

## Stage 4 — test real package consumption and document integration

Approval: pending. Status: not started.

- Add package metadata, XML API documentation, license information, third-party notices and package READMEs. Confirm the repository license permits the proposed distribution; ask if a license choice is required.
- Pack both NuGets into a local feed. Inspect package contents and dependencies; exclude private drawings, build artifacts and app-only assets.
- Add a tiny independent WPF sample that restores the packed packages rather than project references. Demonstrate opening a DXF, selection/measurement events and host-controlled UI.
- Add a Core-only consumer for loading and measuring without WPF; keep both examples free of private file paths.
- Add consumer tests where they validate real packaging boundaries. Keep xUnit discovery and the HTML visual report working.
- Document supported entities, text limitations, approximation semantics, threading, native-window constraints and runtime prerequisites.

Validation: restore/build/run consumers from the local package feed and verify transitive dependencies. Verify the independent Core consumer has no Windows Desktop framework requirement. Confirm existing app installer packaging still works.

Exit check: a new developer can follow the documented sample with no repository source links or reflection into internals.

## Stage 5 — prepare CI, versioning and publication safeguards

Approval: pending. Status: not started.

- Add CI build/test/pack checks for both libraries and packaged consumers, with visual reports retained as artifacts.
- Define preview versions, API compatibility policy, package IDs/availability and release notes. Keep the two package versions consistent and Wpf's Core dependency deliberate.
- Review the existing release workflow before editing packaging files: pushes to main under packaging/ or the release workflow currently trigger an app release. Prevent unintended publication while preparing these changes.
- Prepare package publication using scoped GitHub Actions/NuGet authentication. Do not create credentials, change account security or publish packages without explicit approval.
- Specify the order Core then Wpf, duplicate-version handling, partial-publication recovery, tag-to-commit verification and separation from app installer releases. Published package versions and release tags must not be overwritten.
- Update maintainer documentation and link this plan from CLAUDE.md when implementation tracking begins.

Validation: build/test/pack in CI without publishing; inspect artifacts and package version/dependency consistency. Confirm existing app release behavior remains intentional.

Exit check: all artifacts are reviewable and publication requires a separately approved action.

## Stage 6 — publish the approved preview and verify downloads

Approval: pending. Status: not started.

- Present the exact package IDs, versions, source commit, contents, release notes and destination for explicit publication approval before starting this stage.
- Publish Core and then Wpf with matching versions, create the agreed new tag at the exact built commit, and publish documentation/sample links.
- Keep application installer publication separate unless explicitly included in the approval.
- After package indexing, restore both from the public feed into clean consumers, verify build/runtime behavior and link the published package pages.
- Report any partial publication honestly; use a new version for corrections where published artifacts cannot be replaced.

Exit check: both public packages restore successfully with the documented usage; tag, source commit and package versions agree.

## Tracking

For each stage, record: approval message/date, start/end, changed files, validation results, deviations and next approval needed. Keep all stages pending until explicit user approval is received.

Initial rough estimate: 2–5 focused development days for extraction and a public preview, with the greatest uncertainty in decoupling text preparation and preserving text measurements. Reassess after Stage 1; this is an effort estimate, not a delivery promise.


## Historical resume handoff (resolved by Stage 3)

Current state: checkpoint 77ac185 is committed; Stage 3 is complete and uncommitted; Stage 4 is pending approval. The details below preserve the previous blocker and resume instructions as historical evidence.

## Resume handoff � 2026-10-22

Start the next session with **C:\repos\sandbox\CodexDXF** as the project/workspace folder. The previous session was attached to Documents\Codex\2026-10-20\ope and used extra filesystem grants to edit this repository.

### Exact state at handoff

- Active branch: **nuget-library-split**, successfully created with ordinary `git switch -c nuget-library-split`.
- HEAD: **8722a427be68133e156e9ddd17a3cf33b27ce1d0**, "Prepare v0.2.0 release". The Stage 1�2 work has **not been committed**. No push, new tag or package publication was performed for this extraction.
- The working tree contains all Stage 1�2 changes, including this plan, Stage 1 design, Stage 2 results, Core project/tests, console smoke tool and WPF adapters. These documents are currently untracked and must be included in the checkpoint.
- **Leave the existing untracked .claude/ directory out of the commit.** It is user-local configuration. Reinspect status for any additional user changes before staging.
- Latest validation: 217 existing xUnit/GPU cases plus 21 Core cases passed (238 total, no failures/skips). App and demo builds passed. Existing CS0108 warning persists; demo restore also reported unavailable vulnerability-feed data (NU1900). See NUGET-STAGE2-RESULTS.md for commands, fixture counts and limitations.
- Tests regenerated artifacts/test-report/index.html for the existing 217 visual/offline cases. The 21 Core tests are independently discoverable in Test Explorer and have TRX output.
- No Stage 3 implementation has begun. Do not repeat the extraction or replace the current work with the release commit.

### First actions in the new session

1. Read CLAUDE.md, this plan, NUGET-STAGE1-DESIGN.md and NUGET-STAGE2-RESULTS.md; inspect current status/diffs and any applicable AGENTS.md. Existing architecture sections in CLAUDE.md describe the legacy viewer and should be reconciled during Stage 3.
2. Verify the branch and HEAD. If nuget-library-split already exists and is active, reuse it; do not create another branch. If a checkpoint commit has already been made by the user, inspect it rather than duplicating it.
3. Complete the **already authorized intermediate commit** before Stage 3. Use normal Git commands, inspect the staged diff and stop on any unexpected files. The planned staging/commit commands are:

   ```powershell
   git add CLAUDE.md VibeDxf.sln Tests/VibeDxf.Checks.csproj Tests/Core docs/NUGET-SPLIT-PLAN.md docs/NUGET-STAGE1-DESIGN.md docs/NUGET-STAGE2-RESULTS.md src tools/DemoDrawing/DemoDrawing.csproj tools/CoreSmoke
   git diff --cached --check
   git diff --cached --stat
   git commit -m "Extract platform-independent DXF core and document staged NuGet plan"
   ```

   Record the resulting commit SHA here. Do not push unless separately authorized.
4. Once the checkpoint succeeds, continue **Stage 3 under the existing approval**. Extract VibeDxf.Wpf, migrate away from the temporary projection and linked-source compilation, preserve rendering/measurement behavior and validate the public control/resource lifetime. Keep the numerical/Core boundary intact. Complete that stage and report its checks before asking to begin Stage 4.
5. Stages 4, 5 and 6 remain unapproved. No NuGet publication, app release, tag creation or license choice is authorized by this handoff.

### Git blocker diagnosed in the old session

`git add` and `git commit` failed to create `.git/index.lock` with Permission denied, even after a filesystem grant covering the repository and .git. Branch creation succeeded. Read-only checks found no existing index.lock and no read-only index attribute. Commands ran as CodexSandboxOnline; .git/index had explicit deny-write ACL entries alongside allow entries. The session allowed request_permissions but disabled sandbox-approval and new-rule prompts. This is evidence of an execution/permission restriction, not a stale lock; the exact originating ACL entry was not proven.

Try normal Git from the correctly attached repository session first. If denied again, use the supported permission/approval flow and explain the concrete failure. Opening the correct workspace alone is not a guarantee that protected Git writes will be allowed. Do not manually edit .git, remove sandbox ACLs, relocate the index to evade restrictions, disable security controls, or use another execution surface to bypass a denial. Ask the user to adjust the session permissions if required.

### Implementation reminders

- Core targets net10.0, has no WPF/graphics dependencies, and packing is intentionally disabled until the package stage.
- Core retains deferred text and uses immutable geometry snapshots for optional prepared outlines. WPF currently prepares text through CoreDocumentAdapter. Eight blank paper text records are retained in Core but omitted after shaping; visible fixture counts remain unchanged.
- Existing GPU buffer reuse, text transforms, hatch boundaries, layer freezes, coordinate contexts, midpoint snapping and per-endpoint Center/Closest semantics must remain intact.
- Transitional Core InternalsVisibleTo entries, legacy WPF document/entity wrappers and compile links are intentionally scheduled for removal/replacement in Stage 3.
- A source license has not been selected. Preserve commit attribution, including Claude co-author trailers; the GitHub contributor listing does not itself choose the project's license.
