using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace CesiumForUnity
{
    // Integration with the existing prototype GameObjects. This does not own
    // native meshes, source materials, raster textures, or a second tile hierarchy.
    internal partial class CesiumInstancedRenderer
    {
        private MeshRenderer _sourceRenderer;
        private readonly Dictionary<int, MeshCollider> _instanceColliders = new Dictionary<int, MeshCollider>();
        private bool _released;
        private int _maximumInstances;
        private double _cellSize;
        private bool _clipCoverage;
        private double4x4 _coverageBoxToChart;
        private double4x4 _originalBaseEcef;
        private Bounds _originalMeshBounds;
        private Bounds _lastMeshBounds;
        private bool _createColliders;
        private MeshColliderCookingOptions _colliderCooking;
        private PhysicMaterial _colliderMaterial;
        private bool _colliderConvex, _colliderTrigger;
        private float _colliderContactOffset;

        private void InitializeIntegration(MeshRenderer source, int maximum, double cellSize,
            bool clipCoverage, double4x4 coverageBoxToEcef)
        {
            _sourceRenderer = source;
            _maximumInstances = Mathf.Clamp(maximum, 1, 128);
            _cellSize = math.isfinite(cellSize) ? Math.Max(1.0, cellSize) : 128.0;
            _clipCoverage = clipCoverage;
            _coverageBoxToChart = math.mul(math.inverse(_chartToEcef), coverageBoxToEcef);
            _originalBaseEcef = _baseEcef;
            _originalMeshBounds = _lastMeshBounds = _mesh.bounds;
        }

        private void RebuildBatches()
        {
            _batches.Clear();
            // The native coverage box includes the original height range. After
            // editing a prototype, that range is no longer a conservative bound
            // for the new surface. Submit all placements and keep shader clipping
            // rather than accidentally removing visible, boundary-crossing parts.
            bool canPrune = _clipCoverage && _baseEcef.Equals(_originalBaseEcef) &&
                _mesh.bounds.Equals(_originalMeshBounds);
            BuildBatches(_maximumInstances, _cellSize, canPrune, _coverageBoxToChart);
        }

        private void ConfigureColliders(bool enabled)
        {
            MeshCollider prototype = GetComponent<MeshCollider>();
            // Degenerate meshes never had a collider; do not introduce one.
            if (prototype == null) return;
            _createColliders = enabled && prototype.enabled;
            _colliderCooking = prototype.cookingOptions;
            _colliderMaterial = prototype.sharedMaterial;
            _colliderConvex = prototype.convex;
            _colliderTrigger = prototype.isTrigger;
            _colliderContactOffset = prototype.contactOffset;
            prototype.enabled = false;
            SynchronizeColliders();
            UnityLifetime.Destroy(prototype);
        }

        private void SynchronizeColliders()
        {
            if (!_createColliders || _released) return;
            var retained = new HashSet<int>();
            foreach (Batch batch in _batches)
            foreach (int i in batch.indices)
            {
                retained.Add(i);
                if (_instanceColliders.TryGetValue(i, out MeshCollider existing) && existing != null) continue;
                double4x4 m = _instances[i];
                double3 scale = new double3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz));
                if (math.any(scale <= 0.0) || !math.all(math.isfinite(scale))) continue;
                double3x3 rotation = new double3x3(m.c0.xyz / scale.x, m.c1.xyz / scale.y, m.c2.xyz / scale.z);
                if (math.determinant(rotation) < 0.0) { scale.x = -scale.x; rotation.c0 = -rotation.c0; }
                quaternion q = new quaternion(new float3x3((float3)rotation.c0, (float3)rotation.c1, (float3)rotation.c2));
                var child = new GameObject("Instance collision " + i);
                child.hideFlags = gameObject.hideFlags;
                child.layer = gameObject.layer;
                child.transform.SetParent(transform, false);
                child.transform.localPosition = (Vector3)(float3)m.c3.xyz;
                child.transform.localRotation = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
                child.transform.localScale = (Vector3)(float3)scale;
                var collider = child.AddComponent<MeshCollider>();
                collider.cookingOptions = _colliderCooking;
                collider.sharedMaterial = _colliderMaterial;
                collider.convex = _colliderConvex;
                collider.isTrigger = _colliderTrigger;
                collider.contactOffset = _colliderContactOffset;
                collider.sharedMesh = _mesh;
                _instanceColliders[i] = collider;
            }
            var removed = new List<int>();
            foreach (var entry in _instanceColliders)
            {
                if (retained.Contains(entry.Key)) continue;
                if (entry.Value != null)
                {
                    // Destroy is deferred in Play mode; stop collisions now.
                    entry.Value.enabled = false;
                    entry.Value.gameObject.SetActive(false);
                    UnityLifetime.Destroy(entry.Value.gameObject);
                }
                removed.Add(entry.Key);
            }
            foreach (int index in removed) _instanceColliders.Remove(index);
        }

        // Called synchronously by the native free path BEFORE the base renderer
        // returns meshes to its pool. OnDisable alone is insufficient because
        // the base GameObject destruction can be deferred until end of frame.
        internal static void Release(GameObject model)
        {
            if (model == null) return;
            model.SetActive(false);
            foreach (var renderer in model.GetComponentsInChildren<CesiumInstancedRenderer>(true))
                renderer.ReleaseRuntime();
        }

        private void ReleaseRuntime()
        {
            if (_released) return;
            _released = true;
            _initialized = false;
            OnDisable();
            foreach (var entry in _instanceColliders)
                if (entry.Value != null) entry.Value.enabled = false;
            _instanceColliders.Clear();
            _batches.Clear();
            _instances = Array.Empty<double4x4>();
            Array.Clear(_overlays, 0, _overlays.Length);
            Array.Clear(_mapping, 0, _mapping.Length);
            Array.Clear(_projection, 0, _projection.Length);
            if (_properties != null) _properties.Clear();
            _renderParams = default;
            // Borrowed objects are deliberately not destroyed here.
            _mesh = null;
            _material = null;
            _sourceRenderer = null;
            _anchor = null;
            _georeference = null;
            _colliderMaterial = null;
        }

        private void OnDestroy()
        {
            ReleaseRuntime();
        }
    }
}
