# Saved cameras and spatial display

Opening a document restores its first supported `*ACTIVE` model-space VPORT camera. If there is no active VPORT, the reader uses the legacy HEADER camera. Named model-space VIEW records and other saved VPORT definitions are available under **View > Model camera**. Tiled VPORT definitions are selectable individually; this does not recreate a split-window workspace. Paper-space VIEW bookmarks are not model cameras.

## Public control

```csharp
await view.SetDocumentAsync(document); // restores the saved current camera
var cameras = document.ModelViews;
view.ActiveModelView = cameras.First(c => c.IsSupported).Id;
view.ActiveModelView = null;           // original XY measurement view
view.RestoreSavedModelView();
```

Camera IDs belong to their document. Foreign IDs are rejected. `ModelViewChanged` reports camera changes. Metadata includes direction, target, center, height, aspect ratio, twist, projection mode, lens length and clipping settings. Unsupported cameras remain listed with `IsSupported == false`.

## Projection and interaction

The display pipeline retains XYZ geometry, applies target, view direction and twist, clips against the eye/front/back planes before perspective division, then sends projected coordinates and depth to the GPU. Perspective uses the saved lens length and view dimensions. GPU depth testing resolves occlusion where surface triangles exist; boundary-only solids remain wireframes.

Positive-Z plan cameras reuse the original analytic XY buffers with an affine camera mapping, preserving picking, measurement and infinite rays. Nonplan, perspective and depth-clipped cameras are display-only. Public XY anchor selection and coordinate conversion through these views throw `NotSupportedException`. Document queries continue to use the original XY geometry and distances; selecting a camera does not turn them into 3D measurements.

Finite model geometry remains available when panning beyond the saved frame. Nonplan unbounded lines are restricted to that frame. Paper viewports retain lateral clipping to their saved rectangles.

## Source fields and limits

VPORT uses direction 16/26/36, target 17/27/37, center 12/22, height 40, aspect 41, twist 51 and mode 71. VIEW uses direction 11/21/31, target 12/22/32, center 10/20, height 40, width 41, twist 50 and mode 71. Lens and front/back clipping use 42/43/44. Paper VIEWPORT mode uses 90. HEADER fallback reads the corresponding VIEW variables, including FRONTZ and BACKZ, only inside HEADER.

Preparation is bounded to 128 saved cameras, 32 million processing operations and 8 million retained display vertices across prepared cameras. Spatial geometry is bounded per entity; planar arc flattening uses a 0.0001 drawing-unit display tolerance and a subdivision cap. If spatial preparation fails, valid analytic XY geometry remains available and the affected camera is marked unsupported.

The implementation does not add orbit navigation, lighting, general ACIS face meshing or a CAD kernel. The separate torus change supports complete untrimmed ring-torus surfaces; its tessellation is display-only and creates no measurable topology.

## Validation

The Release suite passes 345 checks, including numeric projection/clipping cases, public camera APIs, original XY behavior, GPU depth ordering, perspective line occlusion, the actual saved camera in `3dsolids.dxf`, and the retained XYZ torus surface. Test reports include actual GPU readbacks for visual inspection.
