# Material parity and diagnostic draw comparison

This change builds on `71d56af0a1c92d9bc99292bf6eb40b12191e7779` of
`feat/instanced-tiles-v4`. It retains the existing native/GameObject adapter,
shared meshes and materials, instance placements, raster shader, and all three
Shader Graph targets. It does not replace source textures, flip normals, disable
backface culling, force unlit shading, or change metallic/roughness values.

## Corrections

The batching adapter no longer unconditionally changes the source material's
alpha-clipping properties after the native loader and GameObject-created
callback have configured it. It enables instancing without modifying surface
state or shader keywords. The stock Cesium materials already enable the clipping
passes; keep their clipping configuration when using raster masks. Deliberately
disabling alpha-tested passes in a custom material is not a supported way to
retain raster clipping in depth/shadows.

Prototype material-slot zero property blocks now take precedence over
renderer-level property blocks, matching Renderer.SetPropertyBlock. When there
is no slot block, the renderer-level block is used. The two levels are NOT
merged. This copies the effective block at initialization; changes made to a
prototype's property blocks after configuration still require tileset recreation.
Cesium's reserved raster bindings are then set by the adapter as before.

Light-probe and reflection-probe usage flags are read from the retained prototype
at submission time, like its shadow and rendering-layer flags. This does not
implement a replacement lighting system or promise that all probe workflows are
equivalent across render pipelines. Unity 2022.3 documents BlendProbes as automatic
per-instance light-probe setup for RenderMeshInstanced; the absence of manually
supplied spherical harmonics is not, by itself, a missing-lighting bug.

## Compare the same loaded content

Add **Cesium Instanced Rendering** to the affected Cesium3DTileset GameObject
before loading the tiles, or recreate the tileset once after adding it. Leave
**Enable Instanced Rendering** on. These settings are captured by the generated
prototype renderer; adding the settings component to already-configured
prototypes without recreating the tileset will not retrofit its reference.

With a fixed camera and settled tile loading, capture the affected object with
**Force Individual Draws** off, then turn only that setting on and capture again.
Return it to off after the comparison. The switch is read live; changing only
this diagnostic setting does not request a tileset recreation. Other settings
(batch size, spatial cells, collisions, or enabling/disabling the adapter) still
require recreation. Keep camera, lighting and loaded content unchanged; ongoing
streaming can still change an otherwise identical view.

Off uses Graphics.RenderMeshInstanced when supported. On submits the same cached
matrices with individual Graphics.RenderMesh calls. It keeps the same material,
property block, overlay textures and coverage clipping and does not reactivate
the untransformed prototype. It is a diagnostic submission mode, not a performance
recommendation. Inspect the Frame Debugger to verify the actual shader pass,
variant, bindings and draw count on the active pipeline.

If only batched submission looks wrong, inspect the instanced shader/lighting
and transform path. If both submissions look wrong, investigate their shared
material inputs, mesh attributes, overlay bindings and scene lighting. Neither
outcome alone proves a specific missing texture, normal or reflection probe.

## Capture the material state

Open the **Cesium Instanced Rendering** component's context menu and choose
**Log Material Diagnostics**. Copy the resulting Console entry. It identifies
the Unity version, active render pipeline and graphics API automatically, then
reports up to eight loaded prototypes. The environment reflection entry is a
hint, not a measurement of the cubemap actually bound by URP/HDRP for a draw.

For a specific dark object, enable the tileset's existing hierarchy display and
use **Log Material Diagnostics** on that prototype's CesiumInstancedRenderer
component. The report includes shader/keywords, material factors, effective
texture overrides, normal/tangent/color/UV0 presence, negative world-determinant
counts, probe modes and attached raster keys. Logging is on demand; there is no
per-frame diagnostic scan or automatic material repair.

## Build and validation

No native source, CMake settings, interop call signatures, shader source or
Cesium Native submodule revision changes are included. Let Unity compile the C#
changes. A clean native/dependency rebuild is not required for this patch; keep
the native plugin and generated bindings from the preceding integration.

Run `Tests/TestCesiumInstancedMaterials.cs` in the Unity Test Runner. These tests
cover state preservation, slot-block precedence, block lifetime and the live
comparison switch; they do not replace image-based pipeline acceptance tests.
Run `python tools~/test_instanced_material_contracts.py` for offline source
contracts. Those checks do not compile or execute Unity C# or shaders.

The reported screenshot has not been reproduced in a Unity runtime here. The
material-state corrections are not a claim that every dark surface is fixed.
Compare color, alpha-cutout edges, depth, shadows and raster boundaries in the
actual project before accepting the change.

## References

- Renderer.SetPropertyBlock: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer.SetPropertyBlock.html
- Renderer.GetPropertyBlock: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer.GetPropertyBlock.html
- Graphics.RenderMeshInstanced: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Graphics.RenderMeshInstanced.html
