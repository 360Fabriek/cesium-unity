## ADDED Requirements

### Requirement: Asynchronous Mesh Creation
The system SHALL utilize Unity's `WritableMeshData` API to prepare vertex and index buffers on background threads before applying them to the mesh.

#### Scenario: Rendering a new tile
- **WHEN** a tile is loaded and its glTF data is parsed in a background thread
- **THEN** the Unity mesh data is populated asynchronously and applied to the Unity Mesh with minimal main-thread blocking

### Requirement: Asynchronous Texture Upload
The system SHALL prepare texture pixel data in NativeArrays on background threads and upload them efficiently to the GPU.

#### Scenario: Applying a texture to a tile
- **WHEN** a tile's texture is decoded
- **THEN** the texture data is moved to the GPU using efficient Unity APIs (e.g. `SetPixelData`) avoiding large memory copies on the main thread

### Requirement: Configurable Texture Upload Settings
The system SHALL expose an optional setting on the `Cesium3DTileset` component to control the texture upload behavior (such as `AsyncTextureUpload` vs synchronous limits).

#### Scenario: Tuning texture upload performance
- **WHEN** an application requires strict control over texture upload time on the main thread
- **THEN** the developer can configure the texture upload strategy on the `Cesium3DTileset` component.
