## ADDED Requirements

### Requirement: GPU Instanced Rendering
The system SHALL use Unity's `Graphics.DrawMeshInstanced` API to render multi-instance glTF primitives when `EXT_mesh_gpu_instancing` or i3dm formats are used.

#### Scenario: Rendering an instances mesh
- **WHEN** a glTF primitive has an active `EXT_mesh_gpu_instancing` extension
- **THEN** the tileset component dispatches a single batched draw call instead of creating independent `GameObject` instances.

### Requirement: Clipping Compatibility
The system MUST handle clipping rasters correctly on instanced components.

#### Scenario: Fallback for complex clipping
- **WHEN** a tile requires per-instance clipping evaluations that cannot be efficiently handled within the instanced global array shader properties
- **THEN** the system MUST fall back to traditional non-instanced `GameObject` rendering for that primitive to ensure visual correctness.

### Requirement: Instance Properties Correctness
The system SHALL apply scale factors and translation factors accurately to the instances.

#### Scenario: Instance Translations Scale Factor
- **WHEN** building instance translations from glTF accessors
- **THEN** it multiplies by `CesiumPrimitiveData::positionScaleFactor` equivalent and populates a MaterialPropertyBlock matrix array.

### Requirement: Feature IDs and Metadata
The system SHALL surface the correct instance-level feature IDs and metadata for picking and evaluation.

#### Scenario: Accessing instanced features
- **WHEN** a blueprint, component or script requests primitive features from an instanced mesh (`InstancedTileRenderer` equivalent)
- **THEN** the correct instance features are retrieved.

### Requirement: VR / XR Compatibility
The system MUST render instanced meshes correctly in VR using Single Pass Instanced rendering.

#### Scenario: Rendering in a VR Headset
- **WHEN** the application is running in VR with Single Pass Instanced rendering enabled
- **THEN** both eyes display the instanced meshes at their correct positions and orientations.

### Requirement: Correct Orientation and Normals
The system MUST correctly convert the instanced coordinates from glTF/Cesium Native space to Unity space avoiding orientation distortion or flipped normals.

#### Scenario: Handedness Conversion
- **WHEN** building the instance matrices for `DrawMeshInstanced`
- **THEN** it strictly applies Unity's left-handed conversion and negative scale compensation, ensuring the mesh renders right-side out with proper upright orientation.
