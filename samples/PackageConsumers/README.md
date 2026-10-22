# Independent NuGet consumers

These examples use only `PackageReference` to **VibeDxf.Wpf 0.2.0-preview.1**. They have no project reference, linked production source, reflection into internals or extra text-preparation provider.

Requirements: Windows 10 or later and .NET 10 SDK with WPF support to build. Running requires .NET 10 Desktop Runtime. The package uses the existing Vortice packages transitively. The preview is local only; it has not been published to NuGet.org.

## Build the local preview and verify both hosts

From the repository root in PowerShell 7:

```powershell
./tools/Test-LocalPackage.ps1
```

The script packs the library into `artifacts/nuget/local/`, checks metadata/contents, copies these sources into a new validation directory, and restores into a fresh isolated package cache. It checks that both consumers received the packed DLL bytes, that the preview came from the local feed, and that dependency resolution was transitive. It then runs document-only and hidden WPF smoke checks against a generated synthetic drawing. Reports, consumer builds, downloaded packages and the fixture remain under `artifacts/nuget/validation/<run-id>/`.

The local package is intentionally marked `repository commit="local-uncommitted"` rather than attributing uncommitted source changes to historical Git HEAD. This local-only provenance marker must be replaced with the exact approved clean source commit before future publication. The script does not commit, push, tag or publish. Restores disable NuGet audit for this local integration check; audit success is not implied.

## Open the WPF sample interactively

After creating the local preview:

```powershell
dotnet restore samples/PackageConsumers/WpfHost/WpfHost.csproj --configfile samples/PackageConsumers/NuGet.Config
dotnet run --project samples/PackageConsumers/WpfHost/WpfHost.csproj --no-restore -- "C:\drawings\example.dxf"
```

The sample demonstrates host-owned file dialogs, a viewer declared in XAML, layout switching, layer checkboxes, entity traversal, interactive selection/measurement events and direct distance queries on selected entity rows. Use Ctrl+click to select two rows, then the measurement button. Programmatic queries also work on hidden layers and annotation graphics; interactive annotations stay display-only. Show all layers preserves viewport freeze rules.

Without a path argument, use Open DXF. Application shutdown cancels a pending load and disposes the viewer. Native-window airspace means host UI is placed alongside the drawing rather than over it.

## Run the console sample

```powershell
dotnet restore samples/PackageConsumers/ConsoleHost/ConsoleHost.csproj --configfile samples/PackageConsumers/NuGet.Config
dotnet run --project samples/PackageConsumers/ConsoleHost/ConsoleHost.csproj --no-restore -- "C:\drawings\example.dxf"
```

The console awaits the same loader, enumerates prepared entities and measures geometry without constructing an Application, viewer, HWND or Vortice graphics device. It still targets Windows/WPF and needs the Desktop Runtime. No caller-side STA setup is required because text shaping uses the library's STA worker.

`--smoke` modes are for the generated fixture in the validation script, not arbitrary drawings. They check an exact three-unit line distance and prepared approximate text geometry. The WPF smoke also checks selection events, layer/category masks, camera conversion and clear/reassignment without rendering errors.

Per-entity visibility, entity removal/editing, undo and DXF saving are excluded from the current package. See the package README and XML API documentation for supported entities, approximation/diagnostic semantics, coordinate contexts, threading, cancellation and ownership.
