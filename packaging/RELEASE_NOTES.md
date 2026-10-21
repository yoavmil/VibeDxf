# VibeDxf v0.1.0

First Windows release of VibeDxf: a GPU-accelerated DXF viewer and measurement tool.

- Direct3D 11 drawing with Direct2D selection and measurement overlays.
- Open DXF files from the menu, command line, or drag and drop.
- Pointer-centered zoom and middle/right-button panning.
- Exact shortest-distance, signed ΔX and ΔY measurements between elements.
- Straight-segment midpoint snapping and midpoint measurements.
- New VibeDxf application, installer, and shortcut icon.

## Downloads

- **VibeDxf-Setup-0.1.0-win-x64.exe**: per-user installer with Start menu shortcut, optional desktop shortcut, and uninstall support.
- **VibeDxf-0.1.0-win-x64.zip**: portable version; extract the complete folder and run VibeDxf.exe.
- **SHA256SUMS.txt**: SHA-256 checksums for both downloads.

## Requirements

Windows 10 or later, x64. Install the **.NET 10 Desktop Runtime x64** separately if needed:
https://dotnet.microsoft.com/download/dotnet/10.0

The runtime is not included and the installer does not download or install it. The .NET SDK is not needed. Windows includes several .NET versions in different ways; a different .NET major version or the ASP.NET runtime alone does not meet this requirement.

The installer is unsigned; Windows may display an unknown-publisher warning.

Supported geometry: ASCII DXF LINE, ARC, CIRCLE and 2D polylines, including bulges. Blocks/INSERT, text, dimensions, hatches, splines, ellipses, binary DXF and 3D/OCS geometry are not supported yet.
