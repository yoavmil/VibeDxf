# SVG support plan

Status: **steps 1–6 implemented (2026-11-23), not committed.** Stage 4 fidelity items (widened strokes, dashes, clip paths, markers) and the paint-order renderer stage remain open. No package or desktop release is authorized by this plan.

## Goal

Open `.svg` and `.svgz` files in the same viewer and library as DXF, with the same guarantees:

- immutable prepared documents;
- analytic XY measurement that does not depend on zoom;
- center/closest strategies, plus midpoint snapping on exact straight segments;
- GPU buffers uploaded once per document;
- honest diagnostics for anything skipped or approximated.

SVG support covers import and viewing only. Exporting to SVG is out of scope.

## Key constraint: reuse the DXF pipeline unchanged

Everything after loading already works on format-neutral data: `DrawingBuilder`, `DxfEntity` (curves, fill contours, render curves, color and layer), `DxfDocument` freezing, `Triangulate`, `DrawingRenderer`, `DxfViewport` and `MeasurementTarget`. SVG support is therefore mostly a **new reader** that fills a `DrawingBuilder`:

```
.svg/.svgz ─► SvgReader ─► DrawingBuilder ─► DxfDocument (unchanged) ─► DxfView / renderer (unchanged)
.dxf       ─► DxfReader ─┘
```

There are no new NuGet packages. `System.Xml`, `System.IO.Compression` and the WPF geometry engine (`System.Windows.Media.PathGeometry`, already used for text) cover everything the reader needs.

## Design decisions (recommended defaults, confirm before Stage 1)

| # | Decision | Recommendation |
|---|----------|----------------|
| D1 | Public API shape | Keep `DxfDocument`/`DxfView` as the general drawing types; renaming them would break the published 0.3.0 API. `LoadAsync(path)` detects the format from the extension, then from the content. Add `LoadAsync(Stream, DrawingFormat format, …)` and a `DxfDocument.SourceFormat` property. This is an additive change, so the next package would be a minor version (0.4.0). |
| D2 | Coordinates and units | Apply the root `viewBox`→viewport transform, so coordinates are in the root's declared physical unit. For example, `width="210mm"` gives `Units = "mm"`. Unitless or `%` sizes stay in SVG user units, labelled `px (SVG user units)`. |
| D3 | Y axis | Flip y-down to the viewer's y-up world at load (`y' = −y`). The drawing then appears upright. Signed ΔY follows the viewer's convention, which is the opposite of the SVG source. Document this. |
| D4 | Stroke width | Phase 1 draws hairlines at the centerline. This is CAD semantics and matches how DXF lineweights are ignored today. Measurement always uses the centerline. Widened strokes come in Stage 4 as an option. |
| D5 | Dark background (**superseded:** see Decisions) | SVGs are authored for white paper: the default fill and stroke are black, and they often contain a full-canvas white background rect. Recommendation: keep the dark viewer and existing luminance lifting, and treat a rect covering the root viewport as a display-only page background that is hidden by default. A light canvas mode for SVG documents is the alternative (see "Open questions"). |
| D6 | Entity kinds | Use the uppercase element names: `LINE`, `RECT`, `CIRCLE`, `ELLIPSE`, `POLYLINE`, `POLYGON`, `PATH`, `TEXT`. Because `CIRCLE` matches the DXF name, the Center strategy works without changes. The `Kind` XML docs need to say "source entity kind" instead of "DXF". |
| D7 | Layers | Inkscape layers (`<g inkscape:groupmode="layer">`, named by `inkscape:label`) become `DxfLayer`s, and `display="none"` becomes the saved "off" state. Without Inkscape layers, top-level `<g id>` groups become layers. Everything else goes on layer `0`. |
| D8 | Handles | `SourceHandle` is the element `id`, or a generated element path such as `svg/g[2]/path[5]` when there is no id. `<use>` instances add the instance path, like INSERT expansion does. |

## Geometry mapping

| SVG | Measurement `Curves` | Exact? | Midpoint snaps |
|-----|----------------------|--------|----------------|
| `line`, `polyline`, `polygon`, `rect` (no `rx`) | `Curve.Line` segments | yes | yes |
| `rect` with `rx`/`ry` | lines plus quarter arcs (circular when `rx == ry` after transform) | yes / approx. | straight sides only when exact |
| `circle`, and circular `A` arcs under a similarity transform | `Curve.Arc` | yes | n/a |
| `ellipse`, elliptical arcs, non-uniformly transformed circles | flattened chords within 0.0001 units | approximate | no |
| path `C/S/Q/T` Béziers | flattened chords within 0.0001 units (same bound as SPLINES.md) | approximate | no |
| path `M/L/H/V/Z` | `Curve.Line` | yes | yes, unless the path is approximate |

Midpoint eligibility is per entity (`IsApproximate`), not per curve. A path that mixes straight segments and Béziers is therefore approximate as a whole, which means its straight segments lose their midpoints. That keeps the existing rule that "chords never become midpoint targets" without changing the public `Curve` record. A later refinement could split such paths per subpath.

**Fills:** the existing triangulator is even/odd only. A `nonzero` fill is converted by building a WPF `PathGeometry` with `FillRule.Nonzero` and taking `GetOutlinedPathGeometry()`, which removes overlaps so even/odd gives the same result. Then `FillContours` comes from the flattened outline. `RegionContours` mirrors the fill, so a click inside a filled shape measures as inside, which is the hatch behavior. Shapes with `fill="none"` get an empty `RegionContours`, so they behave as boundaries only.

**Visibility:** `fill="none" stroke="none"`, `display="none"` and `visibility="hidden"` elements are not rendered. Hidden layers keep their geometry under the saved-off state. Other hidden elements are dropped and counted.

## Supported subset by stage

### Stage 1: core reader (geometry, style and structure)
- Secure XML loading: `DtdProcessing.Ignore`, no `XmlResolver`, a size limit, and no fetching of external `href`s. `.svgz` is read through `GZipStream`. Content sniffing checks for `<` after a BOM or whitespace, or the gzip magic bytes `1F 8B`.
- Root and nested `<svg>`: `viewBox`, `width`/`height` in `px mm cm in pt pc` and unitless, and `preserveAspectRatio`.
- `transform` (`matrix translate scale rotate skewX skewY`) composed down the tree.
- Shapes: `line rect circle ellipse polyline polygon path`. Path data uses a full SVG grammar parser (implicit repeats, compact arc flags, relative commands). This is a custom parser: WPF's `Geometry.Parse` is close to SVG but not identical, and it would lose the analytic arcs that measurement needs.
- `<g>`, `<defs>`, and `<use>`/`<symbol>`, expanded like INSERT, with cycle, depth and entity-count limits that protect against "billion laughs" style files.
- Styling: presentation attributes, the `style` attribute, inheritance, and `<style>` CSS limited to type, `.class`, `#id` and grouped selectors. This covers Illustrator and Inkscape output. `!important` and combinators are not supported and produce a warning.
- Paint: named colors, `#rgb`, `#rrggbb`, `rgb()`, `currentColor`, `fill-opacity`/`stroke-opacity`/`opacity`, and `fill-rule`. A stroke or fill becomes the entity's `Color`.
- Diagnostics go through the existing `Skipped`/`RenderingWarnings`, so the footer, warning dialog and `LoadDiagnostics` work unchanged.

