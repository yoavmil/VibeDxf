# VibeDxf v0.2.0

A broader 2D DXF viewer with paper layouts, layer controls, text, blocks and annotations.

- Switch between model space and paper layouts. Viewports show their own scale, rotation and frozen-layer state; select and measure model geometry through a sheet viewport in model units.
- Show DXF layer and entity colors, filter layers, solo a layer, restore saved visibility, hide annotation types, or switch to monochrome.
- Render nested block inserts and arrays, visible block attributes, POINT and SOLID entities, TEXT and MTEXT, and solid or patterned HATCH areas with holes and curved boundaries.
- Display saved dimension graphics, legacy leaders and multileaders. These annotations are display-only, so measurements use the underlying geometry.
- Render, select and measure infinite RAY and XLINE entities.
- Improve fill triangulation, handle separate contour groups, and move triangulation off the UI thread.
- Expand the offline xUnit tests and HTML visual report, including public DXF fixtures. Visual Studio Test Explorer discovers the tests.

## Downloads

- **VibeDxf-Setup-0.2.0-win-x64.exe**: per-user installer with the app icon, Start menu shortcut, optional desktop shortcut and uninstall support.
- **VibeDxf-0.2.0-win-x64.zip**: portable version; extract the complete folder and run VibeDxf.exe.
- **SHA256SUMS.txt**: SHA-256 checksums for both downloads.

## Requirements and limitations

Windows 10 or later, x64, and the **.NET 10 Desktop Runtime x64**:
https://dotnet.microsoft.com/download/dotnet/10.0

The runtime is not bundled, downloaded or installed automatically. The .NET SDK is not required to run the app. The installer is unsigned.

Only ASCII DXF and supported planar geometry are rendered. Original SHX fonts, general 3D views, standalone ellipses, external references, gradient hatches, linetypes and lineweights remain unsupported. Text and spline measurements use approximations. See the README for detailed support and rendering limitations.

The local HTML visual test report is generated at `artifacts/test-report/index.html`. Release workflow runs also upload the report and TRX results as an artifact.
