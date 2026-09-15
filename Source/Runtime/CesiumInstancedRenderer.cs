using System;
using System.Collections.Generic;
using Reinterop;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CesiumForUnity
{
    /// <summary>
    /// One component per source primitive, not per placement. The native tile owns
    /// the mesh/material; raster-overlay resources remain owned by Cesium Native.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("")]
    [Reinterop]
    internal class CesiumInstancedRenderer : MonoBehaviour
    {
        private const int MaximumOverlays = 8;
        private static readonly int WorldToChartId = Shader.PropertyToID("_CesiumInstanceWorldToChart");
        private static readonly int GeodeticId = Shader.PropertyToID("_CesiumInstanceGeodetic");
        private static readonly int MappingId = Shader.PropertyToID("_CesiumInstanceRasterMapping");
        private static readonly int ProjectionId = Shader.PropertyToID("_CesiumInstanceRasterProjection");
        private static readonly int CoverageId = Shader.PropertyToID("_CesiumInstanceCoverage");
        private static readonly int ClipCoverageId = Shader.PropertyToID("_CesiumInstanceClipCoverage");

        private sealed class Batch
        {
            internal int[] indices;
            internal Matrix4x4[] matrices;
            internal Bounds bounds;
        }

        private sealed class Overlay
        {
            internal string key;
            internal Texture texture;
            internal int textureId;
            internal int coordinateId;
            internal int translationScaleId;
        }

        private readonly List<Batch> _batches = new List<Batch>();
        private readonly Overlay[] _overlays = new Overlay[MaximumOverlays];
        private readonly Vector4[] _mapping = new Vector4[MaximumOverlays];
        private readonly Vector4[] _projection = new Vector4[MaximumOverlays];
        private MaterialPropertyBlock _properties;
        private Mesh _mesh;
        private Material _material;
        private CesiumGlobeAnchor _anchor;
        private CesiumGeoreference _georeference;
        private double4x4[] _instances;
        private double4x4 _chartToEcef;
        private double4x4 _baseEcef;
        private double4x4 _lastEcefToLocal;
        private Matrix4x4 _lastParent;
        private double _longitude, _latitude;
        private bool _dirty = true;
        private bool _initialized;
        private bool _canInstance;
        private bool _warnedOverlayLimit;
        private RenderParams _renderParams;

        internal static bool CanUse(Cesium3DTileset tileset)
        {
            if (tileset == null) return false;
            var settings = tileset.GetComponent<CesiumInstancedRendering>();
            if (settings != null && !settings.enableInstancedRendering) return false;
            var georeference = tileset.GetComponentInParent<CesiumGeoreference>();
            if (georeference == null) return false;
            double3 r = georeference.ellipsoid.radii;
            // The stable shader projection supports spheres and oblate ellipsoids.
            // Unsupported custom ellipsoids retain the v4 renderer, not a wrong projection.
            if (!math.all(math.isfinite(r)) || math.any(r <= 0.0) ||
                Math.Abs(r.x - r.y) > r.x * 1e-12 || r.z > r.x ||
                1.0 - r.z * r.z / (r.x * r.x) > 0.01) return false;
            Material m = tileset.opaqueMaterial;
            return m == null || (m.renderQueue < 3000 && IsCesiumShader(m.shader));
        }

        private static bool IsCesiumShader(Shader shader)
        {
            Material lit = Resources.Load<Material>("CesiumDefaultTilesetMaterial");
            Material unlit = Resources.Load<Material>("CesiumUnlitTilesetMaterial");
            return shader != null && shader.isSupported &&
                ((lit != null && shader == lit.shader) || (unlit != null && shader == unlit.shader));
        }

        // The native pointer is borrowed only for this synchronous call. No pointer
        // or reference to native tile/model memory is retained by managed code.
        internal static unsafe void Configure(GameObject primitive, long matrices,
            int count, double3 radii, double4 rectangle, double4 referenceRectangle, double4x4 coverageBoxToEcef, bool clipCoverage)
        {
            if (count <= 0 || matrices == 0) return;
            var renderer = primitive.AddComponent<CesiumInstancedRenderer>();
            renderer._instances = new double4x4[count];
            long bytes = checked((long)count * sizeof(double4x4));
            fixed (double4x4* destination = renderer._instances)
                Buffer.MemoryCopy((void*)matrices, destination, bytes, bytes);
            renderer.Initialize(radii, rectangle, referenceRectangle, coverageBoxToEcef, clipCoverage);
        }

        private void Initialize(double3 radii, double4 rectangle, double4 referenceRectangle, double4x4 coverageBoxToEcef, bool clipCoverage)
        {
            _mesh = GetComponent<MeshFilter>().sharedMesh;
            MeshRenderer source = GetComponent<MeshRenderer>();
            _material = source.sharedMaterial;
            _material.enableInstancing = true;
            // Keep the overlay clip path in depth and shadow passes in all targets.
            foreach (string property in new[] { "_AlphaClip", "_BUILTIN_AlphaClip", "_AlphaCutoffEnable" })
                if (_material.HasProperty(property)) _material.SetFloat(property, 1.0f);
            _canInstance = SystemInfo.supportsInstancing && IsCesiumShader(_material.shader);
            source.enabled = false;
            _anchor = GetComponent<CesiumGlobeAnchor>();
            _baseEcef = _anchor.localToGlobeFixedMatrix;
            _georeference = GetComponentInParent<CesiumGeoreference>();
            // All synthetic children use the same chart. Their shared boundary
            // is therefore evaluated with identical floating-point arithmetic.
            double referenceEast = referenceRectangle.z;
            if (referenceEast < referenceRectangle.x) referenceEast += Math.PI * 2.0;
            _longitude = (referenceRectangle.x + referenceEast) * 0.5;
            _latitude = Math.Max(-1.4660765716752369,
                Math.Min(1.4660765716752369, (referenceRectangle.y + referenceRectangle.w) * 0.5));
            double east = rectangle.z;
            if (east < rectangle.x) east += Math.PI * 2.0;
            double wrap = Math.Round((_longitude - (rectangle.x + east) * 0.5) / (Math.PI * 2.0)) * Math.PI * 2.0;
            rectangle.x += wrap;
            east += wrap;
            _chartToEcef = CesiumInstanceMath.ChartToEcef(_longitude, _latitude, radii);
            double s = Math.Sin(_latitude), c = Math.Cos(_latitude);
            double e2 = 1.0 - radii.z * radii.z / (radii.x * radii.x);
            _properties = new MaterialPropertyBlock();
            // An unattached overlay must not sample the prototype's geographic UVs.
            // Preserve material texture references: they are not owned by this renderer.
            foreach (string textureName in _material.GetTexturePropertyNames())
                if (textureName.StartsWith("_overlayTexture_", StringComparison.Ordinal))
                    _properties.SetFloat("_overlayTextureCoordinateIndex_" +
                        textureName.Substring("_overlayTexture_".Length), -1.0f);
            _properties.SetVector(GeodeticId, new Vector4((float)s, (float)c,
                (float)(radii.x / Math.Sqrt(1.0 - e2 * s * s)), (float)e2));
            _properties.SetVector(CoverageId, new Vector4((float)(rectangle.x - _longitude),
                (float)(rectangle.y - _latitude), (float)(east - _longitude),
                (float)(rectangle.w - _latitude)));
            _properties.SetFloat(ClipCoverageId, clipCoverage ? 1.0f : 0.0f);
            _properties.SetVectorArray(MappingId, _mapping);
            _properties.SetVectorArray(ProjectionId, _projection);
            _renderParams = new RenderParams(_material)
            {
                matProps = _properties,
                layer = gameObject.layer,
                shadowCastingMode = source.shadowCastingMode,
                receiveShadows = source.receiveShadows,
                renderingLayerMask = source.renderingLayerMask,
                lightProbeUsage = source.lightProbeUsage,
                reflectionProbeUsage = source.reflectionProbeUsage,
                motionVectorMode = MotionVectorGenerationMode.Camera
            };
            Cesium3DTileset tileset = GetComponentInParent<Cesium3DTileset>();
            var settings = tileset.GetComponent<CesiumInstancedRendering>();
            int maximum = settings == null ? 128 : Mathf.Clamp(settings.maximumInstancesPerBatch, 1, 128);
            double cellSize = settings == null ? 128.0 : Math.Max(1.0, settings.spatialBatchSize);
            BuildBatches(maximum, cellSize, clipCoverage, math.mul(math.inverse(_chartToEcef), coverageBoxToEcef));
            ConfigureColliders(tileset.createPhysicsMeshes &&
                (settings == null || settings.createInstanceColliders));
            _initialized = true;
            _dirty = true;
        }

        private void BuildBatches(int maximum, double cellSize, bool clipCoverage, double4x4 coverageBoxToChart)
        {
            var cells = new SortedDictionary<(long, long, long), List<int>>();
            double4x4 toChart = math.mul(math.inverse(_chartToEcef), _baseEcef);
            for (int i = 0; i < _instances.Length; ++i)
            {
                if (math.determinant(_instances[i]) == 0.0) continue;
                if (clipCoverage && !CesiumInstanceMath.IntersectsCoverage(
                    math.mul(toChart, _instances[i]), _mesh.bounds, coverageBoxToChart)) continue;
                double3 p = math.mul(toChart, _instances[i].c3).xyz;
                var key = ((long)Math.Floor(p.x / cellSize),
                    (long)Math.Floor(p.y / cellSize), (long)Math.Floor(p.z / cellSize));
                if (!cells.TryGetValue(key, out List<int> indices))
                {
                    indices = new List<int>();
                    cells.Add(key, indices);
                }
                indices.Add(i);
            }
            foreach (List<int> indices in cells.Values)
            {
                for (int start = 0; start < indices.Count; start += maximum)
                {
                    int count = Math.Min(maximum, indices.Count - start);
                    var batch = new Batch { indices = new int[count], matrices = new Matrix4x4[count] };
                    indices.CopyTo(start, batch.indices, 0, count);
                    _batches.Add(batch);
                }
            }
        }

        private void ConfigureColliders(bool enabled)
        {
            MeshCollider prototype = GetComponent<MeshCollider>();
            // No collider was created for a degenerate mesh; preserve that decision.
            if (prototype == null) return;
            prototype.enabled = false;
            if (enabled)
            {
                foreach (Batch batch in _batches)
                foreach (int i in batch.indices)
                {
                    var child = new GameObject("Instance collision " + i);
                    child.hideFlags = gameObject.hideFlags;
                    child.layer = gameObject.layer;
                    child.transform.SetParent(transform, false);
                    double4x4 m = _instances[i];
                    double3 scale = new double3(math.length(m.c0.xyz), math.length(m.c1.xyz), math.length(m.c2.xyz));
                    if (math.any(scale <= 0.0)) { UnityLifetime.Destroy(child); continue; }
                    double3x3 rotation = new double3x3(m.c0.xyz / scale.x, m.c1.xyz / scale.y, m.c2.xyz / scale.z);
                    if (math.determinant(rotation) < 0.0) { scale.x = -scale.x; rotation.c0 = -rotation.c0; }
                    quaternion q = new quaternion(new float3x3((float3)rotation.c0, (float3)rotation.c1, (float3)rotation.c2));
                    child.transform.localPosition = (Vector3)(float3)m.c3.xyz;
                    child.transform.localRotation = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
                    child.transform.localScale = (Vector3)(float3)scale;
                    var collider = child.AddComponent<MeshCollider>();
                    collider.cookingOptions = prototype.cookingOptions;
                    collider.sharedMesh = _mesh;
                }
            }
            UnityLifetime.Destroy(prototype);
        }

        internal static void AttachRaster(GameObject model, string key, Texture texture,
            double4 rectangle, bool webMercator, double radius)
        {
            foreach (var renderer in model.GetComponentsInChildren<CesiumInstancedRenderer>(true))
                renderer.SetRaster(key, texture, rectangle, webMercator, radius);
        }

        internal void SetRaster(string key, Texture texture, double4 rectangle, bool webMercator, double radius)
        {
            if (!_initialized || texture == null ||
                !_material.HasProperty("_overlayTexture_" + key)) return;
            int slot = -1;
            for (int i = 0; i < MaximumOverlays; ++i)
                if (_overlays[i] != null && _overlays[i].key == key) { slot = i; break; }
            if (slot < 0)
                for (int i = 0; i < MaximumOverlays; ++i)
                    if (_overlays[i] == null) { slot = i; break; }
            if (slot < 0)
            {
                if (!_warnedOverlayLimit)
                {
                    Debug.LogError("Instanced rendering supports at most eight raster material keys per primitive.", this);
                    _warnedOverlayLimit = true;
                }
                return;
            }
            if (_overlays[slot] == null)
                _overlays[slot] = new Overlay
                {
                    key = key,
                    textureId = Shader.PropertyToID("_overlayTexture_" + key),
                    coordinateId = Shader.PropertyToID("_overlayTextureCoordinateIndex_" + key),
                    translationScaleId = Shader.PropertyToID("_overlayTranslationAndScale_" + key)
                };
            Overlay overlay = _overlays[slot];
            overlay.texture = texture;
            _mapping[slot] = CesiumInstanceMath.RasterMapping(_longitude, _latitude, rectangle, webMercator, radius);
            // The second component bounds Mercator deltas at the projection poles.
            _projection[slot] = new Vector4(webMercator ? 1.0f : 0.0f,
                (float)CesiumInstanceMath.MercatorAngle(_latitude), 0.0f, 0.0f);
            _properties.SetTexture(overlay.textureId, texture);
            _properties.SetFloat(overlay.coordinateId, -2.0f - slot);
            _properties.SetVector(overlay.translationScaleId, new Vector4(0, 0, 1, 1));
            _properties.SetVectorArray(MappingId, _mapping);
            _properties.SetVectorArray(ProjectionId, _projection);
        }

        internal static void DetachRaster(GameObject model, string key, Texture texture)
        {
            foreach (var renderer in model.GetComponentsInChildren<CesiumInstancedRenderer>(true))
                renderer.RemoveRaster(key, texture);
        }

        internal void RemoveRaster(string key, Texture texture)
        {
            for (int i = 0; i < MaximumOverlays; ++i)
            {
                Overlay overlay = _overlays[i];
                // A late detach of an ancestor must not clear a replacement raster.
                if (overlay == null || overlay.key != key || overlay.texture != texture) continue;
                _properties.SetFloat(overlay.coordinateId, -1.0f);
                _properties.SetTexture(overlay.textureId, null);
                _overlays[i] = null;
            }
        }

        private void UpdateTransforms()
        {
            Matrix4x4 parent = transform.parent == null ? Matrix4x4.identity : transform.parent.localToWorldMatrix;
            double4x4 ecefToLocal = _georeference.ecefToLocalMatrix;
            double4x4 baseEcef = _anchor.localToGlobeFixedMatrix;
            if (!_dirty && parent == _lastParent && ecefToLocal.Equals(_lastEcefToLocal) && baseEcef.Equals(_baseEcef)) return;
            _dirty = false;
            _lastParent = parent;
            _lastEcefToLocal = ecefToLocal;
            _baseEcef = baseEcef;
            double4x4 worldFromEcef = math.mul(CesiumInstanceMath.ToDouble(parent), ecefToLocal);
            double4x4 worldFromModel = math.mul(worldFromEcef, _baseEcef);
            double4x4 worldToChart = math.inverse(math.mul(worldFromEcef, _chartToEcef));
            _properties.SetMatrix(WorldToChartId, CesiumInstanceMath.ToFloat(worldToChart));
            Bounds localBounds = _mesh.bounds;
            foreach (Batch batch in _batches)
            {
                for (int j = 0; j < batch.indices.Length; ++j)
                {
                    Matrix4x4 matrix = CesiumInstanceMath.ToFloat(math.mul(worldFromModel, _instances[batch.indices[j]]));
                    batch.matrices[j] = matrix;
                    Bounds bounds = CesiumInstanceMath.TransformBounds(matrix, localBounds);
                    if (j == 0) batch.bounds = bounds;
                    else batch.bounds.Encapsulate(bounds);
                }
            }
        }

        private void Submit(Camera camera)
        {
            if (!_initialized || !isActiveAndEnabled || camera == null || camera.cameraType == CameraType.Preview ||
                (camera.cullingMask & (1 << gameObject.layer)) == 0 || _mesh == null || _material == null) return;
            UpdateTransforms();
            RenderParams parameters = _renderParams;
            parameters.camera = camera;
            parameters.layer = gameObject.layer;
            foreach (Batch batch in _batches)
            {
                parameters.worldBounds = batch.bounds;
                if (_canInstance)
                    Graphics.RenderMeshInstanced(parameters, _mesh, 0, batch.matrices);
                else
                    for (int i = 0; i < batch.matrices.Length; ++i)
                        Graphics.RenderMesh(parameters, _mesh, 0, batch.matrices[i]);
            }
        }

        private void BeforeCull(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) Submit(camera);
        }

        private void BeforeSrpCamera(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null) Submit(camera);
        }

        private void OnEnable()
        {
            _dirty = true;
            Camera.onPreCull += BeforeCull;
            RenderPipelineManager.beginCameraRendering += BeforeSrpCamera;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= BeforeCull;
            RenderPipelineManager.beginCameraRendering -= BeforeSrpCamera;
        }

        // Only these methods need generated C++ bindings; Graphics calls stay in C#.
        private static void ExposeToCPP()
        {
            Cesium3DTileset tileset = null;
            GameObject go = null;
            Texture texture = null;
            CanUse(tileset);
            Configure(go, 0L, 0, default(double3), default(double4), default(double4), default(double4x4), false);
            AttachRaster(go, "", texture, default(double4), false, 1.0);
            DetachRaster(go, "", texture);
        }
    }
}
