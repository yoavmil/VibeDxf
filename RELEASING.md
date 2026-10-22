# Packaging and releasing VibeDxf

Maintainer instructions for building and publishing Windows downloads. Keep the
root README focused on users; put release implementation details here.

For the separate library package, follow [NuGet release instructions](docs/NUGET-RELEASING.md).
NuGet uses `nuget/v<version>` source tags and manual nuget.org upload. The app's
`v*` tags and release workflow below remain independent.

## Release contract

- Target: Windows 10 or later, x64, `net10.0-windows`.
- Publish a **framework-dependent** app. Do not bundle, download, or install .NET.
  Users install the **.NET 10 Desktop Runtime x64** themselves if necessary.
  The SDK is required for building, not for running the published app.
- Publish a per-user installer, a portable ZIP, and SHA-256 checksums.
- Include the application icon and third-party license notices in the distribution.
- Create an annotated Git tag named `v<version>` for the exact commit built.
- Leave published releases and tags unchanged; corrections get a new version.

## Source files

| File | Purpose |
| --- | --- |
| [packaging/version.txt](packaging/version.txt) | Default release version, without the `v` prefix; currently `0.2.0`. |
| [src/VibeDxf.csproj](src/VibeDxf.csproj) | App version, target framework, dependencies, and executable icon. |
| [packaging/RELEASE_NOTES.md](packaging/RELEASE_NOTES.md) | Release description uploaded to GitHub. Update headings and download filenames for each version. |
| [packaging/requirements.txt](packaging/requirements.txt) | Requirements shown before installation and copied into the portable app as `REQUIREMENTS.txt`. |
| [packaging/Build-Installer.ps1](packaging/Build-Installer.ps1) | Publishes the app, builds the ZIP and installer, and calculates checksums. |
| [packaging/VibeDxf.iss](packaging/VibeDxf.iss) | Inno Setup installer definition. |
| [.github/workflows/release.yml](.github/workflows/release.yml) | Windows CI checks, packaging, install/uninstall verification, Git tag creation, and release publication. |
| [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) | Dependency license notices copied into both distributions. Review when dependencies change. |
| [src/Assets/VibeDxf.ico](src/Assets/VibeDxf.ico) | Multi-resolution Windows icon, with 16–256 pixel frames. Used by the app, installer, uninstaller display, and shortcuts. |
| [src/Assets/VibeDxf.svg](src/Assets/VibeDxf.svg) | Editable vector source for the icon and README artwork. |

The window icon is also referenced in [src/MainWindow.xaml](src/MainWindow.xaml).

## Publish a new release

1. Start from the latest `main`, including any README edits made on GitHub.
   Preserve local work before synchronizing; do not overwrite the user-facing README
   with an older local copy.
2. Choose an unused version in `major.minor.patch` form, for example `0.2.0`.
   Set `packaging/version.txt` and the project `<Version>` to that version.
3. Update `packaging/RELEASE_NOTES.md`, including its title and asset filenames.
   Review requirements, dependency notices, and icon references if those changed.
4. Run the checks locally:

   ```powershell
   dotnet test .\Tests\VibeDxf.Checks.csproj -c Release
   ```

   Public drawing fixtures are checked automatically, including GPU uploads and
   exact unsupported-entity counts. No private drawing files are required.

5. Commit the version, notes, and any app changes together, then push to `main`.
   **Pushing changes under `packaging/` or to the release workflow automatically
   starts publication for the version in `packaging/version.txt`.** Have all release
   changes ready in that push. You normally do not create the tag yourself.
