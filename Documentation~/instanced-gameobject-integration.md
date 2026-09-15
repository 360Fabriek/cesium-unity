# Raster-aware instancing over the existing GameObject renderer

This integrates with the implementation at `094e8403509e3c1bab3e367e2f658078ec0ab3bf`
on `feat/instanced-tiles-v4`. It does not install the alternative ZIP renderer or
replace `UnityPrepareRendererResources` / `CesiumGltfGameObject` with another layout.

## Retained structure

The original native renderer still creates the tile root, prototype GameObjects,
MeshFilters, MeshRenderers, globe anchors, materials and meshes. The existing
`UnityPrepareInstancedRendererResources` adapter temporarily suppresses instance
expansion while loading one prototype per primitive, restores the instance
extension, then adds `CesiumInstancedRenderer` to each prototype. The base loader's
private load-thread hand-off is passed back unchanged. Its GameObject-created
callback still runs; non-raster MaterialPropertyBlock overrides set on prototypes
by that callback are now copied into the batched draws.

The prototype MeshRenderer remains disabled to avoid drawing an extra prototype.
Its `forceRenderingOff`, shadow-casting mode, receive-shadows flag and rendering
layer mask control the batched draws. Disable the tile/prototype GameObject or the
CesiumInstancedRenderer component to stop submission. MeshRenderer.enabled is not
a visibility control here, because that renderer is deliberately disabled. Runtime
material/mesh replacement and changes to the optional CesiumInstancedRendering
settings still require tileset recreation. Changes to the existing mesh's bounds
are detected without replacing the mesh.

One prototype/anchor is retained per primitive, not per placement. Colliders remain
optional child GameObjects. There is no second tile hierarchy or GPU buffer owner.
The batch limit remains the current branch's 128, not the ZIP candidate's 250.

## Raster and coverage integration

The existing `CesiumInstancedRaster.hlsl` and multi-target Shader Graphs are retained
unchanged. They use placed geographic positions, not the prototype's overlay UVs.
The geometry coverage rectangle is independent of the currently attached raster
image's bounds; attaching an ancestor image does not enlarge a child region.
Boundary-straddling instances remain eligible for submission and the shader clips
fragments. Polygon masks retain the existing clipping-material behavior. An
ordinary imagery texture's extent is not automatically a geometry cut instruction.

The guarded CMake adapter now also intercepts coordinate generation for models
already marked by this backend. It returns projected coverage and cached placed
height bounds without appending another prototype UV buffer/accessor for every
synthetic raster-child level. The existing child upsampler continues to copy the
model with its instance accessor indices intact. Unmarked/legacy models still run
through the original Native coordinate generator and upsampler.

This removes repeated unused UV generation, not all native model copies. First-load
prototype coordinate generation and native model-buffer duplication between virtual
children remain potential loading/memory costs. Mesh/material sharing across those
native children is not introduced by this integration.

The adapter normalizes CRLF input, checks that each replacement context and target
source occurs exactly once, and writes only a build-tree copy. Source/API drift
fails CMake configuration instead of silently patching an unintended occurrence.
The cesium-native submodule revision and checked-out source remain unchanged.

## Visibility, edits and cleanup

Instance bounds, not instance origins, are used for conservative coverage rejection.
Changing the prototype's ECEF placement or mesh bounds rebuilds membership so an
instance rejected earlier is not permanently missing after an edit. Once edited,
the original native coverage box's height range cannot safely reject the new
geometry; the renderer submits all usable placements and leaves the final coverage
decision to the shader. Moving back to the original placement/bounds restores the
original conservative pruning. This does not update Native's tileset traversal
bounds for arbitrarily animated datasets.

Origin shifts update cached world matrices but do not regroup unchanged geographic
placements. Mirrored transforms and ordinary floating-point roundoff are accepted;
non-finite, singular or projective placement data is rejected. Singular parent
transforms temporarily suppress submission until a valid transform returns.

Native cleanup now calls the managed `Release` barrier before returning meshes to
the original renderer's pool. It deactivates the tile, unregisters future camera
callbacks, clears batch/placement data and drops borrowed raster references.
Repeated release and late raster callbacks are safe. It does not destroy borrowed
meshes, source materials or raster textures; their original owners still free them.
This prevents future submission after release, not cancellation of draws already
queued earlier in the frame.

A malformed raster replacement is validated before changing texture/slot identity.
The previously attached raster therefore remains detachable and usable. Detach of
an older ancestor still cannot clear its replacement.

## Compatibility and limitations

Unity 2022.3 is the baseline. The current Built-in, URP and HDRP shader targets are
retained, with Graphics.RenderMeshInstanced as the batching backend. Hardware without
instancing uses the current per-mesh submission fallback with the same raster
properties. Translucent materials are not forced into unsorted instance groups.

Models using primitive feature components, skins, morph targets, animation or glTF
BLEND materials stay on the original GameObject renderer. A tileset under the older
CesiumMetadata component also keeps the original path. These guards preserve that
path's existing behavior; they do not add features it did not already support.
In particular, its existing instanced raster limitations remain. There is no new
per-instance metadata/picking implementation or arbitrary custom-shader support.

Collider-only objects preserve the prototype's physics settings and are synchronized
when batch membership changes. Raster fragment clipping remains visual: it does not
cut collision triangles, generate closed cut surfaces, or implement per-pixel raycasts.
Full per-instance collision can still dominate object counts and loading cost.

## Build and verify

Regenerate Reinterop before rebuilding: native cleanup now calls the new managed
`CesiumInstancedRenderer.Release(GameObject)` binding. Keep the working compiler,
triplet and short build directory. Close Unity before installing the rebuilt plugin.
For the short directory used in the earlier troubleshooting:

```powershell
cmake --build "C:\b\cu-v4" --config RelWithDebInfo --target install --parallel 8
if ($LASTEXITCODE -ne 0) { throw "Native integration build failed." }
```

The changed CMake include causes regeneration on an existing configured build. A
clean dependency download or submodule update is not needed for these changes.

Offline checks, from the repository root:

```text
python tools~/test_instanced_gameobject_integration.py
python tools~/test_projection.py
```

Validation performed while preparing this integration:

- 13 offline checks passed. These execute CMake against minimal upstream-context
  fixtures and check source contracts; they are not full Native or Unity builds.
- The unchanged branch projection test passed 10,000 comparisons using the actual
  HLSL scalar formula compiled as C++ floats. Maximum longitude/latitude/Mercator
  projected errors were approximately 1.24 / 0.40 / 3.04 millimetres over the test's
  limited 2 km horizontal-axis offsets and 0-500 m height range. This is not a global
  accuracy guarantee or a shader-performance measurement.
- 13 new Unity NUnit cases are supplied in TestCesiumInstancedGameObjectIntegration.
  They were not run in this environment.
- Native/C# compilation, Shader Graph import, rendering on the three pipelines,
  collision tests and Rotterdam performance measurements remain unverified.

The modified baseline files were reconstructed from connector reads and verified
against their Git blob hashes. Validation used that source snapshot, not a complete
Unity project checkout. Before deployment, test a canopy crossing the polygon/child
boundary, origin shifts, parent-to-child raster replacement, tile deactivate/reactivate,
and repeated load/unload. Verify color, depth and shadow results in each pipeline in
use, and compare timings in a player with an optimized native build.