### Stage 2: text
- `<text>` and `<tspan>` with `x`/`y`/`dx`/`dy`, `font-size`, `font-family` (with WPF font fallback, so it is not limited to Arial), `font-weight`/`font-style` and `text-anchor`.
- Text is shaped on the existing STA loader through the code path in `DxfPrimitives`/`DxfMText`, and gets the `Text` category.
- `textPath`, vertical text and per-glyph `rotate` produce warnings.

### Stage 3: app and API integration
- Format detection and `SourceFormat` (D1). The "No supported geometry" error message becomes format-aware.
- `MainWindow`: the open dialog filter becomes `Drawings (*.dxf;*.svg;*.svgz)`, plus drag-drop and the title or footer.
- README gets a "Supported SVG" section, and CLAUDE.md gets an architecture note. Public XML docs drop "DXF-only" wording where it is now wrong.
- `DxfView` has no saved cameras for SVG, so it fits to bounds. Layout tabs stay hidden because there are no layouts.

### Stage 4: fidelity (optional, each item separately approvable)
- **Widened strokes:** display-only fill from `GetWidenedPathGeometry(Pen)` with caps, joins, miter limit and `stroke-dasharray`. A centerline hairline is still drawn, so thin strokes stay visible when zoomed out. Measurement stays on the centerline.
- **Dashes on hairlines:** split the `RenderCurves` like hatch dash phases.
- **`clipPath`:** intersect through WPF `Geometry.Combine` for display, and clip the measurement curves.
- **Markers:** expand them like DXF leader arrow blocks, as display-only annotations.
- **Gradients and patterns:** fill with a representative stop color and add a rendering note, the same way gradient hatches warn.

### Out of scope (counted as skipped with a warning)
Filters, masks, raster `<image>`, `<foreignObject>`, scripts, animation (SMIL/CSS), external resources, and complex CSS such as combinators, pseudo-classes and media queries.

## Known renderer limitation: paint order

The renderer batches by primitive type, so all fill triangles are in one list and all lines in another. Fills keep document order among themselves, but a stroke of an earlier element can appear above the fill of a later one. CAD-style and line-art SVGs are unaffected. Layered illustrations may look wrong where strokes and fills overlap. A fix would assign per-entity depth through the existing D32 depth buffer, and translucency complicates it. That is a separate renderer stage and needs approval, because it touches the "geometry uploaded once" invariants and their checks.