6. Watch the [Windows release workflow](https://github.com/yoavmil/VibeDxf/actions/workflows/release.yml).
   It must pass the app checks, installer build, and installation/uninstallation
   verification before creating the tag and publishing downloads.
7. Verify the [release page](https://github.com/yoavmil/VibeDxf/releases): correct
   version and commit, all three assets, requirements, and working download links.
   The installer should show the VibeDxf icon and offer the optional desktop shortcut.
8. Update the README's direct installer link to the new version. It currently pins
   `v0.2.0`, so publishing a new release does not change that link automatically.

You can also start the workflow from GitHub's **Actions > Windows release > Run
workflow** on `main`. It also accepts pushed `v*` tags, but a tag-triggered run must
agree with `packaging/version.txt`.

## What the workflow does

The workflow runs on `windows-2025`, checks out full Git history, and installs the
.NET 10 SDK using `actions/setup-dotnet`. It uses the runner's Inno Setup 6 compiler
and GitHub CLI. `permissions: contents: write` lets the job create the tag and release;
the CLI receives the job's `github.token` as `GH_TOKEN`. No personal access token is
required. Release jobs are serialized with a repository-wide concurrency group.

After resolving the version, the job:

1. Skips checks, packaging, and publication if that version is already published.
2. Runs the xUnit checks, including off-screen rendering and GPU-buffer reuse checks,
   and uploads TRX results and the HTML visual report as a workflow artifact.
3. Runs `Build-Installer.ps1` to build the framework-dependent distributions.
4. Installs silently into a temporary folder, checks the executable exists and
   `coreclr.dll` is absent, then uninstalls and checks the executable was removed.
   This checks installer behavior on a runner with .NET installed; it does not test
   the missing-runtime user experience on a clean Windows machine.
5. Creates and pushes an annotated `v<version>` Git tag at the checked-out commit.
   If the tag already exists, its target must match that commit.
6. Creates a draft GitHub Release and uploads the installer, ZIP, and checksums.
   An existing draft can have its assets replaced. Finally, it publishes the release
   and marks it latest.

The expected `gh release view` failure for a first release is handled explicitly.
The version step resets `LASTEXITCODE` after that lookup so PowerShell does not
report a successful first-release lookup path as a failed step.

## Build locally

Use Windows with the .NET 10 SDK, restored NuGet dependencies, and Inno Setup 6.
Run from the repository root:

```powershell
.\packaging\Build-Installer.ps1

# Optional explicit version or compiler location
.\packaging\Build-Installer.ps1 -Version 0.2.0 -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

# Publish app and portable ZIP only; no Inno Setup needed
.\packaging\Build-Installer.ps1 -PublishOnly
```

`-Version` overrides the script's default but does not update the project, notes,
or `version.txt`. Keep those consistent when preparing an actual release.
`-PublishOnly` returns before building the installer or generating checksums.

The publish command uses Release configuration, `win-x64`, `--self-contained false`,
`PublishSingleFile=false`, and `PublishTrimmed=false`. The script verifies a .NET 10
`Microsoft.WindowsDesktop.App` requirement in `VibeDxf.runtimeconfig.json` and rejects
bundled `coreclr.dll` or `hostfxr.dll`. It copies the requirements and license notices
into the publish folder before creating the ZIP and installer.

Outputs for version `<version>`:

```text
artifacts/release/<version>/
  publish/                         Complete app folder and dependency DLLs
  downloads/
    VibeDxf-Setup-<version>-win-x64.exe
    VibeDxf-<version>-win-x64.zip
    SHA256SUMS.txt
```

Extract the entire portable ZIP; the executable needs its adjacent files. Generated
files under `artifacts/` are ignored by Git. Use a fresh output folder when rebuilding
locally after removing dependencies, since the script does not clean old publish files.
Local packaging does not create a Git tag or upload anything to GitHub.

## Installer behavior

Inno Setup installs into `%LOCALAPPDATA%\Programs\VibeDxf` without administrator
privileges. It creates a Start menu shortcut and optionally a desktop shortcut
(unchecked by default), includes uninstall support, and offers to launch the app
after interactive installation. Silent installation does not launch it.

Keep the existing installer `AppId` stable across versions so updates identify the
same application. The setup currently targets x64-compatible Windows 10 or later.
The installer is unsigned; Windows may show an unknown-publisher warning. Signing
is not configured in the current build script or workflow.

## Verification and recovery

- Compare downloaded files with `SHA256SUMS.txt`, using
  `Get-FileHash -Algorithm SHA256 -LiteralPath <download-path>`.
- Review the failed workflow step and its logs before retrying. Build or install
  failures occur before tag creation and release publication.
- If publication fails after the tag is created, rerun the same workflow commit.
  A tag pointing to another commit is rejected; do not move a published tag to make
  a newer commit pass. Use a new version for a correction to a published release.
- A published version is skipped on subsequent runs. Changing its notes or source
  and rerunning does not rebuild its assets.
- If local Git or a connector lacks write access, report that limitation explicitly.
  Use ordinary Git commands or an authorized GitHub interface; do not manually edit
  `.git` internals. Publication on GitHub does not synchronize a local checkout.
