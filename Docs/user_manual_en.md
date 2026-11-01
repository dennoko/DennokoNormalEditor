# Dennoko Normal Editor User Manual

A non-destructive custom normal editor designed for VRChat avatar modification.  
Adjust shading, smooth out seam breaks across separate meshes (such as the neck seam between head and body), restore subtle 3D facial contours on spherized meshes, or flatten anime cel shading directly in Unity's Scene View. Original mesh asset files are never modified.

## Requirements

- Unity 2022.3.22f1 or later
- VRChat SDK
- **NDMF (nadena.dev.ndmf)** — Required

---

## Quick Start

### 1. Add Component
Right-click the mesh you want to edit (e.g., Head or Body) in the Hierarchy and select `dennokoworks > Dennoko Normal Editor`.

### 2. Enter Edit Mode
Click the **"Edit"** button in the Inspector. (*Automatically enters edit mode when adding the component to a mesh via the context menu).

### 3. Edit
Click a vertex in the Scene View to select an influence area. Drag the 3D handle to tilt normals, or use the sliders in the bottom-right overlay panel (Smooth, Restore Shape, Flatten, Erase).  
While grabbing the handle, scroll the mouse wheel to adjust the brush radius.

### 4. Finish Editing
Press `Esc` (deselects if a vertex is selected, finishes editing if none is selected), or click the **"Finish Editing"** button in the Inspector.

That's it!

---

## Detailed Guide

### 1. Attaching the Component

In the Hierarchy, right-click the GameObject with the target mesh (`SkinnedMeshRenderer` / `MeshRenderer`) and select `dennokoworks > Dennoko Normal Editor` (or add it via `Add Component` in the Inspector).

- **When added via right-click on a GameObject with a Renderer:**  
  The mesh is automatically registered as an edit target and **Edit Mode starts immediately**.
- **When added with multiple GameObjects selected:**  
  **All selected meshes are registered as edit targets** (e.g., selecting both Face and Body allows you to edit and blend across the neck seam). Only one component is created, placed on the GameObject you right-clicked (or the last selected object).
- **When added to a GameObject without a Renderer:**  
  Only the component is added. Specify the desired Renderer in the **"Add Target"** field in the Inspector.

The GameObject that received the component is briefly highlighted in the Hierarchy.

To add more targets later, **drag GameObjects with a mesh into the empty "Add Target" field** at the bottom of the "Targets" list in the Inspector. Multiple objects can be dropped at once.  
Registering both Head and Body simultaneously allows seamless normal smoothing across mesh boundaries.

### 2. Editing

Click the **"Edit"** button in the Inspector to enter Edit Mode in the Scene View.

| Action | Result |
| --- | --- |
| Click a vertex on the mesh | Selects the closest **visible** vertex to the click position (Yellow dot = candidate, Blue circle = influence radius) |
| Drag 3D handle | Pulls normals in the influence radius toward the handle direction with falloff (Tilt) |
| **Mouse wheel while grabbing handle** | **Changes influence radius (Scroll Up = Shrink, Scroll Down = Expand / Blender convention)** |
| Drag sliders in bottom-right overlay | Applies Smooth, Restore Shape, Flatten, or Erase across the selected area |
| `Esc` | Deselect current vertex. Press again to finish editing |
| Select another object | Finishes editing |

Use the overlay panel in the bottom-right corner of the Scene View to adjust radius, falloff, mirror, and effect sliders on the fly.

### About Vertex Selection

**Vertices hidden behind surfaces cannot be selected.** Vertices occluded by front-facing geometry are filtered out, and only visible surface vertices are selectable.

> **Note:** Only **Renderers registered as edit targets** are treated as occluders.  
> For example, if only the body is registered and clothing is not, you may select body vertices hidden underneath clothing. Adding the clothing to the edit target list resolves this.

### Undo (Ctrl+Z)

**Each handle drag or slider adjustment represents one Undo step.** Pressing `Ctrl+Z` undoes the previous edit.  
Radius changes made via mouse wheel while dragging the handle are included in the same step and undone together.

### Radius Adjustment While Grabbing Handle

Scrolling the mouse wheel while grabbing the handle resizes the influence radius.  
**You can adjust it as soon as you grab the handle, before even moving it.** This lets you preview the coverage before tilting, or adjust the influence after tilting while maintaining the displacement.

The wheel scales proportionally (fine adjustments at smaller radii, broader steps at larger radii). When not grabbing the handle, the mouse wheel zooms the Scene View normally.

### 3. Selected Area Adjustments (Overlay Effect Sliders)

With a vertex selected, drag the sliders in the bottom-right overlay panel to preview and apply effects across the influence radius:

- **Smooth (なじませる):**  
  Averages normals within the radius with surrounding vertices. It automatically detects UV seams and **boundaries between different meshes (such as the neck seam between Head and Body)**, smoothing normals continuously across the border to eliminate dark shading lines or harsh seams.
