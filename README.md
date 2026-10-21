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

Reads ASCII DXF LINE, RAY, XLINE, ARC, CIRCLE, LWPOLYLINE and legacy 2D POLYLINE entities, including curved bulge segments, plus control-point SPLINE curves, POINT markers, filled SOLID faces, single-line TEXT, multiline MTEXT and HATCH areas. Blocks and INSERTs are expanded with nested placement, rotation, scaling, mirroring and rectangular arrays. Click an individual block child to select or measure it. Entity type, layer and instance handle path are kept.

Text uses an Arial font substitute and supports placement, rotation, width, oblique angle, mirroring and alignment. Original SHX/custom font styles are not reproduced. Text and splines use load-time outline approximations; nonuniformly scaled circular curves become approximate ellipses. Their measurements are marked ≈. Solid distances include the filled interior; point markers measure from their exact coordinate. Rays and construction lines (XLINE) are drawn to the window edge at every zoom and measured exactly along their full length; fitting the drawing uses only their base points. Rays/xlines with an out-of-plane direction are skipped.

MTEXT uses WPF’s text layout engine and supports paragraph breaks, reference-width wrapping at word boundaries (long words may overflow), all nine attachment points, rotation/direction vectors, inline height/width/tracking/oblique changes, bold/italic font flags, underline/overline/strike-through, and stacked fractions. Advanced paragraph indentation/alignment is simplified with a warning. Columns, vertical flow and general 3D text orientation are not supported. Background masks/frames are omitted with a warning.

Hatches support solid fills, stored line patterns with dashes/dots, holes and island styles, and polyline, arc, ellipse or control-point spline boundaries. Select a hatch to highlight its pattern strokes, dots and stored contours; measurements use its region rather than individual pattern strokes. Hatch boundaries are read as saved, so a non-associative hatch can differ from nearby outlines.

Dimensions (linear, aligned, angular, radial, diameter, ordinate, arc length and jogged radius) are drawn from the graphics block the CAD program stored with them, including arrowheads and dimension text. Dimensions are display-only: clicking them selects nothing, so measurements always snap to the real geometry underneath. Definition points are hidden. Dimension values are not recalculated, and a dimension without a stored block is skipped with a warning.

TEXT and MTEXT control codes are decoded: %%d/%%p/%%c symbols, %%nnn character codes and caret-encoded tabs (shown as spaces). Underline/overline toggles are removed, not drawn.

Multileaders (MULTILEADER) draw their straight leader lines, landing doglegs, closed filled arrowheads and MTEXT content, and are display-only like dimensions. Spline leaders are drawn straight, custom arrowheads as closed filled arrows, and block content is omitted; each case adds a rendering note.

Not supported yet: gradient hatches, excessively dense hatch patterns, variable attributes, legacy LEADER entities, standalone ELLIPSE entities, binary DXF, external references, block clipping/dynamic block behavior and general 3D/OCS geometry. XY-plane OCS with either normal direction is supported for the new planar primitives and inserts. Unsupported entity types trigger a warning and are counted in the footer. All supported ENTITIES records are shown together, without layout filtering, layer visibility, colors or linetypes. Non-ASCII layer names from legacy code pages may need encoding support.

Units come from `$INSUNITS` when present; otherwise the footer says the drawing units are unspecified. No unit conversion is applied.
