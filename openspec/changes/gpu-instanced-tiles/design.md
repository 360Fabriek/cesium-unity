## Context

The Cesium for Unity plugin currently renders glTF and i3dm tilesets by creating an individual `GameObject` with a `MeshRenderer` for each node inside a glTF model. When tilesets use the `EXT_mesh_gpu_instancing` extension or i3dm format, this pattern creates a thousands-of-draw-calls bottleneck since each instance is an independent `MeshRenderer` with its own transform. Providing GPU instanced rendering via Unity's `Graphics.DrawMeshInstanced` API can drastically reduce CPU load and draw calls.

## Goals / Non-Goals

**Goals:**
- Implement an `InstancedTileRenderer` or equivalent manager to handle batched rendering of instanced meshes.
- Parse `EXT_mesh_gpu_instancing` and instance feature IDs/metadata correctly.
- Ensure the materials support `enableInstancing = true` and process the per-instance matrices properly.
- Update clipping rasters functionality to verify compatibility or disable instanced rendering on tiles where clipping applies.

**Non-Goals:**
- Supporting arbitrary custom materials that do not utilize the `CesiumDefaultTilesetMaterial` without user modification.
- Re-architecting the base glTF loader entirely—this change focuses purely on extending support for the instanced mesh subsets.
- Modifying any files in the `native~\extern` folder. This directory is strictly off-limits for changes.

## Decisions

**1. Using `Graphics.DrawMeshInstanced` with `MaterialPropertyBlock`**
- *Rationale:* Instead of creating a `GameObject` for every instance, one `InstancedTileRenderer` per primitive will maintain the matrices. `Graphics.DrawMeshInstanced` supports up to 1023 instances per draw call. Utilizing `MaterialPropertyBlock`, we can push custom per-instance properties if necessary (like custom colors or feature IDs). 
- *Alternatives:* Using `Graphics.RenderMeshIndirect` or `ComputeBuffers`. However, `DrawMeshInstanced` aligns better with Unity's standard pipelines and is more broadly supported across different rendering paths (Built-in, URP, HDRP).

**2. Handling Clipping Rasters**
- *Rationale:* Raster clipping in Cesium relies on shader logic that tests against clipping primitive parameters. Since the parameters are defined globally per-material or per-tile, clipping should theoretically work with instanced meshes if the world position of the instance vertex is correctly calculated. However, to be safe, we will add a fallback: if `CesiumRasterOverlay` applies a clipping overlay to an instanced tile and it cannot be executed cleanly in the instanced shader, we will bypass instanced rendering for that specific node.

**3. Complete VR/XR Instancing Compatibility**
- *Rationale:* Unity's `DrawMeshInstanced` requires a comprehensive shader setup to function correctly in VR/XR (especially for Single Pass Instanced rendering). We will ensure all Cesium shaders include `#pragma multi_compile_instancing`, `#pragma instancing_options`, `UNITY_SETUP_INSTANCE_ID`, `UNITY_TRANSFER_INSTANCE_ID`, `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO`, and `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX`. This guarantees that instance IDs correctly multiplex with the stereo eye index and that both eyes render the instanced geometry at the correct transforms. We will also ensure `InstancedTileRenderer` generates bounding boxes that comfortably fit the FOV of both eyes to prevent incorrect culling.

## Risks / Trade-offs

- **[Risk] Unity's 1023 instance limit per draw call:**
  - *Mitigation:* The `InstancedTileRenderer` will batch matrices into arrays of size 1023 (or less) and issue multiple `DrawMeshInstanced` commands sequentially in the `Update` or `LateUpdate` loop.
- **[Risk] Loss of Frustum Culling per Instance:**
  - *Mitigation:* `DrawMeshInstanced` does not automatically frustum-cull individual instances. For large tiles, we rely on Cesium's macro-level tile culling. If performance drops because of off-screen instances within a large tile being drawn, we might need a coarse CPU bounding-box check, though usually, tiles are small enough that this is acceptable.
- **[Risk] HDRP/URP Compatibility:**
  - *Mitigation:* We will ensure that the modifications to shader keywords (`#pragma instancing_options`) are added to all SRP variants of the Cesium materials.
- **[Risk] Incorrect Orientation and Flipped Normals (Handedness Mismatch):**
  - *Mitigation:* Cesium Native uses a right-handed coordinate system, while Unity uses a left-handed one. Previous proofs of concept suffered from flipped normals and incorrect orientation because instance matrices lacked strict handedness conversion. The C++ to C# interop will guarantee that matrix transformations flip the correct axes and handle negative scales safely so polygon winding order and normals remain intact.
