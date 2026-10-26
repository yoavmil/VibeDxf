# Publishing VibeDxf.Wpf

## Stable 0.3.0

Published and publicly verified. See [release results](RELEASE-0.3.0-RESULTS.md).

Release 0.3.0 publishes the geometry and saved-camera work merged through PR #9.
The user authorized both NuGet and desktop publication on 2026-10-26. Package and
app versions are both 0.3.0 for this release, with independent `nuget/v0.3.0` and
`v0.3.0` annotated tags and the existing validation/publication workflows.

The release adds saved-camera metadata and selection APIs, model camera restoration,
perspective/clipping/depth display, 3D polyline/mesh wireframes, ACIS straight,
elliptical and explicit intersection-spline boundaries, tolerant topology and
edge-free ring-torus display. Nonplan views are display-only; document measurements
remain projected XY. Hosts can set ActiveModelView = null after document assignment
to retain the original XY view. Existing public members are retained. General ACIS
surface meshing, lighting, orbit navigation, editing and writing remain unsupported.

Older release records and examples below are historical. Use the current project
version and exact candidate's validation.json for new publication and verification.

## Stable 0.2.0 (historical)

Published and publicly verified. See [stable release results](NUGET-STABLE-0.2.0-RESULTS.md).

The user explicitly approved publishing `VibeDxf.Wpf 0.2.0` as a stable release on
2026-10-22. It promotes the verified preview with no public API or runtime behavior
changes. The package README, release metadata and independent sample references
now use 0.2.0. The desktop app remains at 0.2.0 and is not being released again.
The source tag is `nuget/v0.2.0`; the automated workflow builds and validates the
tagged source, publishes its tested artifact, and verifies public-feed consumers.
The preview version and its tag remain immutable historical releases.

Stable release notes: embeddable DXF viewer, immutable documents with prepared
text outlines, entity traversal, double-precision distance queries, layouts and
layer/category controls. Windows and .NET 10 Desktop Runtime are required. The
documented supported-entity, font/geometry approximation and native-window limits
are unchanged. Per-entity visibility, removal/editing and DXF writing are excluded.

The NuGet package is independent of the desktop app release. Version 0.2.0-preview.1,
MIT, nuget.org publication and a source tag were approved by the user. The user
subsequently requested automated publishing using GitHub Actions Trusted Publishing.
The trust policy is now active, and 0.2.0-preview.1 is publicly available from the
original approved local candidate. No long-lived NuGet API key is stored in the
repository. See the [Stage 6 results](NUGET-STAGE6-RESULTS.md) for public consumer verification evidence.

## Automated publishing setup