- **Restore Shape (形状を戻す):**  
  For meshes where spherized normals make the face look too flat, blends a portion of **Smooth Geometric Normals** (normals computed from actual polygon geometry while maintaining smoothness). This restores natural depth to the nose ridge, lips, and eye contours without introducing faceting.
- **Flatten (揃える):**  
  Aligns normals in the selected area toward the center vertex's normal direction (or area average), creating clean, flat anime cel shading.
- **Erase (元に戻す):**  
  Restores normals in the selected area back toward their unedited state (0%). Use this to partially undo edits in specific spots.

### 4. Edit Strength Mask (UV0 Mask)

Each edit target in the Inspector has an optional **"Mask"** (`Texture2D`) property.

- Scales edit strength on that mesh according to the **brightness of UV0 (white = 100%, black = 0%)**.
- By painting areas like eyes, eyebrows, or hair roots black, you can prevent accidental edits in those regions.
- Gradient textures can also be used to achieve smooth falloff transitions.

### 5. Upload Directly

No manual save or export step is required. Edits are stored on the component and applied automatically during avatar build via NDMF, including tangent re-orthogonalization. Original mesh asset files are never touched.

---

## Brush Settings

### Radius
The influence radius in world space (meters). Displayed as a blue circle in the Scene View.

### Falloff
The curve defining how effect strength diminishes away from the center.

| Type | Characteristics |
| --- | --- |
| Smooth | Smooth falloff at boundaries. Recommended for general editing |
| Linear | Constant linear falloff rate |
| Sharp | Strong effect near the center with rapid dropoff |
| Constant | Uniform strength across the entire radius (no falloff) |

---

## Mirror Editing

Enable Mirror (ON) in the Scene View overlay or Inspector.

- **Mirror Axis:** Choose between X Axis (Left/Right), Y Axis (Up/Down), or Z Axis (Front/Back). Standard avatar editing uses the X Axis.
- **Symmetric Editing:** Operations performed while mirror is active (tilting, smoothing, shape restoration) are mirrored across the center plane.
- **Center Deadzone:** Edits very close to the center plane are treated as non-mirrored single edits to prevent duplicate transformations from overlapping influence spheres.

---

## Bake

Export the edited mesh to a standalone asset file (`.asset`).

- Output directory: **Same folder as the original mesh**
- File name: Original mesh name appended with **`_edited`** (numbered sequentially if a file already exists)
- **Safe Design:** Baking generates a new mesh file and selects it in the Project window without replacing renderers in the scene. Useful when exporting assets or taking meshes to other projects.

---

## Specifications & Limitations

### Supported Features
- Vertex normal tilting with proportional brush influence
- Cross-mesh weld and seam normal smoothing (e.g., Head and Body)
- Smooth geometric normal partial restoration (restoring depth on spherized faces)
- Normal flattening for cel shading
- UV0 texture edit strength masking
- Mirror editing on any axis
- Real-time NDMF preview integration (no Play mode needed)
- Standalone mesh baking
- **Automatic Tangent Orthogonalization:** Tangents are orthogonalized against modified normals via Gram-Schmidt, preventing normal map distortion and anisotropic reflection artifacts.

### Not Supported (By Design)
- Topology modifications (adding, deleting, or extruding vertices/faces)
- Vertex position deformation (to deform mesh geometry, use **Dennoko Mesh Editor**)
- UV or bone weight editing

### Dependency on Vertex Indices
This tool stores normal delta vectors relative to the **original mesh's vertex indices**. Keep the following in mind:

- **FBX Re-export / Re-import:** Re-exporting from 3D modeling software (e.g., Blender) may change vertex counts or ordering, causing normal edits to misalign.
- **Safety Safeguard:** If vertex count differs from edit time due to mesh replacement or re-import, **normal edits are automatically skipped** to prevent distortion, and a warning is displayed.

### Compatibility with Other Tools
Build processing runs during NDMF's `Transforming` phase (immediately after Modular Avatar).  
Because it executes before optimizing tools like Avatar Optimizer (AAO), it safely coexists without conflict.

---

## Troubleshooting

### "NDMF preview not acquired" Warning
NDMF preview is disabled or has not finished generating. Editing is still possible, but you will be viewing and editing base geometry without effects from upstream tools. Ensure NDMF preview is enabled on the Unity toolbar.

### "Vertex count differs from when edited" Warning
The base mesh was replaced or re-imported with a different vertex count. Edit application is suspended to protect the model. Click "Clear All Edits" in the Inspector to reset and re-edit.

### Neck Seam Does Not Smooth Out
Verify that both Head (Face) and Body are added to the component's **"Targets"** list. Both must be registered so that boundary vertices across separate meshes can be welded and calculated together.

### Checking for Updates
The current version (`v1.0.0`) is displayed at the top of the Inspector. When a new version is released on GitHub, an "Update available" link appears; click it to visit the release page. Click the `↻` button on the right to manually recheck.

