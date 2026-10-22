# NuGet extraction: Stage 2 results

Date: 2026-10-22. User explicitly approved Stage 2 with "go" after the Stage 1 report. Stage 3 is not approved or started. No commit, tag, package publication or app release was performed.

## Delivered boundary

- Added src/VibeDxf.Core/VibeDxf.Core.csproj, targeting net10.0 with no WPF, Windows Desktop, Vortice or other package references. Packing is deliberately disabled until the packaging stage.
- Moved the ASCII parser, block/dimension/leader expansion, hatch construction, spline flattening, analytic curve distance, color palette resolution and pure triangulation/transformation into the compiled Core project.
- Added independent double-precision Point2D, Vector2D, Bounds2D and Affine2D types. Transform composition is Then(next) = next(this(point)); Rotate accepts degrees while curve angles/sweeps remain radians. Empty bounds differ from a zero-size point. Rays/xlines retain finite fit bounds and infinite geometric domains.
- Core documents expose read-only collections, including defensive copies of entity curves/regions, viewport freeze lists and text tags. Entity IDs are unique within each loaded document; original handles and block-instance paths are retained. Saved visibility and raw ACI/true-color metadata are independent of view state and dark-theme lightening.
- Core parses text descriptors and MTEXT formatting runs without fonts: source content/tags, placement, alignment, direction, font-style request and composed block transforms. Text-only files load successfully. Bounds explicitly report incompleteness until text is prepared; unsupported modes retain warnings/skips.
- Added GeometrySnapshot and measurement anchors/results. Closest/Center strategies and exact finite-line midpoints produce endpoints, signed delta X/Y, Euclidean distance and approximation flags. Unprepared text returns GeometryNotPrepared. Invalid targets, display-only annotations and mixed model/paper contexts return explicit statuses. A supplied neutral text geometry snapshot can be measured without modifying source entities.
- Added synchronous path/stream loading, asynchronous path loading and structured diagnostic codes. Stream loading defaults to leaving the caller's stream open. Cancellation is observed while reading, grouping/expanding records and during spline/hatch work; pure triangulation also accepts cancellation.
- Added a Core xUnit project to the solution and tools/CoreSmoke, a plain net10.0 console consumer with a project reference. Core's runtime dependency is Microsoft.NETCore.App, not Microsoft.WindowsDesktop.App.

## Transitional WPF adapter and Stage 3 work

The existing desktop application and viewer retain their WPF-facing Point/Rect/entity types during this stage. CoreDocumentAdapter projects a parsed Core document into that representation and prepares text with the existing WPF shaping implementation. It reuses Core's pure geometry algorithms and applies the existing dark-theme policy. The old parser, hatch reader, multileader reader and spline implementation are replaced by delegates/adapters rather than a second parsing pipeline.

The remaining WPF entity model, selection wrapper and linked-source WPF test/demo compilation are temporary. They must be removed or replaced with compiled Wpf project references in Stage 3. The Core tests already reference the compiled Core assembly. Narrow InternalsVisibleTo declarations allow the current app/test/demo adapters to reach internal semantic helpers; remove these transitional friendships during Stage 3. Do not turn them into a permanent public renderer API.

Triangulation caches remain on the legacy WPF projection, prepared during load. Shared Core source documents do not contain mutable GPU buffers or triangle caches. Device lifetime, bindable viewer APIs, asynchronous presentation preparation and its STA scheduling are Stage 3 work; this stage preserves the current WPF text preparation behavior rather than introducing a new worker/device architecture.

Core exposes Load(path, cancellationToken), Load(stream, leaveOpen, cancellationToken), and LoadAsync(path, cancellationToken), returning DxfLoadResult. These implement the Stage 1 proposed contract without adding an otherwise unnecessary DxfLoadOptions type yet. Diagnostics currently provide stable phase/severity/code, entity kind, count and an instance handle for deferred text. Aggregated unsupported warnings cannot always identify an individual source record.

## Counts and text semantics

Public fixture Core results:

| Drawing | Model entities | Model entities with deferred text | Skipped |
| --- | ---: | ---: | --- |
| api-cw750-details.dxf | 4,313 | 478 | 3DSOLID=3; VIEWPORT (3D view)=1 |
| uncommon.dxf | 148 | 38 | 3D POLYLINE=2; 3DSOLID=1; MESH=1 |

api-cw750 Core paper layouts contain 64/76/77/63 entities and 2/1/2/2 viewports. Core retains eight blank text records across these sheets, because parsing no longer depends on whether a font produces ink. After preparation the WPF projection retains the original visible counts: 63/73/74/62. This distinction is tested explicitly; no new unsupported records were introduced. uncommon retains no paper layout tabs.

Core's text descriptors preserve raw DXF content and parsed formatting runs; they do not claim exact font outlines. Core-only users can measure non-text geometry immediately. Text-outline measurement needs an explicitly supplied prepared GeometrySnapshot. The default WPF adapter continues to use Arial substitution and mark text geometry approximate.

## Validation

Commands:

```powershell
dotnet test Tests/Core/VibeDxf.Core.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=core-stage2.trx"
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore -p:OutputPath=bin/Stage2Final/ --logger "trx;LogFileName=stage2-regression.trx"
dotnet build src/VibeDxf.csproj -c Release --no-restore -p:OutputPath=bin/Stage2Final/ -p:UseAppHost=false
dotnet build tools/DemoDrawing/DemoDrawing.csproj -c Release -p:OutputPath=bin/Stage2/ -p:UseAppHost=false
dotnet run --project tools/CoreSmoke/CoreSmoke.csproj -c Release -- Tests/Fixtures/Public/api-cw750-details.dxf Tests/Fixtures/Public/uncommon.dxf
```

- Core suite: 21 passed, 0 failed, 0 skipped. Covers assembly dependencies, affine order/inverse/mirroring, read-only data, large-origin precision, text-only documents, semantic MTEXT/embedded direction, retained blank text, block transforms, geometry snapshots, coordinate contexts, strategies, midpoints, diagnostics, streams, cancellation and public fixture counts.
- Existing suite: 217 passed, 0 failed, 0 skipped, including GPU captures, picking, layers/layouts, hatch boundaries, text and distance regressions. Final results are recorded in Tests/TestResults/stage2-regression.trx; Core results in Tests/Core/TestResults/core-stage2.trx.
- App and demo builds succeeded. Existing CS0108 warning remains (DxfViewport.IsVisible hides UIElement.IsVisible); WPF markup compilation may report the same warning twice. Demo restore also reported NU1900 because the vulnerability feed was unavailable; dependencies restored and the build succeeded. This was not a vulnerability scan success.
- CoreSmoke loaded both public drawings and verified a distance of 3. Its runtimeconfig requires only Microsoft.NETCore.App 10.0. Core assembly reference assertions exclude WPF/Windows/graphics libraries. Actual Linux/macOS execution is not claimed.
- The HTML visual report at artifacts/test-report/index.html was regenerated for the 217 WPF/offline cases. Core's 21 independent tests are in Test Explorer/TRX, not appended to that report. A generated MTEXT GPU capture was inspected; this is not a pixel-perfect comparison to a saved pre-extraction image set or commercial renderer.
- git diff --check passed. No rendering hardware benchmark or installer publication was performed.

## Approval required next

Stage 2 is complete. Ask Yoav before beginning Stage 3: extract VibeDxf.Wpf, remove the temporary projection, migrate app/tests/demo to compiled libraries and stabilize the control/resource-lifetime API. Source license choice is still required before public package distribution, but no license was added or attribution changed in this stage.