## Testing

- `Tests/SvgChecks.cs` holds direct xUnit tests with GPU captures in the HTML report, like `PrimitiveChecks`. The synthetic fixtures go in `Tests/Fixtures/svg/`:
  - every shape;
  - path grammar edge cases (implicit commands, compact flags, degenerate arcs);
  - transforms, including a non-uniform transform on a circle;
  - units and viewBox;
  - nonzero vs. evenodd fills;
  - `<use>` recursion limits;
  - CSS classes;
  - Inkscape layers;
  - text.
- Numeric checks:
  - circle-to-line distance is exact;
  - midpoints are absent on Bézier paths;
  - the Center strategy works on `<circle>`;
  - `mm` scaling is correct;
  - the y-flip sign is correct.
- Security checks: DTD/entity expansion is ignored, external `href` is never fetched, and `<use>` bombs hit the limits.
- Smoke fixture: `src/Assets/VibeDxf.svg`, the app icon that is already in the repo.
- Optional public fixtures with a provenance README, as `Tests/Fixtures/Public` has. Candidates are the resvg test suite (MIT) and the W3C SVG 1.1 suite (W3C license). Licenses need to be confirmed before anything is vendored.
- `tools/DocumentSmoke` is extended to load an SVG without WPF Application or Vortice initialization.
- The existing checks must keep passing, including the GPU-buffer-reuse invariants. New named catalog checks are added to `Tests/CheckCatalog.cs`.

## Files expected to change

New files:
- `src/VibeDxf.Wpf/SvgReader.cs`: XML walk, style cascade, `<use>` expansion and layers.
- `SvgPathData.cs`: path grammar and arc conversion.
- `SvgStyle.cs`: CSS subset and paint parsing.

Edited files:
- `DxfDocument.cs` (format detection and overloads)
- `DxfReader.cs`: the last-step triangulation and freeze move into a shared helper so both readers use them.
- `MainWindow.xaml.cs`
- `README.md`
- `CLAUDE.md`
- test project files

`DrawingRenderer`, `DxfViewport` and `MeasurementTarget` are not expected to change until Stage 4 and the paint-order stage.

## Decisions (user, 2026-11-23)

1. Scope is import and view only. Export is out of scope.
2. D5 is replaced: the background can be toggled, and **SVG documents default to a white background**. DXF stays dark. Toggling must not re-upload geometry, so it may only change the clear color and the dark/light color adaptation in the constant buffer or shader. If the luminance floor is applied on the CPU at upload today, it has to move to the shader. Check this first. The page-background rect is then drawn normally.
3. D2 is confirmed: units follow the file's declarations when present, and are SVG user units otherwise.
4. Real-world target files are generated plots, not illustrations (see the starter profile below). Work proceeds in small steps.

## Starter profile (real files)

The two sample files come from the same generator and have the same shape:

| File | Size | Elements |
|------|------|----------|
| `D:\TGV\BOE(284718)0\splines.svg` | 57 MB | 569,436 `circle`, 1,102 `path`, 1,102 `text`, 1,104 `g` |
| `D:\TGV\CT_project\Maps\svg\TCL01PA0-GC01A.svg` | 102 MB | 1,677,952 `circle`, 2 `g` |

Their structure:
- **Root:** `width`/`height` are unitless and equal the `viewBox` size, so the scale is 1:1 and coordinates are in user units. There are no transforms, no CSS and no `style` attributes.
- **`circle`:** `r="1.0"` with a named `fill` (black, red, Crimson, …) and no stroke. These are point markers.
- **`path`:** only `M` + absolute `C` cubic Béziers (288k segments), with `fill="none" stroke=<named> stroke-width="1"`.
- **`text`:** only `x`/`y`/`fill="gray"`, with no font-size (SVG default 16px).
- **Ids:** they repeat (`id="points"`, `id="Curve"` many times), so ids are not unique. A handle needs the element path when ids are duplicated.

So the first useful slice is: `svg`/`g`/`circle`/`path` (M/L/C/Z)/`text`, named and hex colors, and `fill`/`stroke`/`stroke-width` attributes.

## Large files

