# VibeDxf v0.3.0

Saved 3D viewpoints and broader DXF geometry support, including the complete Waschbecken sink wireframe.

- Restore the drawing's saved model-space VPORT camera on open. View > Model camera selects saved VPORT configurations, named model VIEWs, or Original XY / measurement.
- Honor orthographic direction, target, twist, framing, perspective lens length and front/back clipping in supported model and paper-space cameras. Retained XYZ display geometry uses GPU depth testing where surface triangles exist.
- Render legacy 3D polylines, polygon/polyface meshes and MESH control wireframes, retaining XYZ for camera views and projected XY geometry for queries.
- Read straight and elliptical ACIS SAT/SAB solid boundaries, plus the supported explicit rational/non-rational intersection-spline and tolerant-topology profiles. All 13 solids in Waschbecken-2018.dxf now load, including the basin, drain and draining board.
- Display complete untrimmed ring-torus surfaces, including legacy SAT 400 files whose faces contain no boundary edges. Their surface mesh is display-only and creates no measurable topology.
- Add saved-camera metadata/control APIs to VibeDxf.Wpf and expand regression coverage to 358 checks, including actual GPU readbacks and independent package consumers.

## Downloads

- **VibeDxf-Setup-0.3.0-win-x64.exe**: per-user installer with Start menu shortcut, optional desktop shortcut and uninstall support.
- **VibeDxf-0.3.0-win-x64.zip**: portable version; extract the complete folder and run VibeDxf.exe.
- **SHA256SUMS.txt**: SHA-256 checksums for both downloads.

The embeddable library is released separately as **VibeDxf.Wpf 0.3.0** on NuGet.org.

## Requirements and limitations

Windows 10 or later, x64, and the **.NET 10 Desktop Runtime x64**:
https://dotnet.microsoft.com/download/dotnet/10.0

The runtime is not bundled, downloaded or installed automatically. The .NET SDK is not required to run the app. The installer is unsigned.

Only ASCII DXF is supported. Positive-Z plan cameras without depth clipping retain XY picking and measurement; nonplan, perspective and depth-clipped cameras are display-only. Document distance queries remain in projected XY, not true 3D. Edged solids, including the sink, remain wireframes: general trimmed ACIS surface meshing, shading/lighting, hidden-line removal and orbit controls are not implemented. Original SHX fonts, standalone ELLIPSE entities, external references, gradient hatches, linetypes and lineweights remain unsupported. See the README for detailed scope and approximation limits.

## Library host behavior

DxfView now restores a supported saved model camera when assigning a document. Hosts requiring the original XY view can set ActiveModelView = null. XY coordinate conversion and model anchor selection through a noninteractive camera throw NotSupportedException. The existing document traversal and XY measurement contract remains unchanged; new camera metadata and selection APIs are additive.