The `Publish NuGet` workflow lives in `.github/workflows/nuget-publish.yml`.
The repository variable `NUGET_USER` is `yoav.miller` (the NuGet profile username).
On [NuGet Trusted Publishing](https://www.nuget.org/account/trustedpublishing),
create this policy under package owner `yoav.miller`:

| Field | Value |
| --- | --- |
| Policy name | VibeDxf GitHub publishing |
| Provider | GitHub Actions |
| Repository owner | yoavmil |
| Repository | VibeDxf |
| Workflow file | nuget-publish.yml |
| Environment | Leave empty (the workflow does not use an environment) |
| Scope | Push new packages and package versions |
| Glob patterns/packages | VibeDxf.Wpf |
| Unlist/relist | Disabled |

The trust policy grants this repository/workflow permission to publish only the
named package. It does not grant access to other packages owned by the account.
Creating that policy is a separate permission change from adding workflow source.
Official [Trusted Publishing instructions](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

## Publish with GitHub Actions

For a new version, update the library project version and release notes, commit
the source, then create and push an annotated `nuget/v<version>` tag for that commit.
The tag must match the project version. Pushing the tag starts publication.

For an existing tag, use **Actions > Publish NuGet > Run workflow** on main,
enter its source tag, and enable `publish`. Leave `publish` off for a validation-only
run: it builds, tests, packs and runs isolated consumers without requesting NuGet
credentials or publishing. The workflow must be present on the default branch for
GitHub's Run workflow control to be available.

The existing `nuget/v0.2.0-preview.1` tag stays on source commit `f019536`.
Use manual dispatch for that first version; do not delete or move its existing tag.
The newer workflow checks out the tagged source to build and validate it.

The pipeline builds the app/library, runs the regression/GPU suite, packs with the
exact source commit and runs both independent package consumers. A separate job
downloads that tested artifact and verifies its SHA-256 before publishing; it does
not rebuild the package. Only the publishing job can request an OIDC token.
`NuGet/login@v1` exchanges that token immediately before `dotnet nuget push`.
The account policy must match the repository and workflow file above.

The final step waits up to ten minutes for indexing, then downloads the public
package, verifies source metadata and DLL hash and runs both consumers against a
fresh cache with nuget.org as the only feed. A push is not considered fully verified
until this final step succeeds. Candidate and public verification artifacts are
retained for 90 days; regression reports for 14 days. Archive release records before
artifact expiry.

The publishing job downloads by the artifact ID emitted by validation, so retrying
only a failed publishing job reuses the already tested candidate from that run.

On rerun, an already indexed version is verified instead of pushed again. A
different source commit or DLL fails verification. The pipeline does not use
`--skip-duplicate`, move tags, unlist packages or publish a desktop release. If
indexing exceeds the wait, rerun later against the same tag. If the package itself
is wrong, publish a new version.

When the existing version was uploaded manually or built with a different SDK,
its DLL can differ from a fresh CI rebuild of the same source. For that case only,
manual dispatch accepts `existing_library_sha256`: copy it from the original
approved candidate's validation.json (not from an unverified public download).
It is rejected if the package is not already public, and never changes which bytes
can be pushed for a new version. The public package must still match the exact
tagged commit, metadata, contents and that approved checksum, and both consumers
must pass. Leave this input empty for normal new releases. Authentication is also
tested on existing-version runs; dry runs never request credentials.

## Source, versions and compatibility

- The library project owns the NuGet version; the desktop app remains 0.2.0.
- Use `major.minor.patch-preview.N` for previews. Increase N for every published
  correction. Never replace a published package, move its tag or reuse its version.
- Public APIs can change during previews. Document breaking changes explicitly in
  release notes. After 1.0, compatible additions increase minor; fixes increase
  patch; breaking source/binary/behavior contracts require major.
- Review public declarations, signatures, coordinate/identity/failure semantics,
  lifetime and supported targets against the previous release before each release.
  There is no automated API compatibility baseline for the first preview.
- Source tags are annotated `nuget/v<package-version>`. They deliberately do not
  match the app workflow's `v*` trigger. Do not change `packaging/**` or
  `.github/workflows/release.yml` for a NuGet release: pushes of those paths on
  main can publish the desktop installer. The NuGet checks workflow has read-only
  permissions, no secrets, push command or release trigger.

## Build and review the exact candidate

Use Windows, PowerShell 7 and .NET 10 SDK. Commit all release source first. Only
local `.claude/` untracked configuration is exempted from the clean-source check.

```powershell
dotnet build VibeDxf.sln -c Release
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-build --logger 'trx;LogFileName=nuget-release.trx'
$sourceCommit = (git rev-parse HEAD).Trim()
./tools/Test-LocalPackage.ps1 -Release -ExpectedCommit $sourceCommit
```

The harness verifies MIT/README/notices/XML docs, exactly one library, the three
existing Vortice 3.8.3 dependencies, the version, repository URL and exact commit.
It copies plain public consumers, binds their package reference to the candidate
version, restores from the candidate to a fresh isolated
cache, compares the loaded DLL bytes, and runs console and WPF smoke checks.
No source links, project references or reflection are allowed in consumers.
NuGet audit is disabled for the isolated smoke restore; this is not a security audit.

For the stable release, upload **only** the tested `artifacts/nuget/release/0.2.0/VibeDxf.Wpf.0.2.0.nupkg` if using the manual fallback.
The adjacent `validation.json` and `SHA256SUMS.txt` identify its exact bytes,
library hash, source commit, contents and validation. Preserve this directory.
The harness refuses to replace an existing candidate for the same version.
Development mode without `-Release` remains marked `local-uncommitted` and must
never be uploaded. Ordinary NuGet-check artifacts are retained for 14 days; the
publishing workflow keeps release candidates for 90 days. Archive release records
separately. GPU checks need hardware or the existing WARP fallback.

## Source tag and manual upload

After validation succeeds, create the annotated source tag on the exact commit
in validation.json, and push that branch and that specific tag. No app tag/release
is part of this operation. Do not push all tags or force a branch/tag update.

Sign in at [nuget.org upload](https://www.nuget.org/packages/manage/upload), select
the approved nupkg, review the README, ID, version, MIT, dependencies and source
link, then submit. The signed-in account becomes the initial package owner.
Do not upload an uncommitted preview or a rebuilt package with different bytes.
For first publication, confirm the account is the intended owner before submitting.

Official [NuGet publication instructions](https://learn.microsoft.com/nuget/nuget-org/publish-a-package).
Manual upload remains a fallback; choose one publication path per version. An
automatic run that finds a manually uploaded version verifies its tagged source and
DLL bytes before reporting success.

## Public download verification and recovery

After indexing, on the tagged source commit:

```powershell
$candidate = Get-Content artifacts/nuget/release/0.2.0/validation.json -Raw | ConvertFrom-Json
./tools/Test-LocalPackage.ps1 -PublicFeed -ExpectedCommit $candidate.RepositoryCommit -ExpectedLibrarySha256 $candidate.LibrarySha256
```

This downloads from nuget.org, validates version/source/contents and the approved
DLL hash, then restores both consumers into another empty cache using nuget.org
as the only feed. Both examples must build and run. Nuget.org repository signing
can change the package ZIP hash, so compare the approved DLL bytes and nuspec
metadata rather than demanding the uploaded ZIP and signed download have one hash.

If upload fails, check the package page before retrying; indexing is asynchronous.
If the same version already exists, inspect its source and DLL before claiming
success. Do not skip duplicate errors or overwrite the candidate. If it contains a
mistake, publish a new preview version; unlisting is a separate owner action and
does not make the version reusable. Retain failed validation reports. Do not claim
public consumption succeeded until both public-feed consumers pass.

## 0.2.0-preview.1 release notes

First preview of one Windows/WPF package: an embeddable DXF viewer, immutable
prepared documents including text, entity traversal, double-precision distance
queries, layouts and layer/category controls. Requires Windows and .NET 10 Desktop
Runtime. Existing supported entities and approximation limits are documented in
the package README. Per-entity visibility, removal/editing, DXF writing, DWG and
cross-platform support are excluded. The desktop application remains at 0.2.0.

## Stage 5 local validation

On 2026-10-22 the solution built successfully with zero warnings/errors using an
isolated BaseOutputPath because a running application held the normal output DLL.
All 226 regression cases passed (Tests/TestResults/nuget-stage5.trx); the visual
report is artifacts/test-report/index.html. Official actionlint 1.7.12 passed the
new workflow, and a dirty-source release attempt was correctly rejected. The exact
committed release-candidate consumer outcomes and hashes are recorded separately
in artifacts/nuget/release/0.2.0-preview.1/validation.json after source commit.
Hosted CI results must be checked on the pushed source branch; local validation
does not imply a hosted run has passed. The desktop release workflow is unchanged.
