## ADDED Requirements

### Requirement: Single-Pass Instancing Support
All Cesium shaders and materials SHALL support Unity's single-pass instanced XR rendering pipeline via standard Unity macros.

#### Scenario: Running in an XR headset
- **WHEN** the application is built for an XR platform with single-pass instanced rendering enabled
- **THEN** the Cesium tiles render correctly in both eyes simultaneously without falling back to multi-pass rendering
- **THEN** no duplicated draw calls exist for the left and right eye
