# Spline support

The viewer reads control-point DXF `SPLINE` entities, including nonuniform knots,
positive rational weights, and closed/periodic parameter domains. Degree 1–16 is
supported. Parsing follows the [Autodesk SPLINE group-code reference](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-E1F884F8-AA90-4864-A215-3182D47A9C74.htm).

`SplineGeometry` inserts knots to extract rational Bezier spans, then subdivides
them until their projected control points lie within **0.0001 drawing units** of
the chord. Positive weights provide a convex-hull bound. This happens once when
loading the file; rendering, highlighting, picking, and distance measurement reuse
the resulting line segments. Pan/zoom does not rebuild spline geometry.

Each spline remains a single selectable entity, with its layer and handle.
Approximation segments do not offer artificial midpoint snapping. Center/Closest
controls do not change spline targets. Measurements involving a spline display
`≈`: they are approximations, with up to 0.0001 drawing units of geometric error per
spline (up to 0.0002 for two splines), aside from floating-point roundoff. This is a
distance error bound; closest-point locations and signed deltas can vary when the
closest pair is not unique. Existing lines/arcs still use analytic measurements.
The fixed tolerance can become visible at extreme magnification.

Fit-point-only definitions, varying-Z nonplanar curves, nonpositive weights,
malformed data, and curves exceeding subdivision limits are counted as
`SPLINE (invalid or unsupported)` instead of crashing the entire drawing load.
Constant-Z control points are projected into XY, as with the rest of this 2D viewer.

## Offline tests and visual report

From the repository root, after the dependencies have been restored:

```powershell
dotnet run --project Tests/VibeDxf.Checks.csproj -c Release --no-restore

# Also check the private reference drawing (not included in the repository):
dotnet run --project Tests/VibeDxf.Checks.csproj -c Release --no-restore -- --spline-sample "C:\Users\Yoav Miller\Downloads\C25F_REV-A.DXF"
```

Every run writes `artifacts/test-report/index.html` and its adjacent PNG files.
Open that HTML file in a browser. It includes a filterable list of all passed
checks, full output/failure details, geometry diagrams, strategy comparisons, and
actual GPU frame captures. Numerical and lifecycle checks appear as results rather
than fabricated screenshots. Keep the HTML and PNGs together when sharing it.

Spline tests cover independent NURBS basis evaluation, rational weights, nonuniform
and repeated knots, periodic domains, degenerate/invalid input, metadata, picking,
GPU pixels for the curve/highlight/connector, spline-to-line/arc/spline distances,
and reuse of GPU buffers. The optional C25F check expects 4,284 entities including
315 splines, with none skipped, and captures the complete rendered drawing.

The current checks are a console application, not a test-framework project, so
Visual Studio Test Explorer does not discover them. Failures now print `FAIL` and
return exit code 1; the viewport window closes in `finally`. This avoids the
unhandled managed-exception dialog for ordinary test assertions.
