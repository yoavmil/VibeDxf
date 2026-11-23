# VibeDxf v0.4.0

Partial SVG support, smooth curves at any zoom, antialiasing, and a white/dark background toggle.

- Open `.svg` and `.svgz` drawings (File › Open or drag and drop). Shapes, the full path syntax including elliptical arcs, transforms, declared physical units, simple CSS, `<use>`/`<symbol>`, text/tspan, opacity, nonzero/evenodd fills and Inkscape layers are supported. Measurement, center/closest strategies and midpoint snapping work as for DXF.
- Large plot-style SVGs load quickly: a 100 MB file with 1.7 million filled circles opens in a few seconds and stays interactive. Filled circles are drawn as GPU instances.
- Béziers and ellipses are tessellated on the GPU for the current zoom, so curves stay smooth when zoomed in deeply.
- All drawings, DXF included, are now rendered with multisample antialiasing.
- View › White background (`Ctrl+B`) toggles the background. SVG drawings open on white and DXF on dark. Toggling does not re-upload geometry.
- Opening a file clears the current drawing immediately and shows a loading message.
- For SVG drawings, the Layers panel offers only a Text toggle.
- The VibeDxf.Wpf library gains additive APIs: `DrawingFormat`, `DxfDocument.SourceFormat`, `DrawingBackground`, `DxfView.BackgroundMode`/`IsLightBackground`/`PlaceholderText`, `DxfEntity.FillColor`/`FillDisc`, and a `LoadAsync(Stream, DrawingFormat)` overload. Regression coverage grows to 365 checks.

## Downloads

- **VibeDxf-Setup-0.4.0-win-x64.exe**: per-user installer with Start menu shortcut, optional desktop shortcut and uninstall support.
- **VibeDxf-0.4.0-win-x64.zip**: portable version. Extract the complete folder and run VibeDxf.exe.
- **SHA256SUMS.txt**: SHA-256 checksums for both downloads.

The embeddable library is released separately as **VibeDxf.Wpf 0.4.0** on NuGet.org.

## Requirements and limitations

Windows 10 or later, x64, and the **.NET 10 Desktop Runtime x64**:
https://dotnet.microsoft.com/download/dotnet/10.0

The runtime is not bundled, downloaded or installed automatically. The .NET SDK is not required to run the app. The installer is unsigned.

**SVG support is partial:**
- **Approximate display:** stroke widths are drawn as hairlines, dashes are drawn solid, and gradients use their first stop color.
- **Ignored with a warning:** clip paths, masks, filters, markers, patterns, raster images and `textPath`.
- **Overlap:** strokes are drawn above fills regardless of document order.

Only ASCII DXF is supported. All existing DXF scope and limits from v0.3.0 are unchanged; see the README for details.

## Library host behavior

Existing APIs and behavior are unchanged apart from rendering: frames are now antialiased, so a 1 px line can spread across two pixel rows. `DxfView.BackgroundMode` defaults to Auto, which is dark for DXF documents and white for SVG.
