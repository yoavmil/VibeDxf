# Public DXF compatibility fixtures

Downloaded 2026-10-21. Tests use these committed copies offline; no private drawings or environment variables are required.

| File | Source | Bytes | SHA-256 |
|---|---|---:|---|
| api-cw750-details.dxf | [Upstream](https://github.com/gdsestimating/dxf-parser/blob/master/samples/data/api-cw750-details.dxf) | 2738077 | 02a2d997c617cf9ca74ff06d8f0a6d0262db73027d3afedbe5df9f3d5b6d25be |
| uncommon.dxf | [Upstream](https://github.com/mozman/ezdxf/blob/master/examples_dxf/uncommon.dxf) | 343110 | 5efdd671e3b6d8c45bd0688b1d6479d523f2290f0b390f1584348bf46e34c965 |

Upstream repositories publish under MIT; retain the adjacent dxf-parser-LICENSE.txt and ezdxf-LICENSE.txt notices with these fixtures. Files are unmodified.

## Current parser coverage

- api-cw750-details.dxf: 4,226 model-space entities after INSERT and dimension block expansion (3,245 LINE, 322 MTEXT, 296 LWPOLYLINE, 75 ARC, 73 HATCH, 58 CIRCLE, 58 MULTILEADER, 57 TEXT, 42 SOLID). All 293 DIMENSION and 58 MULTILEADER records render as display-only annotations. Four layouts: Sheet 1 (55 paper-space entities, 2 viewports), Sheet 2 (61, 1), Sheet 3 (62, 2) and Sheet 4 (54, 2), each with a title block. Inactive sheets store viewport ID/status 0; Sheet 4's isometric viewport is skipped. Skipped: 91 ATTRIB, 11 LEADER, 3 3DSOLID, 1 VIEWPORT (3D view).
- uncommon.dxf: 147 model-space entities (59 LINE, 35 MTEXT, 21 SOLID, 10 ARC, 7 LWPOLYLINE, 6 POINT, 3 RAY, 2 MULTILEADER, 2 XLINE, 1 SPLINE, 1 CIRCLE); 13 DIMENSION, 2 LARGE_RADIAL_DIMENSION and 1 ARC_DIMENSION render from their blocks. Its default Layout1 (one bare viewport) and empty Layout2 are dropped, so it has no layout tabs. Skipped: 2 3D POLYLINE; 1 each 3DSOLID, ATTRIB, MESH (5 records).

These counts describe ENTITIES records handled by the current reader; sequence/control records are not drawable entities. Referenced BLOCKS definitions are expanded recursively; counts include child entities and skipped child records per instance, not unused block definitions. Model space and every paper layout (from ENTITIES for the active one, *Paper_SpaceN blocks for the others) are loaded separately; the reader implements planar XY OCS and 2D insertion transforms, not general OCS/3D transforms. A successfully parsed/uploaded entity does not guarantee a complete or geometrically faithful rendering of these drawings.

## Automated checks

PublicDrawingAndGpuUpload has two Test Explorer cases. Each checks exact loaded counts, spline counts and skipped-type counts, opens an off-screen viewport, verifies the uploaded line/spline vertex count, ray/xline instance count and arc instance count, and captures the supported geometry into artifacts/test-report/index.html. Captures provide visual review, not a pixel-perfect reference comparison for these drawings. Independent synthetic tests cover spline picking, rendering pixels and distances. These are compatibility samples, not a large-entity performance benchmark.

## Hatch regression atlas

The report includes one zoomed GPU frame for every one of the 89 placed hatch instances (73 in model space, 4 per sheet title block). Orange shows its hatch strokes and stored contour; white shows the surrounding drawing. The report search filters image cards as well as test results. The audit checks every continuous ANSI31 stroke against its boundary and tests fill triangle containment. Synthetic cases cover solid and patterned fills, concavity, islands, bulges, circular/elliptic/spline edges, rational weights, complementary clockwise angles, multiple pattern families, dash phases, dots, transforms, large origins and malformed/unsupported data.

Hatch 95F2 is non-associative and has no boundary source handles. Its arc centers differ from nearby outline 14C2 by approximately (+0.000474752237, -0.010236703965) drawing units. Tests preserve these distinct source coordinates; loading a hatch must not silently relocate it to a nearby outline.
