# VibeDxf v0.4.1

Bug-fix release for the new SVG support.

- **Overlapping SVG dots show the correct color.** Plot files that draw a black point and then a colored point at the same position now show the colored one, as SVG intends. Before, many dots appeared black. Shapes with `stroke="none"` no longer get an outline, and overlapping filled shapes keep their document order.
- **Opening another file while one is loading now works.** The load in progress is cancelled and the newly chosen file opens. Before, the second request was silently ignored. Re-opening the file that is already loading lets that load finish.

See the v0.4.0 notes for the partial SVG import, smooth GPU curves, antialiasing and the background toggle.

## Downloads

- **VibeDxf-Setup-0.4.1-win-x64.exe**: per-user installer with Start menu shortcut, optional desktop shortcut and uninstall support.
- **VibeDxf-0.4.1-win-x64.zip**: portable version. Extract the complete folder and run VibeDxf.exe.
- **SHA256SUMS.txt**: SHA-256 checksums for both downloads.

The embeddable library is released separately as **VibeDxf.Wpf 0.4.1** on NuGet.org. It has no API changes from 0.4.0.

## Requirements and limitations

Windows 10 or later, x64, and the **.NET 10 Desktop Runtime x64**:
https://dotnet.microsoft.com/download/dotnet/10.0

The runtime is not bundled, downloaded or installed automatically. The .NET SDK is not required to run the app. The installer is unsigned.

**SVG support is partial:**
- **Approximate display:** stroke widths are drawn as hairlines, dashes are drawn solid, and gradients use their first stop color.
- **Ignored with a warning:** clip paths, masks, filters, markers, patterns, raster images and `textPath`.
- **Overlap:** strokes are still drawn above fills regardless of document order.

See the README for the full DXF and SVG scope.
