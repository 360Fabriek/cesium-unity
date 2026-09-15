# Instanced tilesets and raster overlays

The batched backend targets Unity 2022.3 and the existing Built-in, URP, and HDRP
Shader Graph targets. It replaces the v4 per-placement rendering hierarchy for
triangle models containing `EXT_mesh_gpu_instancing`. Cesium Native still handles
`i3dm`, embedded/external GLBs, and `cmpt`; no second legacy tile parser is added.

## Status

This is a source implementation requiring Unity validation, not a measured or
certified performance release. No Windows native build, Reinterop generation,
Unity shader compilation, player graphics test, or Rotterdam runtime benchmark
was executed in the authoring environment. Run the acceptance checks below in
your actual pipeline before deploying it.

The implementation is based on Unity branch commit
`87804fa6d01d0caf91770522b2dfccdf43e1d022` and Native revision
`80a22ff4337c5b7057cff53d0055045c15c6d350`. The Native submodule revision is unchanged.

## Rendering

`UnityPrepareInstancedRendererResources` wraps the existing renderer. It decodes
placements on the worker thread and temporarily removes the instance extensions
while the base mesh loader runs, so only one prototype GameObject/material is
created per source primitive. Extensions are restored before publishing the
model. An owned wrapper passes the base loader's opaque resources back unchanged;
it does not reinterpret the base loader's private structures.

Each prototype has one `CesiumInstancedRenderer` and one globe anchor. There is
no MeshRenderer or globe anchor per placement. Meshes and source materials retain
the existing once-per-tile ownership and cleanup. Instance matrices are copied
from Native synchronously; no borrowed native address is retained.

The component submits spatially grouped `Graphics.RenderMeshInstanced` calls.
The default cap is 128 placements and spatial cell size is 128 metres. This is a
conservative cap, not a claim about a universal optimal batch size. Matrices and
bounds are cached until the parent/georeference/anchor changes. Cameras do not
cause static placement matrices to be regenerated. Camera-specific submission
uses Built-in pre-cull and SRP begin-camera callbacks; no URP/HDRP assembly or
custom render feature is required by the shared runtime code.

When GPU instancing is unavailable, the component submits individual
`Graphics.RenderMesh` calls with the same mapping and clipping properties. This
retains geographic clipping but is not a performance-equivalent fallback.

## Raster mapping and clipping

Raster mapping uses the placed fragment's geographic position, not the shared
prototype's `_CESIUMOVERLAY_n` vertex coordinates. The backend corrects Native's
prototype-only geographic footprint using the placed vertices on the worker
thread. Geographic and Web Mercator projections use the actual attached image
rectangle. Ordinary imagery outside its coverage does not automatically hide
geometry. The existing material key `Clipping` retains the polygon-mask path,
including the overlay's already-rasterized invert-selection behavior.

Two separate bounds are retained:

* **Geometry coverage:** the region belonging to a synthetic raster child.
* **Image coverage:** the rectangle of the currently attached texture, which may
  temporarily belong to a larger ancestor.

A raster replacement changes sampling data, not the geometry coverage. A late
detach is matched by material key and texture identity, so it does not clear a
newer texture. Raster textures are borrowed in property blocks and are never
added to the source material's owned-texture list.

For synthetic children, a conservative box-overlap test can reject an instance
only when its entire enclosing geometry box is separated from the child's
coverage box. Testing the instance origin is deliberately insufficient. Border
instances remain in the batch and the fragment shader cuts the actual edge.
Render bounds still include the complete retained geometry for correct culling
and shadows; they are not used as a substitute for clipping.

All siblings use their original model's common geographic chart. Half-open
coverage bounds assign a shared edge consistently. The shader evaluates those
bounds before image UV clamping/sampling. Position mapping and clipping are in
the shared `CesiumRasterOverlay.shadersubgraph`, retaining its property GUIDs and
output slot. Both parent shaders and all three of their pipeline targets remain
unchanged. Alpha clipping is enabled on batched materials so the helper can be
used in their generated depth and shadow passes. Verify those passes in Unity;
preserving graph targets alone is not proof of graphics correctness.

The projection is an origin-relative, single-precision evaluation of oblate
ellipsoid coordinates. It does not reconstruct a single-precision ECEF position
and subtract nearly equal geographic coordinates. Double precision is retained
on the CPU through composition and chart setup. This does not eliminate normal
Unity-world float precision limits; keep the georeference near the camera.

## Native raster refinement

The pinned Native raster upsampler rewrites prototype mesh accessors, which is
not valid for independent instance placements. The scoped CMake integration in
`native~/cmake/PatchInstancedRasterUpsampling.cmake` creates a build-tree copy of
that source with a marked-model branch that preserves the model/accessors and
sets the synthetic-child marker. The renderer then clips to the child's region.
Unmarked models retain Native's original upsampling behavior.

