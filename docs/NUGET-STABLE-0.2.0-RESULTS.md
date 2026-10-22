# Stable NuGet 0.2.0 release

Published and verified on 2026-10-22 under the user's explicit approval.

- Package: [VibeDxf.Wpf 0.2.0](https://www.nuget.org/packages/VibeDxf.Wpf/0.2.0),
  stable, MIT, owned by `yoav.miller`.
- Exact source: `7d91a86418875742950d5c5c13c1ad96570d2b35`, annotated
  [`nuget/v0.2.0`](https://github.com/yoavmil/VibeDxf/tree/nuget/v0.2.0).
- [Automated release run](https://github.com/yoavmil/VibeDxf/actions/runs/39782252560)
  passed: source tag/version verification, app/library build, all 226 regression
  checks, isolated WPF/console candidate consumers, actual Trusted Publishing OIDC
  authentication, actual NuGet push, public download and isolated public consumers.
- Public package source/version and DLL bytes match the tested candidate. The WPF
  smoke reported no rendering errors; both consumers restored into a fresh cache
  using nuget.org as the only feed.

The stable release promotes the verified preview without public API or runtime
behavior changes. Version metadata, packaged README, release notes and sample
references were updated. The desktop application remains at 0.2.0; its packaging
and release workflow were unchanged and no application release was triggered.
The earlier prerelease and both source tags remain immutable.

SHA-256 records:

| Artifact | SHA-256 |
| --- | --- |
| Tested upload package | `69345939E622FD594253DFB3313BDDAE2684D72DE6A9968C869651BC44A6A53B` |
| Signed public download | `74599CD67F40AA7D7D99A51E2B85B2B801AFD5AA7690EA652D89D6A35F8F2D63` |
| Library DLL in both | `4773206538D1CA748738672C0B3EA80675402D5D6A4A283F6261E211E1C082A9` |

NuGet accepted the upload before indexing finished. The workflow waited and then
verified successful public consumption; no duplicate push was needed. Ignored
local evidence, candidate, signed download and public reports are retained under
`artifacts/nuget/stable-0.2.0/`. CI candidate/public reports expire after 90 days.

A subsequent workflow-only retry fix downloads by the validation job's artifact
ID, so retries can retain the original tested package even when only the publishing
job reruns. Official actionlint 1.7.12 passed this fix. It does not change the
published package or its source tag.

See [release instructions](NUGET-RELEASING.md) for future versions and recovery.
The documented Windows/.NET Desktop requirements, approximation limits and excluded
editing APIs are unchanged.
