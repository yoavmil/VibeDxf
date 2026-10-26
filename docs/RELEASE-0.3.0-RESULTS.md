# Release 0.3.0 results

Published and verified on 2026-10-26 with the user's authorization for both the NuGet package and desktop release.

## Published artifacts

- [VibeDxf.Wpf 0.3.0](https://www.nuget.org/packages/VibeDxf.Wpf/0.3.0)
- [Desktop v0.3.0](https://github.com/yoavmil/VibeDxf/releases/tag/v0.3.0), including Windows x64 installer, portable ZIP and SHA256SUMS.txt.
- Exact source commit: `22320f45cf9ee51469baebdda320f2147bcf752a`.
- Annotated source tags `nuget/v0.3.0` and `v0.3.0` both resolve to that commit.

## Validation

All 358 local Release regression checks passed. The committed local package passed fresh-cache console and WPF consumer checks. Hosted [NuGet checks](https://github.com/yoavmil/VibeDxf/actions/runs/40340639446), [NuGet publication](https://github.com/yoavmil/VibeDxf/actions/runs/40340644151), and [Windows release](https://github.com/yoavmil/VibeDxf/actions/runs/40340639373) completed successfully.

The desktop workflow verified framework-dependent packaging and silent install/uninstall behavior before publication. Both public desktop downloads were downloaded again and matched the published checksums. The NuGet workflow published its tested CI artifact using Trusted Publishing, then verified the publicly indexed package and both isolated public-feed consumers. An independent public download also matched the tested CI library checksum and exact repository commit.

| Artifact | SHA-256 |
| --- | --- |
| CI NuGet candidate ZIP (.nupkg), before repository signing | `B55C39004C8F6665CCF647082DFB91A0C7A84CBCDDFBA3F127E11EC9C8997BCB` |
| Published VibeDxf.Wpf.dll | `E2B3225637B11F4A4BEB029C0DA28D00AD564B505AB079C0F9506FF67C0A2EB5` |
| VibeDxf-Setup-0.3.0-win-x64.exe | `D81C5828C54314E60A041751C59D1DB6F9DF0ED47E8B196ECAC6EE6C85CFFC81` |
| VibeDxf-0.3.0-win-x64.zip | `E22A865010EA782815971C25FAEB49823915D72B668E080A6486C9A95C1D94B0` |

The CI candidate, public consumer reports, public package, desktop downloads and verification metadata are archived locally under `artifacts/release-0.3.0-evidence/`. CI artifacts also remain available through the linked workflow runs for their configured retention periods.

## Included behavior

Saved cameras and perspective/clipping/depth display, 3D polyline/mesh wireframes, straight/elliptical and explicit spline ACIS boundaries, tolerant topology, all 13 Waschbecken solid wireframes, and complete untrimmed ring-torus display. Nonplan views remain display-only; measurements use projected XY. General trimmed ACIS face shading, lighting, orbit navigation, editing and writing remain unsupported. Windows and .NET 10 Desktop Runtime are required; the runtime remains unbundled.