The checked-out submodule is not edited. Configuration fails explicitly when the
source anchor no longer matches, so a Native upgrade requires reviewing this
integration. Synthetic children still copy Native model data and allocate their
own renderer resources. Cross-tile GPU resource deduplication is not implemented;
very deep raster refinement can increase memory use despite instance culling.

## Settings and compatibility

Compatible content uses the batched path by default. Add **Cesium Instanced
Rendering** to the same GameObject as **Cesium 3D Tileset** for explicit settings:

| Setting | Default | Effect |
| --- | --- | --- |
| Enable Instanced Rendering | On | Disable to return to the v4 compatibility renderer. |
| Maximum Instances Per Batch | 128 | Upper bound, clamped to 1..128. |
| Spatial Batch Size | 128 m | Groups placements; does not define a clipping boundary. |
| Create Instance Colliders | On | Keeps collision for retained placements when Create Physics Meshes is also enabled. |

The fast path accepts the supplied lit/unlit shaders (including material copies
using those shaders), triangle-only models, and spheres/oblate ellipsoids with
`e^2 <= 0.01` (including WGS84). Custom shaders, transparent material overrides,
point/line-containing models, and unsupported custom ellipsoids keep the v4 path.
That compatibility path retains its earlier limitation: instanced raster
attachment is skipped. There is no claim of generic custom-shader support.

Up to eight distinct raster material keys can be bound, subject to the keys
actually exposed by the material. The stock materials expose imagery keys
`0`, `1`, `2`, and `Clipping`.

Collision still creates GameObjects/colliders per retained placement. It is
separate from rendering and can remain expensive. Disable Create Instance
Colliders for render-only content; disable the tileset's Create Physics Meshes as
well to avoid source collision baking. Fragment clipping does not cut collision
meshes, cap cut surfaces, or implement volumetric clipping. Border colliders may
extend beyond the visible edge, including across synthetic-child boundaries.

Per-instance metadata/picking and instance GameObject customization are not
implemented by this backend. `OnTileGameObjectCreated` consumers now see
prototypes, not a GameObject per rendered placement. Disable the batched path
when those compatibility behaviors are required. Ray-traced rendering and
per-object motion-vector history are not added by this change.

## Build and validation

Regenerate Reinterop after importing the new C# code, then rebuild/install the
native plugin using your working source-build environment. Reuse the successful
MSVC toolset, triplet, and EZVCPKG_BASEDIR; do not recreate dependencies merely
because the rendering implementation changed. Close Unity before installing the
DLL. For example, from the package's `native~` directory and the same build shell:

```powershell
cmake --build "C:\b\cu-v4" --config RelWithDebInfo --target install --parallel 8
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }
```

The original Windows build directory must already be configured. CMake will
regenerate it for the added sources and scoped Native integration. A C#-only
rebuild is insufficient. Benchmark optimized native builds, not Debug builds.

Offline checks (Python 3, CMake; the numerical test also needs NumPy and C++):

```sh
python tools~/test_instanced_renderer_contracts.py
python tools~/test_projection.py
```

Authoring results: ten serialization/build-integration checks passed and one
full-checkout parent-target inspection was skipped. The CMake checks use a small
synthetic target, not a full Native build. The projection test compiles the
checked-in HLSL scalar formula as C++ floats and compares 10,000 samples against
double-precision geodesy (spherical/WGS84, latitudes 0, +/-51.92 and +/-80 degrees,
+/-2 km horizontal axis offsets, heights 0..500 m). Maximum projected-coordinate
errors were approximately 1.25 mm longitude, 0.41 mm latitude and 3.05 mm Mercator.
These are formula tests, not end-to-end GPU accuracy or shader compilation tests.

`Tests/TestCesiumInstancedRenderer.cs` supplies Unity tests for matrix layout,
conservative bounds, straddling geometry, chart axes, raster mapping,
antimeridian-equivalent longitude mapping, batch splitting, and identity-safe
raster replacement/detachment. These tests were not executed during authoring.
The subgraph can be regenerated with `tools~/generate_instanced_overlay_graph.py`.

Before acceptance, test each intended pipeline in Editor and a player build:

1. Load the existing embedded/external i3dm and nested-composite fixtures, then
   Rotterdam. Use Frame Debugger to verify instanced draws, not just the checkbox.
2. Place a clipping edge through a tree/mesh whose origin is outside but geometry
   extends inside. Verify the retained geometry, polygon inversion, depth,
   normals and shadows, including masked source materials.
3. Exercise raster parent fallback and refinement, neighboring synthetic children,
   origin shifts, nonuniform/mirrored instances, layer/camera filtering, tile
   unloading, play-mode transitions and domain reloads. No stale textures or
   duplicated boundary surfaces should remain.
4. Compare optimized-player CPU/GPU frame times, batch counts, loading spikes,
   allocations and memory with the batched setting on/off, with collisions also
   measured separately. No particular FPS improvement is claimed in advance.
