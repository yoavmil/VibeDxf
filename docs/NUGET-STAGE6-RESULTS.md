# NuGet publication and automation results

Completed 2026-10-22. Stages 5 and 6 are complete. The user subsequently requested
automated NuGet publishing and explicitly approved creation of the scoped NuGet
trust policy and preview publication.

## Public package and source

- Package: [VibeDxf.Wpf 0.2.0-preview.1](https://www.nuget.org/packages/VibeDxf.Wpf/0.2.0-preview.1).
- Package owner: `yoav.miller`; license: MIT.
- Exact source: `f019536e2e7992de1048563c2636a524784f2828`, annotated tag
  [`nuget/v0.2.0-preview.1`](https://github.com/yoavmil/VibeDxf/tree/nuget/v0.2.0-preview.1).
- The public DLL SHA-256 equals the original approved local candidate:
  `36F88A818DD556EAD868D6F6169786FF38A05972DBF1786C46163ED30C935696`.
- The original upload candidate SHA-256 was
  `7B025EBCE50F5360886874F2D61737B813B791DC3A354B46B441FF3A53CD3FB8`.
  The signed public package ZIP SHA-256 is
  `34885A0A5DB500BE0EEABCBE5C3D1A6EA94079E3EF9518E17F45A116A876A9AF`.
  ZIP hashes differ after repository signing; library bytes and source metadata
  were checked independently.

The package became indexed during automation setup and matches the original manual
upload candidate. The initial automated attempt skipped the push and rejected the
different DLL checksum from a fresh CI rebuild. Existing-release verification was
then given the original approved candidate's DLL checksum. No published version
was overwritten, unlisted or replaced, and the source tag was not moved.

## Automation installed and tested

[Pull request 1](https://github.com/yoavmil/VibeDxf/pull/1) was merged with source
history preserved. Automation was added in `70630a7`; existing-release verification
support in `8abb656`. Local main was synchronized. The existing app release workflow
and packaging files were unchanged relative to the remote main baseline; no desktop
app release was triggered.

- `nuget-checks.yml`: read-only Windows build, regression/GPU checks, pack and
  isolated package consumer checks.
- `nuget-publish.yml`: annotated `nuget/v*` tag publication plus manual dispatch
  with validation-only mode; a separate job publishes the tested artifact after
  SHA-256 validation, then verifies public-feed consumption.
- Repository variable `NUGET_USER=yoav.miller` was configured.
- The active NuGet trust policy permits only `VibeDxf.Wpf` pushes from repository
  `yoavmil/VibeDxf` and workflow `nuget-publish.yml`. Environment is unset;
  unlist/relist permission is disabled. No long-lived NuGet API key was created.
- Official actionlint 1.7.12 passed the workflows. The updated package harness
  passed local WPF/console smoke checks and binds copied samples to the candidate
  version without editing repository sample projects.

[Validation-only publishing run](https://github.com/yoavmil/VibeDxf/actions/runs/39777136503)
passed with the publishing job skipped. The final
[authentication and public verification run](https://github.com/yoavmil/VibeDxf/actions/runs/39778943708)
passed: build, all 226 regression checks, both local-feed consumers, candidate
provenance, actual OIDC authentication and both public-feed consumers. It verified
the public DLL checksum and tagged source metadata using an empty package cache
and nuget.org as the only source. WPF rendering reported no errors.

An automated new-version push was not performed because this version was already
published. The actual OIDC exchange and public-consumer path were exercised; the
new-version push command is configured for the next unused version/tag. The
`existing_library_sha256` input is accepted only for a version already indexed on
nuget.org and never changes the artifact pushed for a new version.

## Retained evidence and next release

Ignored local evidence is under `artifacts/nuget/automation/`: the original and
CI candidate records, public download, successful public verification artifact,
workflow-run metadata and screenshot of the active policy. The original manual
candidate remains in `artifacts/nuget/release/0.2.0-preview.1/`. CI candidate/public
reports are retained for 90 days; regression reports for 14 days.

For a new release, update the library version and release notes, commit, create an
annotated `nuget/v<version>` tag and push that tag. Leave existing-release checksum
input empty for new versions. Full instructions and recovery behavior are in
[NUGET-RELEASING.md](NUGET-RELEASING.md). Public sample restore commands use
`samples/PackageConsumers/NuGet.Public.Config`.

Per-entity visibility/removal remain excluded. Windows/.NET 10 Desktop Runtime,
text/font approximations and other documented library limitations still apply.
