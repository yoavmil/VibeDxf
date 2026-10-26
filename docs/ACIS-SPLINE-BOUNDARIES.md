# ACIS explicit spline boundaries

The Waschbecken regression extends the existing boundary reader. It does not reconstruct a general ACIS solid kernel or shaded faces.

## Admitted geometry

Binary SAB/ASM `intcurve-curve` records may use the `exact_int_cur` 20900 envelope: zero extension flag, open clamped `nubs` or positive-weight `nurbs`, degree 1–16, strictly increasing distinct knots, endpoint multiplicity p, and interior multiplicity at most p. The reader expands the two endpoint multiplicities to the standard p+1 convention. It validates zero fit tolerance, null supporting surfaces/pcurves, saved discontinuity arrays, identity parameter mapping and the full known trailer. Unknown subtypes/versions, references, periodic profiles and nonzero fit tolerance remain unsupported.

Saved edge parameters trim the curve; reversed curve sense evaluates C(-t). Exact knot insertion exposes rational Bezier spans, including partial first/last spans. Subdivision uses the positive rational control-point convex hull to bound distance from each XYZ chord after body placement. The affine XY projection's amplification is included, so both retained XYZ and projected XY satisfy a 0.0001 drawing-unit chord error bound before INSERT scaling. No endpoint chord is substituted for an unsupported curve. Approximate entities have no chord midpoint anchors.

Preparation is cancellation-aware and bounded: at most 4,096 distinct knots, 65,536 controls, 8 million knot-insertion work units, subdivision depth 24, 200,000 chords per spline and 200,000 boundary primitives per solid.

## Tolerant topology

The exact names `tcoedge-coedge`, `tedge-edge` and `tvertex-vertex` retain their inherited coedge, edge and vertex link positions. Support does not extend to arbitrary suffix-matching names. Curve parameters and explicit 3D edge controls remain authoritative for boundary display. Inline coedge support curves and UV curves do not replace them.

For endpoint matching, the supported SAB tolerant vertex profile contains the inherited fields, legacy scalar -1, and two equal nonnegative bounds. A source vertex may differ from the evaluated boundary endpoint within that saved local bound. Ordinary vertices retain the existing strict check. Coordinates are never snapped or globally healed. A tolerant source is marked approximate, and its source endpoint allowance is separate from the spline subdivision error. This is bounded boundary display, not a claim that the source forms a watertight, valid B-rep.

## Regression evidence

The exact public sink drawing now loads all 13 model-space solids rather than three. Its compressed fixture retains the original DXF bytes, verified by SHA-256; see Tests/Fixtures/Public/README.md. Tests exercise non-rational and rational splines, reversed sense, arbitrary trims, 3D placement, endpoint tolerance acceptance/rejection and unknown-profile rejection. The actual drawing is rendered in both original XY and the saved oblique camera. Those GPU captures show the recovered basin, drain assembly and draining board as wireframes.

Format references: [ezdxf's ACIS topology reader](https://github.com/mozman/ezdxf/blob/master/src/ezdxf/acis/entities.py), [Spatial SAT format documentation](https://www.paulbourke.net/dataformats/sat/sat.pdf), and the original [LibreDWG sample discussion](https://github.com/LibreDWG/libredwg/issues/1071). No external CAD library or new runtime dependency is added.
