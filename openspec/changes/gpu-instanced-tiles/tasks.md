## 1. Preparation and Core Setup

- [x] 1.1 Create `InstancedTileRenderer.cs` inside `Runtime/` to handle `Graphics.DrawMeshInstanced` logic.
- [x] 1.2 Update `ConfigureReinterop.cs` to expose `MaterialPropertyBlock` and vector/matrix array setters to C++.

## 2. C++ Data Parsing and Extraction

- [x] 2.1 Update `UnityPrepareRendererResources.cpp` to parse `EXT_mesh_gpu_instancing` extensions from glTF models.
- [x] 2.2 Construct the transformation matrix arrays for instances, ensuring `positionScaleFactor` is correctly multiplied. Explicitly apply the correct handedness coordinate conversion (Cesium Native to Unity) and correct negative scaling that could flip normals or winding orders.
- [x] 2.3 Plumb instance features and metadata extraction logic into the primitive parsing workflow.

## 3. Rendering Implementation

- [x] 3.1 Implement logic in `InstancedTileRenderer` to populate the `MaterialPropertyBlock` with the received instance arrays and properties.
- [x] 3.2 Dispatch `Graphics.DrawMeshInstanced` in batches (max 1023) within the `InstancedTileRenderer` update loop.
- [x] 3.3 Ensure materials used by instanced primitives enable Unity's GPU instancing (`enableInstancing = true`).
- [x] 3.4 Inject the full suite of VR/XR instancing compatibility macros (`UNITY_SETUP_INSTANCE_ID`, `UNITY_TRANSFER_INSTANCE_ID`, `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO`, `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX`) and `#pragma multi_compile_instancing` into Cesium's shader passes.
- [x] 3.5 Ensure `InstancedTileRenderer` instance culling bounds safely accommodate the stereo parameters for XR.

## 4. Clipping rasters and Fallbacks

- [x] 4.1 Update raster clipping evaluation to check compatibility with instanced meshes.
- [x] 4.2 If complex clipping is active for an instanced tile, dynamically fallback the tile to standard `GameObject` instantiation to preserve visual correctness.

## 5. Metadata and Features Validation

- [x] 5.1 Ensure the `CesiumPrimitiveFeatures` or equivalent representation can serve the features for specific instance indices.
- [x] 5.2 Validate that standard picking / raycasting identifies the correct instanced instance ID and yields the correct metadata.

## 6. Testing

*Note: All tests must include full automated validation and be strictly tested against Unity `6000.1.3f1` using the executable at `C:\Program Files\Unity\Hub\Editor\6000.1.3f1\Editor\Unity.exe`.*

- [ ] 6.1 Create fully automated test validation scenes using the provided test tilesets and location to programmatically verify batched rendering:
  - **Location**: lat `40.04253068`, lon `-75.61209424`, height `10.0096`
  - **Test Tileset 1**: `https://sandcastle.cesium.com/SampleData/Cesium3DTiles/Instanced/InstancedWithBatchTable/tileset.json`
  - **Test Tileset 2**: `https://sandcastle.cesium.com/SampleData/Cesium3DTiles/Instanced/InstancedOrientation/tileset.json`
  - **Test Tileset 3**: `https://raw.githubusercontent.com/CesiumGS/cesium/refs/heads/main/Specs/Data/Cesium3DTiles/Instanced/InstancedTextured/tileset.json`
  - **Test Tileset 4**: `https://raw.githubusercontent.com/CesiumGS/3d-tiles-samples/refs/heads/main/1.0/TilesetWithTreeBillboards/tileset.json`
- [ ] 6.2 Write automated PlayMode tests for clipping rasters against an instanced tileset to verify the fallback or shader implementations work robustly.
- [ ] 6.3 Write automated tests for metadata extraction establishing that logic correctly targets instanced geometries.
- [ ] 6.4 Validate rendering in a VR environment with Single Pass Instancing enabled.
- [ ] 6.5 Execute the complete automated test suite locally utilizing `C:\Program Files\Unity\Hub\Editor\6000.1.3f1\Editor\Unity.exe` in batchmode (`-runTests`).
