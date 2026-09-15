# Instanced tilesets

This implementation renders node-level `EXT_mesh_gpu_instancing` in Cesium for
Unity. It uses the existing Cesium Native converters for `i3dm` and `cmpt`,
including embedded and externally referenced GLBs; it does not introduce a
second legacy tile parser or change the Cesium Native submodule revision.

## Rendering and ownership

Instance attributes are decoded once per node on the load thread and shared by
its primitives. Translation and scale use FLOAT VEC3 accessors; rotation accepts
FLOAT or normalized signed BYTE/SHORT VEC4 accessors. Omitted attributes use
identity defaults. Invalid accessors, mismatched counts and non-finite values
reject the instance group with a warning. Empty groups do not render a fallback
mesh at the origin.

The renderer composes `tileTransform * nodeTransform * instanceTransform` in
double precision before assigning a globe anchor. Existing RTC and up-axis
handling is retained. Each instance has a GameObject, renderer, globe anchor and,
when requested, collider. Instances of a source primitive share its mesh and
material. Mesh upload and physics baking remain per source primitive. Tile-owned
resources are released once, rather than once per instance.

The transform-decoding approach follows Cesium for Unreal's `loadInstancingData`
in [CesiumGltfComponent.cpp](https://github.com/CesiumGS/cesium-unreal/blob/8e45cd9f1be83da72edba2bf32558ffa187831ca/Source/CesiumRuntime/Private/CesiumGltfComponent.cpp).
Unreal's coordinate reflection is not copied: Unity's existing vertex-upload
path retains glTF coordinates.

## Build

Use a source checkout and follow [Developer Setup](developer-setup.md). From the
package root, initialize submodules and build Reinterop before opening Unity:

```sh
git submodule update --init --recursive
dotnet publish Reinterop~ -o .
```

Open the containing Unity project and let Unity regenerate the Reinterop
bindings, including the new `Material.enableInstancing` setter. Then build and
install the native plugin:

```sh
cmake -S native~ -B native~/build -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build native~/build --target install --config RelWithDebInfo --parallel
```

A C#-only rebuild or an existing precompiled native plugin will not include this
implementation. Restart Unity after rebuilding the plugin if necessary.

## Validation status

The source patch was checked against the exact upstream file hashes and passed
`git diff --check`. The accompanying patch bundle's 10 Python checks passed,
covering patch application and synthetic fixture structure. Those checks do not
compile the native renderer or validate Unity behavior.

The native unit-test source could not be uploaded through the repository tool,
so this branch does not add the optional native test executable or its CMake
target. Native compilation, Unity rendering and the live Rotterdam endpoint
remain unverified. Run a full source build and the rendering checks below before
using the implementation in production.

## Reproducible rendering fixtures

Generate small, self-authored fixtures outside the package source tree:

```sh
python native~/tests/instancing/generate_fixtures.py --output /tmp/cesium-instancing-fixtures
python -m http.server 8000 --bind 127.0.0.1 --directory /tmp/cesium-instancing-fixtures
```

On Windows, replace `/tmp/cesium-instancing-fixtures` with a writable directory.
Use `Cesium3DTileset` with a URL such as
`http://127.0.0.1:8000/nested-tileset.json`. Set the georeference to longitude 0,
latitude 0, height 0 and position the camera to view the local origin. Enable
`showTilesInHierarchy` to inspect the generated renderers.

| Fixture | Renderers | Referenced unique meshes | Referenced unique materials |
| --- | ---: | ---: | ---: |
| Embedded GLB in i3dm | 2 | 1 | 1 |
| External GLB from i3dm | 3 | 1 | 1 |
| Nested cmpt with i3dm and b3dm | 6 | 3 | 3 |
| Direct instanced GLB | 3 | 1 | 1 |
| Multiple primitives | 4 | 2 | 2 |
| Mismatched instance counts | 0 | 0 | 0 |

`expected.json` records these expectations. The invalid group can still have an
allocated, tile-owned source mesh; the table counts meshes referenced by
renderers. Check placement, rotation, non-uniform/negative scale, origin shifts,
colliders, tile hiding/unloading and repeated reloads. Verify ordinary tiles and
mixed composite content still work before testing the Rotterdam endpoint.

## Limitations

- This is a shared-resource GameObject implementation, not Unreal's instanced
  component backend. `enableInstancing` allows compatible materials to batch,
  but GPU batching and performance depend on Unity's shaders and render pipeline.
- Per-instance `EXT_instance_features` metadata, batch-ID picking and styling are
  not implemented. Primitive metadata remains separate from instance metadata.
- Raster overlay attachment is skipped for instanced primitives because existing
  generated overlay UVs are not per-instance. Ordinary primitives use explicit
  GameObject-to-primitive mapping rather than hierarchy order.
- The live Rotterdam tileset and platform-specific rendering have not been
  validated. Use the fixtures first; they contain no copied Rotterdam data.
