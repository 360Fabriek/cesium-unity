## Why

Currently, there is no support for GPU instanced tiles in Cesium for Unity. Rendering complex tilesets (such as i3dm or glTF with `EXT_mesh_gpu_instancing`) results in one draw call per mesh instance, which severely degrades performance. Supporting GPU instanced tiles will dramatically improve rendering performance for foliage, city furniture, and other highly instanced data by reducing CPU overhead and draw calls.

## What Changes

- **Add GPU Instancing Support**: Extract `EXT_mesh_gpu_instancing` and instance translation/rotation/scale data from glTF nodes and pass them to Unity.
- **Batched Rendering**: Utilize `Graphics.DrawMeshInstanced` to render multiple instances in a single draw call. Optimize the architecture so the tileset component dispatches these draw calls rather than each individual mesh component issuing its own instance.
- **VR/XR Compatibility**: Update shader macros in Cesium materials to ensure `DrawMeshInstanced` works correctly with Unity's Single Pass Instanced VR rendering.
- **Instance Properties via MaterialPropertyBlock**: Populate `MaterialPropertyBlock` with instance matrices (`unreal PR #1499`, `unity #600`) to supply per-instance model matrices to the shader.
- **Clipping Compatibility**: Modify the clipping rasters feature to evaluate if it needs to disable GPU instancing. If per-instance clipping cannot be efficiently achieved through the instanced shader arrays, instanced rendering will be bypassed to maintain visual correctness.
- **Feature ID & Metadata access**: Ensure instance identifiers and metadata are correctly surfaced (`unreal PR #1757`, `#1776`).
- **Scale Factor Correction**: Ensure scaling on instance translations (e.g. `positionScaleFactor`) is properly applied.

## Capabilities

### New Capabilities
- `gpu-instanced-rendering`: Supports rendering multi-instance glTF primitives using Unity's `DrawMeshInstanced` API, greatly reducing draw calls for identical meshes.

### Modified Capabilities
<!-- None -->

## Impact

- **Rendering Architecture**: The `CesiumGltfComponent` (or equivalent Unity representation) will be impacted. An `InstancedTileRenderer` or similar dispatching system will be introduced.
- **Materials**: The base Cesium materials might be adjusted to support `enableInstancing = true` and consume per-instance matrices from `UNITY_MATRIX_M` variations if needed.
- **Performance**: Significant reduction in draw calls and CPU overhead for i3dm and `EXT_mesh_gpu_instancing` tilesets.
- **Testing**: Requires full automated test suite validation explicitly compiled and executed against `C:\Program Files\Unity\Hub\Editor\6000.1.3f1\Editor\Unity.exe` for:
  - Validating standard instanced rendering programmatically.
  - Verifying VR/XR Single Pass Instanced rendering works correctly for instanced tiles.
  - Ensuring clipping rasters function correctly on instanced vs non-instanced tiles via test automation.
  - Verifying metadata and feature IDs for instanced features.
