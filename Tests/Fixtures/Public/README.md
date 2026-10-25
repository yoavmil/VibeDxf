# Public DXF compatibility fixtures

Downloaded 2026-10-21. Tests use these committed copies offline; no private drawings or environment variables are required.

| File | Source | Bytes | SHA-256 |
|---|---|---:|---|
| api-cw750-details.dxf | [Upstream](https://github.com/gdsestimating/dxf-parser/blob/master/samples/data/api-cw750-details.dxf) | 2738077 | 02a2d997c617cf9ca74ff06d8f0a6d0262db73027d3afedbe5df9f3d5b6d25be |
| uncommon.dxf | [Upstream](https://github.com/mozman/ezdxf/blob/master/examples_dxf/uncommon.dxf) | 343110 | 5efdd671e3b6d8c45bd0688b1d6479d523f2290f0b390f1584348bf46e34c965 |
| colors.dxf | [Upstream](https://github.com/mozman/ezdxf/blob/master/examples_dxf/colors.dxf) | 151817 | 70c12f1e3fd291c4ab02df7628624956fce315263c0ebcb18734d0b6e5b9974a |
| viewport_frozen_layers.dxf | [Upstream](https://github.com/mozman/ezdxf/blob/master/examples_dxf/viewport_frozen_layers.dxf) | 73504 | c04a78677627e94c0056b2c01f641770439668932bd2288392ae9ec654a5e222 |

Upstream repositories publish under MIT; retain the adjacent dxf-parser-LICENSE.txt and ezdxf-LICENSE.txt notices with these fixtures. Files are unmodified.

## Current parser coverage

- api-cw750-details.dxf: 4,316 model-space entities after INSERT, dimension and leader expansion (3,278 LINE, 322 MTEXT, 296 LWPOLYLINE, 75 ARC, 73 HATCH, 58 CIRCLE, 58 MULTILEADER, 57 TEXT, 43 ATTRIB, 42 SOLID, 11 LEADER, 3 3DSOLID). All 293 DIMENSION, 58 MULTILEADER and 11 LEADER records render as display-only annotations; the leaders use the `_OPEN` arrow block. Four layouts: Sheet 1 (63 paper-space entities, 2 viewports), Sheet 2 (73, 1), Sheet 3 (74, 2) and Sheet 4 (63, 3). Each has a title block with attribute values. Inactive sheets store viewport ID/status 0; Sheet 4's isometric viewport 786B renders a display-only orthographic projection from direction (-1,-1,1), with saved target, scale and twist. The three ACIS solids 7813, 781F and 7843 on FG-Dtl-Dim render actual straight and elliptical boundary edges. Circular XY projections remain analytic; tilted elliptical projections use bounded approximation. No unsupported records remain in this fixture.

- uncommon.dxf: 151 model-space entities (59 LINE, 35 MTEXT, 21 SOLID, 10 ARC, 7 LWPOLYLINE, 6 POINT, 3 RAY, 2 MULTILEADER, 2 XLINE, 1 SPLINE, 1 CIRCLE, 1 ATTRIB, 1 MESH, 1 POLYLINE, 1 3DSOLID); 13 DIMENSION, 2 LARGE_RADIAL_DIMENSION and 1 ARC_DIMENSION render from their blocks. Its default Layout1 (one bare viewport) and empty Layout2 are dropped, so it has no layout tabs. MESH 4CB contributes 108 XY-projected wireframe edges and point markers for vertical edges. Polyface 4E9 renders its indexed visible edges in XY. Polyface 4D7 contains eight coordinate vertices but no face records, so no edges are invented. The straight-edged 3DSOLID 622 renders its actual ACIS boundary after its body transform and XY projection. Skipped: 1 POLYFACE (no visible edges).
- colors.dxf: ezdxf's color sample. Layers RED (ACI 10) and BLUE (ACI 170); circles, MTEXT and polylines that are BYLAYER, have an explicit ACI with a true color (0xF26722) that wins, or are BYBLOCK; and block inserts whose children are BYLAYER, BYBLOCK or explicit, with one insert recolored yellow.
- viewport_frozen_layers.dxf: ezdxf's per-viewport freeze sample. Layer3 is frozen in the file, and a layout has three viewports: no overrides, Layer1 frozen, and Layer2 frozen (group 331).

These counts describe ENTITIES records handled by the current reader; sequence/control records are not drawable entities. Referenced BLOCKS definitions are expanded recursively; counts include child entities and skipped child records per instance, not unused block definitions. Model space and every paper layout (from ENTITIES for the active one, *Paper_SpaceN blocks for the others) are loaded separately; the reader implements planar XY OCS and 2D insertion transforms, not general OCS/3D transforms. A successfully parsed/uploaded entity does not guarantee a complete or geometrically faithful rendering of these drawings.

## Automated checks

PublicDrawingAndGpuUpload has two Test Explorer cases. Each checks exact loaded counts, spline counts and skipped-type counts, opens an off-screen viewport, verifies the uploaded line/spline vertex count, ray/xline instance count and arc instance count, and captures the supported geometry into artifacts/test-report/index.html. Captures provide visual review, not a pixel-perfect reference comparison for these drawings. Independent synthetic tests cover spline picking, rendering pixels and distances. These are compatibility samples, not a large-entity performance benchmark.

## Hatch regression atlas

The report includes one zoomed GPU frame for every one of the 89 placed hatch instances (73 in model space, 4 per sheet title block). Orange shows its hatch strokes and stored contour; white shows the surrounding drawing. The report search filters image cards as well as test results. The audit checks every continuous ANSI31 stroke against its boundary and tests fill triangle containment. Synthetic cases cover solid and patterned fills, concavity, islands, bulges, circular/elliptic/spline edges, rational weights, complementary clockwise angles, multiple pattern families, dash phases, dots, transforms, large origins and malformed/unsupported data.

Hatch 95F2 is non-associative and has no boundary source handles. Its arc centers differ from nearby outline 14C2 by approximately (+0.000474752237, -0.010236703965) drawing units. Tests preserve these distinct source coordinates; loading a hatch must not silently relocate it to a nearby outline.
