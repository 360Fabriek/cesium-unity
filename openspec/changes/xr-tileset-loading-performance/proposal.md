## Why

Loading 3D Tilesets in XR currently causes noticeable frame drops and performance hitches. This occurs because Unity `Mesh` and `Texture2D` creation, along with `GameObject` instantiation, still happen on the main thread, despite C++ tile loading running in the background. Furthermore, if Cesium shaders do not fully support Single-Pass Instanced rendering, XR headsets fall back to multi-pass rendering, effectively cutting rendering performance in half. Resolving these bottlenecks is critical for a smooth XR experience.

## What Changes

- Implement Unity's newer asynchronous APIs (e.g., `Mesh.ApplyAndDisposeWritableMeshData`) to move render resource creation off the main thread.
- Update Cesium shaders to fully support XR Single-Pass Instanced rendering instead of forcing multi-pass rendering.

## Capabilities

### New Capabilities
- `async-render-resources`: Asynchronous creation of Unity Meshes and Textures for 3D tiles.
- `xr-single-pass-rendering`: Support for XR Single-Pass Instanced rendering across all Cesium materials.

### Modified Capabilities

## Impact

- **UnityPrepareRendererResources**: Modifications to `prepareInMainThread` to defer rendering tasks using Unity's Advanced Mesh API and Async Texture Uploads.
- **Shaders**: All Cesium `.shader` and `.hlsl` files will need macros (`UNITY_SETUP_INSTANCE_ID`, `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO`) added to enable single-pass instantiation.
- **Cesium3DTileset**: Adjustments to how Unity GameObjects are instantiated or pooled to reduce main-thread garbage collection and instantiation overhead.
