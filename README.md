# VibeDxf

<img src="src/Assets/VibeDxf.svg" width="80" height="80" alt="VibeDxf icon">

**[Download the Windows x64 installer](https://github.com/yoavmil/VibeDxf/releases/download/v0.1.1/VibeDxf-Setup-0.1.1-win-x64.exe)**

![VibeDxf measuring from a wavy spline underline to the center of the circle inside b](docs/images/VibeDxf-demo.png)

A truly-free 2D DXF viewer for Windows. I made this because the commercial DXF viewers requiers registration.

This viewer can load large files and can measures the shortest distance between any two elements.

Written in C# / WPF on .NET 10, with GPU rendering through Direct3D 11 and Direct2D ([Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)).

It's called `VibeDxf` because it was all written with vibe coding, Claude + Codex.

## Requirements

- Windows 10 or later
- [.NET 10 **Desktop Runtime x64**](https://dotnet.microsoft.com/download/dotnet/10.0) to run the app. It is **not bundled or installed automatically**. The .NET SDK is needed only to build from source.
- A Direct3D 11 capable GPU. Without one, the viewer falls back to WARP, Windows' software rasterizer.

## Build and run

I use VS2026. The build and run are straightfoward.

## Supported DXF

Reads ASCII DXF LINE, ARC, CIRCLE, LWPOLYLINE and legacy 2D POLYLINE entities, including curved bulge segments, plus control-point SPLINE curves. Spline distances are approximate. Each polyline is one selectable element. Entity type, layer and handle are kept.

Not supported yet: INSERT/block references, text, dimensions, hatches, ellipses, binary DXF, and 3D/OCS geometry. Unsupported entity types are counted in the footer. All supported ENTITIES records are shown together, without layout filtering, layer visibility, colors or linetypes. Non-ASCII layer names from legacy code pages may need encoding support.

Units come from `$INSUNITS` when present; otherwise the footer says the drawing units are unspecified. No unit conversion is applied.