- **XML:** use a forward-only `XmlReader`, never `XDocument`/`XmlDocument`. The streaming reader parses 100 MB in seconds with constant memory, while a DOM would cost several times the file size. `<use>`/`<defs>` need random access by id, so only `<defs>`/`<symbol>` subtrees are buffered. Forward `href` references are resolved after the pass.
- **The real cost is downstream.** 1.7M filled circles as ordinary fill triangles would be too much: even a 16-segment disc gives about 24M triangles and over 1 GB of vertices, plus CPU triangulation. Instead, **filled circles render as GPU-instanced discs**, a fill variant of the existing `ArcVS` instancing. That is one instance per circle (≈ 40 MB), uploaded once, so the buffer-reuse invariant holds. Their measurement curve stays one exact `Curve.Arc`, and their region is the analytic disc, so no `FillContours` polygon is needed.
- **Per-entity overhead:** 1.7M `DxfEntity` objects plus `MidpointIndex`, picking and selection have not been tested at this count. Measure load time and memory on TCL01PA0 in step 1 and decide on mitigations from the data. Picking already has a bounds prefilter, but a spatial grid may be needed.
- Cancellation checks (`LoadingContext.Check`) run every N elements in the SVG pass, as the DXF parser does.

## Small-step order (replaces stage granularity for implementation)

1. `SvgReader` for the starter profile only: circle outlines and Bézier paths drawn as hairlines, no fills or text yet. Format detection and the open dialog. Load `splines.svg` and record the time and memory for both files.
   - **Results (2026-11-23, Release, document-only load):**
     - `splines.svg` loads in 3.3 s with 523 MB retained and 606 MB peak: 2.84M chords from 288k Béziers.
     - `TCL01PA0-GC01A.svg` loads in 3.1 s with 878 MB retained (about 520 B per circle entity).
     - The DXF spline bound of 0.0001 units first produced 69M chords and 7 GB. SVG Béziers now use a bound of `max(0.0001, 1e-5 × max(viewBox w, h))` in user units, which is 0.1 here. `SplineGeometry.Flatten` gained an optional tolerance parameter, and the DXF default is unchanged.
   - **Not yet verified:** GPU upload and interaction (picking, hover) with 1.7M arc instances in the viewer.
2. White/dark background toggle (DXF dark, SVG white).
3. Instanced filled discs.
4. Text (default 16px, gray).
5. Checks plus a small synthetic fixture. The 57/102 MB files are not vendored and are used only as local manual benchmarks.
6. Then the rest of Stage 1 (other shapes, transforms, units, CSS, `<use>`), and so on.

## Implementation results (2026-11-23)

**Steps 2–6 are done:**
- White/dark background toggle: View › White background, `Ctrl+B`, and `DxfView.BackgroundMode`.
- Instanced filled discs.
- Text and `tspan`, with chunk-level `text-anchor`.
- Every shape type, transforms, root units, CSS subset, `<use>`/`<symbol>`, elliptical arcs, gradient first-stop fills, nonzero/evenodd fills, Inkscape layers, `.svgz`, and `LoadAsync(Stream, DrawingFormat)`.

**Additive public API:**
- `DrawingFormat` and `DxfDocument.SourceFormat`.
- `DrawingBackground` and `DxfView.BackgroundMode`/`IsLightBackground`.
- `DxfEntity.FillColor`/`FillDisc`.
- The `LoadAsync` stream overload.

The next package would therefore be a minor version.

**Checks:** 6 new `SvgChecks` cases, including GPU frames on white and dark. The full suite passes, 364 of 364.

**Benchmarks** (Release, idle machine, 1400×900 window):

| File | Load | Retained | Viewer install + upload + first frame | Zoom frame |
|------|------|----------|---------------------------------------|-----------|
| `splines.svg` (569k discs, 1,102 paths, 1,102 labels) | 6.6 s | 686 MB | 1.5 s | 17 ms (vsync) |
| `TCL01PA0-GC01A.svg` (1.68M discs) | 5.3 s | 955 MB | 1.6 s | 26 ms |

The background toggle takes 16 ms in both files and re-uploads nothing.

**Follow-up (2026-11-23):**
- **Deep-zoom jagged splines:** `ZoomedBezierIsSmoothAndAntialiased` reproduced two separate causes:
  - Load-time chords deviated 4.8 px at 100 px/unit. Fixed with GPU-tessellated cubics.
  - Lines had no antialiasing at all; the alpha-line flag gave no coverage. Fixed with 8× MSAA plus a resolve.
- **Measurement is unchanged:** it still uses chords within the documented bound.
- **Frame time after both fixes:** `splines.svg` 17 ms and TCL01PA0 20 ms per frame (vsync-bound).
- **Layers panel:** shows only a Text toggle for SVG, and only when text exists, under "Elements".
- **Opening a file** clears the current drawing immediately. `DxfView.PlaceholderText` shows "Loading …", and the panel releases its rows.

**Known open items:**
- Selection outlines (Direct2D overlay) are still built from measurement chords, so a selected Bézier shows chords at extreme zoom.
- Load time rose from about 3 s in step 1, because of the full style cascade per element. Profile it if it matters.
- `tools/DocumentSmoke` does not exercise SVG; it requires MTEXT.
- Paint order: strokes are drawn above fills.
- Stage 4 fidelity items.
