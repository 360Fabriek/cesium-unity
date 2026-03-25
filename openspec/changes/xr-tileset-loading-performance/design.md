## Context

Cesium for Unity relies on `UnityPrepareRendererResources` to convert tile data fetched by `cesium-native` into Unity `Mesh` and `Texture2D` objects. Currently, the heavy lifting of populating Unity's specific data structures happens on the main thread. This leads to frame hitches when a large number of tiles load simultaneously. Furthermore, in XR contexts, any shader that does not explicitly support Single-Pass Instanced rendering will force Unity to render the scene in multi-pass mode, which effectively halves the framerate. 

## Goals / Non-Goals

**Goals:**
- Substantially reduce main-thread execution time when loading new 3D tiles.
- Ensure all Cesium shaders fully support Unity's Single-Pass Instanced XR rendering pipeline.

**Non-Goals:**
- Modifying the core `cesium-native` async loading algorithms (as they're already optimized).
- Modifying Unity's internal rendering pipeline beyond our shader code.
- Modifying any files in the `native~\extern` folder. This directory is strictly off-limits for changes.

## Decisions

1. **Unity Advanced Mesh API:** 
   We will utilize `Mesh.AllocateWritableMeshData` and `Mesh.ApplyAndDisposeWritableMeshData` to construct tile meshes. This allows background threads to write directly into Unity's native memory buffers. Only the final `Mesh.ApplyAndDisposeWritableMeshData` call needs to operate on the main thread, vastly reducing stalling.
   *Alternative considered:* Using `SetVertexBufferData` etc., which still requires copying data on the main thread. 

2. **XR Shader Macros:**
   We will instrument all HLSL/CG shader code in `cesium-unity` with standard Unity XR macros (`UNITY_VERTEX_INPUT_INSTANCE_ID`, `UNITY_SETUP_INSTANCE_ID(v)`, `UNITY_TRANSFER_INSTANCE_ID(v, o)`, and `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o)`). This is the standard, foolproof way to support single-pass instanced rendering in Unity.

3. **Texture Uploading:**
   We will use Unity's `Texture2D.SetPixelData` combined with `Apply` for efficient texture upload, or `AsyncGPUReadback`/native texture APIs if viable to further circumvent main-thread stalls.

## Risks / Trade-offs

- **[Risk] Thread Synchronization Overheads** → Creating and disposing native arrays requires careful memory management, otherwise memory leaks can occur. Mitigation: Adopt strict scoping and rely on Unity's NativeArray memory validators.
- **[Risk] Shader Complexity** → Adding XR macros across all shader variants. Mitigation: Test shaders both in the editor and on actual XR hardware.

## Migration Plan

- Deploy the mesh creation changes isolated behind tests to verify visually identical rendering before rolling out to the main branches.
- No rollback strategy needed beyond standard git source control since this does not mutate persisted data models.

## Open Questions
- None. (Resolved: Texture upload approach and `AsyncTextureUpload` optimization will be exposed as an optional setting directly on the `Cesium3DTileset` component so users can tune performance).
