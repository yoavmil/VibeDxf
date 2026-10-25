# VibeDxf

<img src="src/Assets/VibeDxf.svg" width="80" height="80" alt="VibeDxf icon">

**[Download the Windows x64 installer](https://github.com/yoavmil/VibeDxf/releases/download/v0.2.0/VibeDxf-Setup-0.2.0-win-x64.exe)**

![VibeDxf measuring from a wavy spline underline to the center of the circle inside b](docs/images/VibeDxf-demo.png)

A truly-free 2D DXF viewer for Windows. I made this because the commercial DXF viewers requires registration.

This viewer can load large files and can measure the shortest distance between any two elements.

Written in C# / WPF on .NET 10, with GPU rendering through Direct3D 11 and Direct2D ([Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)).

It's called `VibeDxf` because it was all written with vibe coding, Claude + Codex.

## Requirements

- Windows 10 or later
- [.NET 10 **Desktop Runtime x64**](https://dotnet.microsoft.com/download/dotnet/10.0) to run the app. It is **not bundled or installed automatically**. The .NET SDK is needed only to build from source.
- A Direct3D 11 capable GPU. Without one, the viewer falls back to WARP, Windows' software rasterizer.

## Build and run

I use VS2026. The build and run are straightforward.

## Supported DXF

Reads ASCII DXF LINE, RAY, XLINE, ARC, CIRCLE, LWPOLYLINE and legacy 2D POLYLINE entities, including curved bulge segments, plus control-point SPLINE curves, POINT markers, filled SOLID faces, single-line TEXT, multiline MTEXT and HATCH areas. Blocks and INSERTs are expanded with nested placement, rotation, scaling, mirroring and rectangular arrays. Click an individual block child to select or measure it. Entity type, layer and instance handle path are kept.

Text uses an Arial font substitute and supports placement, rotation, width, oblique angle, mirroring and alignment. Original SHX/custom font styles are not reproduced. Text and splines use load-time outline approximations; nonuniformly scaled circular curves become approximate ellipses. Their measurements are marked ≈. Solid distances include the filled interior; point markers measure from their exact coordinate. Rays and construction lines (XLINE) are drawn to the window edge at every zoom and measured exactly along their full length; fitting the drawing uses only their base points. Rays/xlines with an out-of-plane direction are skipped.

MTEXT uses WPF’s text layout engine and supports paragraph breaks, reference-width wrapping at word boundaries (long words may overflow), all nine attachment points, rotation/direction vectors, inline height/width/tracking/oblique changes, bold/italic font flags, underline/overline/strike-through, and stacked fractions. Advanced paragraph indentation/alignment is simplified with a warning. Columns, vertical flow and general 3D text orientation are not supported. Background masks/frames are omitted with a warning.

Hatches support solid fills, stored line patterns with dashes/dots, holes and island styles, and polyline, arc, ellipse or control-point spline boundaries. Select a hatch to highlight its pattern strokes, dots and stored contours; measurements use its region rather than individual pattern strokes. Hatch boundaries are read as saved, so a non-associative hatch can differ from nearby outlines.

Dimensions (linear, aligned, angular, radial, diameter, ordinate, arc length and jogged radius) are drawn from the graphics block the CAD program stored with them, including arrowheads and dimension text. Dimensions are display-only: clicking them selects nothing, so measurements always snap to the real geometry underneath. Definition points are hidden. Dimension values are not recalculated, and a dimension without a stored block is skipped with a warning.

Legacy leaders (LEADER) draw their vertices, the hook line to their text, and the arrowhead from the dimension style (size DIMASZ × DIMSCALE, including per-leader overrides and custom arrow blocks). Spline leaders are drawn straight with a rendering note.

Block attributes (ATTRIB) show their values, for example title-block fields and callout numbers. Invisible attributes are not drawn. TEXT and MTEXT control codes are decoded: %%d/%%p/%%c symbols, %%nnn character codes and caret-encoded tabs (shown as spaces). Underline/overline toggles are removed, not drawn.

Multileaders (MULTILEADER) draw their straight leader lines, landing doglegs, closed filled arrowheads and MTEXT content, and are display-only like dimensions. Spline leaders are drawn straight, custom arrowheads as closed filled arrows, and block content is omitted; each case adds a rendering note.

MESH entities are shown as their level-0 wireframe projected onto XY, including face boundaries and explicit edges. Edges parallel to Z become point markers. Picking and measurements use this projected wireframe and ignore Z; it is not a filled region or a true 3D distance representation. Subdivision smoothing is not evaluated: such meshes show their control cage, report a rendering note and mark measurements approximate. Per-face appearance overrides are omitted with a note.

### Layers and colors

Drawings show their DXF colors: the layer color (BYLAYER), an entity's own color index or true color, and BYBLOCK colors inherited from block inserts. Index color 7 shows white on the dark background, and very dark colors are lightened just enough to stay visible. Display-only items are drawn dimmed so it's clear they can't be clicked: dimensions, leaders, multileaders and viewport frames.

Layers start as the drawing saved them: layers that are off or frozen are hidden. The **Layers panel** on the left (View › Layers panel, `Ctrl+L`) lists every layer with its color, element count and saved state. In the panel you can:
- turn layers on and off, search them, show all, or restore the file's saved state;
- double-click a layer to show only that layer;
- show or hide annotation types: dimensions, leaders, multileaders, text, hatches and viewport frames;
- switch the drawing to monochrome.

When saved-off layers hide content, the panel opens by itself with a **Show all** button. Hidden items can't be selected or measured. Layers frozen in a single viewport are hidden only in that viewport. Linetypes (dashes) and lineweights are not applied yet.

### Model and layout

CAD drawings often keep model space as a scratch area: each detail is drawn once at full size, scattered around. The sheets (paper layouts) arrange those details at chosen scales with a title block. When a drawing has real sheets, tabs below the drawing switch between **Model** and each sheet, in the drawing's tab order (`Ctrl+PgUp`/`Ctrl+PgDn` also switch). Most DXF files carry unused default layouts (Layout1, Layout2). A layout only gets a tab if it adds something beyond model space: its own paper-space content, or more than one viewport. Otherwise there are no tabs. A sheet shows its title block and other paper-space content. Each viewport window shows model space at its own scale and rotation, clipped to its frame, as in a plotted sheet. On a sheet, paper-space elements are selected and measured in paper units. Clicking inside a viewport window selects the model geometry shown there, measured in model units; its highlight stays inside that window, and layers frozen in that viewport can't be picked. A measurement never mixes the two: picking a paper element after a model element starts a new selection.

Only top-down (plan) viewports are drawn; 3D views are skipped with a warning. Viewports that are switched off draw nothing. Non-rectangular clip boundaries are shown as their rectangle with a rendering note. Layers frozen in a viewport are hidden in that viewport.

Not supported yet: gradient hatches, excessively dense hatch patterns, multiline attributes, standalone ELLIPSE entities, binary DXF, external references, block clipping/dynamic block behavior and general 3D/OCS geometry. XY-plane OCS with either normal direction is supported for the new planar primitives and inserts. Unsupported entity types trigger a warning and are counted in the footer. Linetypes and lineweights are not applied. Non-ASCII layer names from legacy code pages may need encoding support.

Units come from `$INSUNITS` when present; otherwise the footer says the drawing units are unspecified. No unit conversion is applied.


## Embedding and geometry API

The MIT-licensed Windows/WPF library can be embedded in another application or used to traverse prepared drawing entities and query distances, including text outlines. Install [VibeDxf.Wpf 0.2.0 from NuGet.org](https://www.nuget.org/packages/VibeDxf.Wpf/0.2.0). See the [package README](src/VibeDxf.Wpf/README.md), [independent examples](samples/PackageConsumers/README.md), and [MIT license](LICENSE).
