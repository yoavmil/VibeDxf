# Publishing VibeDxf.Wpf

The NuGet package is independent of the desktop app release. Version 0.2.0-preview.1,
MIT, nuget.org publication and a source tag were approved by the user. The user chose
to upload the finished package through nuget.org; no API key or credential changes
are needed. Publication remains pending until that upload and public verification.

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
It copies plain public consumers, restores from the candidate to a fresh isolated
cache, compares the loaded DLL bytes, and runs console and WPF smoke checks.
No source links, project references or reflection are allowed in consumers.
NuGet audit is disabled for the isolated smoke restore; this is not a security audit.

Upload **only** `artifacts/nuget/release/0.2.0-preview.1/VibeDxf.Wpf.0.2.0-preview.1.nupkg`.
The adjacent `validation.json` and `SHA256SUMS.txt` identify its exact bytes,
library hash, source commit, contents and validation. Preserve this directory.
The harness refuses to replace an existing candidate for the same version.
Development mode without `-Release` remains marked `local-uncommitted` and must
never be uploaded. CI artifacts are retained for 14 days; archive approved release
records separately. GPU checks need hardware or the existing WARP fallback.

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
No publication credentials are stored in this repository, and no automatic
publication workflow has been enabled for this manually uploaded release.

## Public download verification and recovery

After indexing, on the tagged source commit:

```powershell
$candidate = Get-Content artifacts/nuget/release/0.2.0-preview.1/validation.json -Raw | ConvertFrom-Json
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
