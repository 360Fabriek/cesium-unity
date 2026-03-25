## 1. Asynchronous Mesh and Texture Creation

- [ ] 1.1 Update `UnityPrepareRendererResources.cpp` to use Unity's `Mesh.AllocateWritableMeshData`.
- [ ] 1.2 Move vertex and index buffer memory population into the background load thread.
- [ ] 1.3 Apply the populated mesh data on the main thread using `Mesh.ApplyAndDisposeWritableMeshData` to minimize hitching.
- [ ] 1.4 Refactor texture pixel data preparation to use `NativeArray`s in background threads.
- [ ] 1.5 Apply the texture data using `Texture2D.SetPixelData` and avoid unnecessary main thread memory copies.
- [ ] 1.6 Expose an optional setting on `Cesium3DTileset` component to configure `AsyncTextureUpload` vs synchronous texture limits.

## 2. XR Single-Pass Instanced Rendering Support

- [ ] 2.1 Audit all `.shader` and `.hlsl` files within the `cesium-unity` package for XR compatibility.
- [ ] 2.2 Insert `UNITY_VERTEX_INPUT_INSTANCE_ID` into all vertex input structs.
- [ ] 2.3 Insert `UNITY_SETUP_INSTANCE_ID(v)`, `UNITY_TRANSFER_INSTANCE_ID(v, o)`, and `UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o)` into all vertex shaders.
- [ ] 2.4 Test rendering in an XR headset to verify stereo rendering works in single-pass instanced mode without multi-pass fallback.
