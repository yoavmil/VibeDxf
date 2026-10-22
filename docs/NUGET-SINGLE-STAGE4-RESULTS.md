# Single-package Stage 4 results

Date: 2026-10-22. Status: complete. User approved "do stage 4", chose **MIT** and **VibeDxf.Wpf 0.2.0-preview.1**, and explicitly declined Stage 3. Per-entity visibility/removal were not implemented. Desktop application version remains **0.2.0**.

## Package and documentation

- Added the root MIT LICENSE using Yoav Miller and contributors as the copyright holder. Existing dependency/fixture notices and Git attribution were preserved. The app project copies LICENSE into its output/publish payload.
- Enabled packing for the single Windows/WPF library with approved ID/version, authors, description/tags, repository/project links, MIT expression, package release notes and README. Enabled XML API documentation and documented all compiler-visible public API declarations; package/consumer builds report no missing-documentation warnings.
- The inspected package contains one managed DLL and its XML documentation under `lib/net10.0-windows7.0`, root README, LICENSE, third-party notices and standard NuGet metadata. The framework folder is the SDK's normalized form of the existing `net10.0-windows` target; supported runtime remains Windows 10+/.NET 10 Desktop Runtime. No Core library, app icon/EXE, DXF fixture, private drawing, test/source file, PDB or native renderer interface was included as package content.
- Declared dependencies are the existing Vortice.D3DCompiler, Vortice.Direct2D1 and Vortice.Direct3D11 **3.8.3**, plus the Windows Desktop/WPF framework reference. They restore transitively; consumers do not duplicate those references.
- The public flat-container lookup for VibeDxf.Wpf returned HTTP 404 during this stage. This records the current absence of a listed package; it does not reserve the ID or establish publication rights.
- Updated the active plan to mark Stage 3 skipped, and current documentation no longer promises entity editing/visibility APIs. Added public package README and independent-consumer instructions. Historical design proposals are explicitly marked as superseded where necessary.

Local artifact:

```text
artifacts/nuget/local/VibeDxf.Wpf.0.2.0-preview.1.nupkg
SHA-256: 6E2C83FBFF13189019A47679B5A0ED29EC4C7D29EBDAA7E4D90C01928571F068
```

This package represents the **uncommitted working tree**, not historical HEAD `77ac185`. The local validation command deliberately writes repository commit `local-uncommitted` and omits the Git suffix from informational version. Fresh nuspec output prevents MSBuild's incremental-pack cache from retaining stale provenance. Stage 5 must replace this local-only marker with the approved exact clean source commit before publication. No package has been pushed to any remote feed.

## Independent consumption

Added `samples/PackageConsumers/ConsoleHost` and `WpfHost`, each with only an exact `PackageReference` to the approved preview. They use no ProjectReference, repository production source links, friend assembly names, reflection into internals or consumer-supplied text geometry provider.

`tools/Test-LocalPackage.ps1`:

1. Restores/builds/packs the library and inspects metadata, dependency list, license, XML docs and excluded file types.
2. Copies plain sample sources into a new validation directory and generates an isolated NuGet.Config with source mapping: VibeDxf.Wpf comes only from the local feed, dependencies from NuGet.org.
3. Restores both consumers into a fresh package cache, checks `project.assets.json` identifies the library as a package, checks the package's recorded source, and verifies output DLL hashes equal the packed DLL bytes.
4. Builds/runs both hosts against a generated synthetic drawing containing two lines separated by exactly three units, a saved-hidden layer and MTEXT.
5. Leaves evidence under the unique validation directory and does not commit/push/tag/publish anything.

Actual validation directory:

```text
artifacts/nuget/validation/d54fe9a506804f9db7c7458dba427adc/
  summary.json
  console-output.txt
  wpf-smoke.json
  package-library.dll
  packages/              isolated restored dependency cache
  consumers/             standalone copied sources/builds
```

Both standalone builds passed with **0 warnings, 0 errors**. Console checks passed traversal and a distance of **3**, plus prepared approximate text geometry, with no WPF Application or Vortice assemblies initialized. It still targets Windows/WPF and requires the Desktop Runtime.

The WPF sample instantiated the packaged control through XAML and passed source-based selection and measurements, **9 measurement notifications**, layer/category visibility, hidden-source queries, prepared-text queries, camera conversions, clear and document reassignment, with **0 rendering errors**. It ran in a hidden/off-screen native window and closed/disposed normally. Its interactive UI also demonstrates host-owned file selection, layout choices, layer checkboxes, entity rows and code-based row measurements.

The synthetic package smoke is not an assertion of complete DXF coverage. The full fixture/GPU regression suite supplies the broader geometry/layout coverage.

## Installer and regression validation

The machine had no installed Inno compiler in the standard locations. Downloaded official [Inno Setup 6.7.3](https://jrsoftware.org/isdl.php) into ignored workspace artifacts, checked its Authenticode status **Valid** and publisher **Pyrsys B.V.**, then used its documented [portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm). The compiler remains under `artifacts/tools/inno-6.7.3`; no .NET runtime was installed or bundled.

Copied source/packaging files into a fresh isolated directory and ran the **unchanged** Build-Installer.ps1 and VibeDxf.iss with the explicit compiler path. This avoided overwriting previous local release artifacts or changing publication-triggering packaging/workflow files.

```text
artifacts/nuget/installer-validation/a0f5621b28984c82a1517f38df549bfb/
  installer-validation.json
  artifacts/release/0.2.0/downloads/
    VibeDxf-Setup-0.2.0-win-x64.exe
    VibeDxf-0.2.0-win-x64.zip
    SHA256SUMS.txt
```

The installer compiler succeeded. Payload contains VibeDxf.Wpf.dll, LICENSE, dependency notices and requirements. Runtimeconfig still requires Microsoft.NETCore.App 10.0.0 and Microsoft.WindowsDesktop.App 10.0.0, with no bundled coreclr.dll/hostfxr.dll. Installer script hash matches the original; AppId, app version and framework-dependent policy remain unchanged. This stage verifies an actual installer build, not installation/uninstallation on a clean machine.

Final xUnit suite: **226 passed, 0 failed, 0 skipped**, duration **39 seconds**. TRX: `Tests/TestResults/single-package-stage4.trx`. Visual report regenerated at `artifacts/test-report/index.html`. Existing supported/skipped/public-layout/GPU assertions remain intact. No production behavior was changed in Stage 4; library edits are API comments, enum formatting and package metadata. App project adds license copying only.

## Reproduce and remaining boundary

```powershell
./tools/Test-LocalPackage.ps1
dotnet test Tests/VibeDxf.Checks.csproj -c Release --no-restore --disable-build-servers --logger "trx;LogFileName=single-package-stage4.trx"
```

The harness requires PowerShell 7 and access to NuGet.org for its fresh dependency cache. Its restores disable NuGet audit for local integration testing, so audit success is not claimed. It launches only its own hidden WPF smoke process, with a 30-second timeout. See `samples/PackageConsumers/README.md` for manual interactive/console commands.

Stage 4 exit criteria are met: the approved one-package preview is locally packed, inspected and consumed independently; licensing/docs are present; the existing installer still builds. Work remains uncommitted. No new Git commit, push, tag, credential/security change, remote package upload or app release was performed. Stage 5 (CI/publication preparation) and Stage 6 (publication) remain separately unapproved. Physical multi-monitor DPI, real device-loss recovery, fresh-machine install/uninstall and public-feed consumption remain outside this stage's claims.
