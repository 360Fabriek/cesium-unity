# Instanced Tiles

Cesium for Unity renders i3dm and `EXT_mesh_gpu_instancing` content through
`InstancedTilesetRenderer`. The renderer stores one shared mesh/material pair per
primitive and submits per-instance matrices with Unity GPU instancing. This keeps
CPU objects and draw calls low for dense content such as trees, poles, and street
furniture.

## MeshFilter Limitation

Normal 3D Tiles primitives expose `MeshFilter` and `MeshRenderer` components
because each primitive is a Unity mesh object. Instanced tiles deliberately do
not create one renderable mesh object per instance. Creating thousands of
`MeshFilter`/`MeshRenderer` pairs would defeat the memory and draw-call benefit
of i3dm.

The renderer keeps hidden transform objects for per-instance placement, but
those objects are not renderers. Code that needs a mesh can use the shared mesh
from `InstancedTilesetRenderer.InstanceGroupData`. Code that needs one Unity renderer
per instance should convert the source tileset to non-instanced glTF content
instead, accepting the performance and memory cost.

## Raycasting

Exact triangle raycasts require one collider mesh per instance. This can be very
expensive, especially in standalone VR, so the default remains no extra collider
proxies.

Available paths:

- Enable physics mesh creation on the tileset. Native preparation can attach
  `MeshCollider` components to hidden instance objects.
- Set `InstancedTilesetRenderer.colliderMode` to `BoxPerInstance`. This creates
  capped per-instance `BoxCollider` proxies and attaches
  `InstancedTilesetRaycastHit`, which exposes `instancedRenderer`, `groupId`,
  `primitiveIndex`, and `instanceIndex` from `Physics.Raycast` results.

Use `maxRaycastColliders` to cap memory and physics broadphase cost. Box proxies
are coarse and should be used for selection, not precise surface picking.

## Duplicate Rendering

The renderer guards against duplicate draws in one frame and disables renderers
on captured child instance transforms. This avoids the common failure mode where
both hidden instance objects and the GPU-instanced renderer draw the same visual
content.

Group IDs include tile and primitive identity. Re-adding an existing group
replaces the old group and destroys its raycast proxies.

## VR Performance

Standalone VR is sensitive to per-instance GameObjects, colliders, and CPU
culling. Keep `colliderMode` disabled unless selection is needed, keep
`maxRaycastColliders` low, and prefer the default renderer path with Unity GPU
instancing enabled on materials.

Instanced tiles rely on Cesium tile selection for frustum culling. The renderer
does not run a separate Unity-camera frustum pass per instance. This avoids
duplicating Cesium's visibility logic and keeps collider visibility tied to the
same selected-tile active state used for rendering. Forwarded child-tile bounds
are accepted for native compatibility but are not used to hide individual
instances, because refined child bounds can be smaller than the instanced
content and cause trees or street furniture to disappear as the camera moves.

Instance draw matrices are computed from the original double-precision
globe-fixed matrices when a georeference is available. Hidden instance
transforms are still kept for metadata, raycast proxies, and compatibility, but
rendering does not depend on their single-precision Unity transform matrices.
