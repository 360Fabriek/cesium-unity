using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace CesiumForUnity
{
    public static class MatrixUtils
    {
        public static Matrix4x4 Double4x4ToMatrix4x4(double4x4 m)
        {
            return new Matrix4x4(
                new Vector4((float)m.c0.x, (float)m.c0.y, (float)m.c0.z, (float)m.c0.w),
                new Vector4((float)m.c1.x, (float)m.c1.y, (float)m.c1.z, (float)m.c1.w),
                new Vector4((float)m.c2.x, (float)m.c2.y, (float)m.c2.z, (float)m.c2.w),
                new Vector4((float)m.c3.x, (float)m.c3.y, (float)m.c3.z, (float)m.c3.w)
            );
        }

        public static Matrix4x4[] Double4x4ArrayToMatrix4x4Array(double4x4[] arr)
        {
            if (arr == null)
            {
                return Array.Empty<Matrix4x4>();
            }

            Matrix4x4[] result = new Matrix4x4[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                result[i] = Double4x4ToMatrix4x4(arr[i]);
            }

            return result;
        }
    }

    [ExecuteInEditMode]
    public class I3dmInstanceRenderer : MonoBehaviour
    {
        internal struct LongitudeInterval
        {
            public double min;
            public double max;
            public bool wraps;

            public bool Contains(double longitude)
            {
                double normalizedLongitude = NormalizeLongitude360(longitude);
                if (!this.wraps)
                {
                    return normalizedLongitude >= this.min && normalizedLongitude <= this.max;
                }

                return normalizedLongitude >= this.min || normalizedLongitude <= this.max;
            }
        }

        private struct CachedPolygon
        {
            public List<double2> points;
            public LongitudeInterval longitudeInterval;
            public double minLatitude;
            public double maxLatitude;
        }

        private const int UnityMaxBatchSize = 1023;
        private static readonly int ClippingOverlayTexturePropertyId =
            Shader.PropertyToID("_overlayTexture_Clipping");

        private readonly Dictionary<string, InstanceGroupData> _instanceGroups =
            new Dictionary<string, InstanceGroupData>();

        private bool _checkedInstancingSupport;
        private Cesium3DTileset _cachedTileset;
        private CesiumGeoreference _cachedGeoreference;
        private CesiumGeoreference _subscribedGeoreference;
        private bool _polygonClipActive;
        private Matrix4x4 _worldToTileset;
        private bool _clipCacheDirty = true;
        private bool _georeferenceChanged = true;
        private int _clipSourceFingerprint;
        private readonly List<CachedPolygon> _cachedPolygons = new List<CachedPolygon>();
        private LongitudeInterval _unionLongitudeInterval;
        private double _unionMinLatitude;
        private double _unionMaxLatitude;
        private bool _hasBounds;

        [Serializable]
        public class InstanceGroupData
        {
            public const int DefaultMaxBatchSize = 1023;

            public Mesh mesh;
            public Material material;
            public int primitiveIndex = -1;
            public Transform[] instanceTransforms = Array.Empty<Transform>();
            public Matrix4x4[] initialMatrices = Array.Empty<Matrix4x4>();
            public Matrix4x4[] batchBuffer;
            public MaterialPropertyBlock propertyBlock;
            public int maxInstancesPerBatch = DefaultMaxBatchSize;
            public bool usesClippingOverlay;

            public int InstanceCount => this.instanceTransforms != null ? this.instanceTransforms.Length : 0;

            public bool IsReady =>
                this.mesh != null &&
                this.material != null &&
                this.batchBuffer != null &&
                this.batchBuffer.Length > 0 &&
                (this.InstanceCount > 0 || (this.initialMatrices != null && this.initialMatrices.Length > 0));
        }

        private void Awake()
        {
            EnsureInstancingSupportLogged();
        }

        private void OnEnable()
        {
            EnsureInstancingSupportLogged();
            MarkClipCacheDirty();
        }

        private void OnDisable()
        {
            SubscribeToGeoreferenceChanged(null);
        }

        private void Update()
        {
            if (_instanceGroups.Count == 0)
            {
                return;
            }

            RefreshPolygonClippingContextIfNeeded();

            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                RenderInstanceGroup(groupData);
            }
        }

        private void RenderInstanceGroup(InstanceGroupData groupData)
        {
            if (groupData == null || !groupData.IsReady)
            {
                return;
            }

            EnsureMaterialInstancing(groupData.material);

            Transform[] instanceTransforms = groupData.instanceTransforms ?? Array.Empty<Transform>();
            Matrix4x4[] fallbackMatrices = groupData.initialMatrices ?? Array.Empty<Matrix4x4>();
            int totalInstances = Math.Max(instanceTransforms.Length, fallbackMatrices.Length);
            if (totalInstances == 0)
            {
                return;
            }

            int maxBatchSize = Mathf.Clamp(groupData.maxInstancesPerBatch, 1, UnityMaxBatchSize);
            Matrix4x4[] batchBuffer = groupData.batchBuffer;
            if (batchBuffer == null || batchBuffer.Length != maxBatchSize)
            {
                batchBuffer = new Matrix4x4[maxBatchSize];
                groupData.batchBuffer = batchBuffer;
            }

            bool clipByPolygon = _polygonClipActive && groupData.usesClippingOverlay;
            MaterialPropertyBlock propertyBlock = EnsurePropertyBlock(groupData);
            int batchSize = 0;
            for (int instanceIndex = 0; instanceIndex < totalInstances; ++instanceIndex)
            {
                Matrix4x4 matrix = Matrix4x4.identity;
                bool hasValidInstance = false;

                if (instanceIndex < instanceTransforms.Length && instanceTransforms[instanceIndex] != null)
                {
                    matrix = instanceTransforms[instanceIndex].localToWorldMatrix;
                    hasValidInstance = true;
                }
                else if (instanceIndex < fallbackMatrices.Length)
                {
                    matrix = fallbackMatrices[instanceIndex];
                    hasValidInstance = true;
                }

                if (!hasValidInstance)
                {
                    continue;
                }

                if (clipByPolygon && this._hasBounds && !MightIntersectClippingBounds(matrix))
                {
                    continue;
                }

                batchBuffer[batchSize++] = matrix;
                if (batchSize < maxBatchSize)
                {
                    continue;
                }

                Graphics.DrawMeshInstanced(
                    groupData.mesh,
                    0,
                    groupData.material,
                    batchBuffer,
                    batchSize,
                    propertyBlock);
                batchSize = 0;
            }

            if (batchSize > 0)
            {
                Graphics.DrawMeshInstanced(
                    groupData.mesh,
                    0,
                    groupData.material,
                    batchBuffer,
                    batchSize,
                    propertyBlock);
            }
        }

        public void AddInstanceGroup(
            string groupId,
            Mesh mesh,
            Material material,
            List<double4x4> matrices,
            int primitiveIndex = -1)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return;
            }

            EnsureInstancingSupportLogged();

            var groupData = new InstanceGroupData
            {
                mesh = mesh,
                material = material,
                primitiveIndex = primitiveIndex
            };

            if (_instanceGroups.TryGetValue(groupId, out InstanceGroupData existingGroupData))
            {
                groupData.propertyBlock = existingGroupData.propertyBlock;
            }
            else
            {
                groupData.propertyBlock = new MaterialPropertyBlock();
            }

            int instanceCount = matrices != null ? matrices.Count : 0;

            if (instanceCount > 0)
            {
                groupData.initialMatrices =
                    MatrixUtils.Double4x4ArrayToMatrix4x4Array(matrices.ToArray());
                groupData.instanceTransforms = CaptureInstanceTransforms(instanceCount);
            }
            else
            {
                groupData.initialMatrices = Array.Empty<Matrix4x4>();
                groupData.instanceTransforms = Array.Empty<Transform>();
            }

            int effectiveBatchSize = Mathf.Clamp(groupData.maxInstancesPerBatch, 1, UnityMaxBatchSize);
            groupData.batchBuffer = new Matrix4x4[effectiveBatchSize];

            EnsureMaterialInstancing(groupData.material);

            _instanceGroups[groupId] = groupData;
        }

        public void SetInstanceGroupFloat(string groupId, int propertyId, float value)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return;
            }

            if (_instanceGroups.TryGetValue(groupId, out InstanceGroupData groupData))
            {
                EnsurePropertyBlock(groupData).SetFloat(propertyId, value);
            }
        }

        public void SetInstanceGroupFloat(string groupId, string propertyName, float value)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            SetInstanceGroupFloat(groupId, Shader.PropertyToID(propertyName), value);
        }

        public void SetFloatForAllInstanceGroups(int propertyId, float value, int primitiveIndex = -1)
        {
            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                if (groupData == null)
                {
                    continue;
                }

                if (primitiveIndex >= 0 && groupData.primitiveIndex != primitiveIndex)
                {
                    continue;
                }

                EnsurePropertyBlock(groupData).SetFloat(propertyId, value);
            }
        }

        public void SetFloatForAllInstanceGroups(string propertyName, float value, int primitiveIndex = -1)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            SetFloatForAllInstanceGroups(Shader.PropertyToID(propertyName), value, primitiveIndex);
        }

        public void SetVectorForAllInstanceGroups(int propertyId, Vector4 value, int primitiveIndex = -1)
        {
            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                if (groupData == null)
                {
                    continue;
                }

                if (primitiveIndex >= 0 && groupData.primitiveIndex != primitiveIndex)
                {
                    continue;
                }

                EnsurePropertyBlock(groupData).SetVector(propertyId, value);
            }
        }

        public void SetVectorForAllInstanceGroups(string propertyName, Vector4 value, int primitiveIndex = -1)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            SetVectorForAllInstanceGroups(Shader.PropertyToID(propertyName), value, primitiveIndex);
        }

        public void SetTextureForAllInstanceGroups(int propertyId, Texture value, int primitiveIndex = -1)
        {
            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                if (groupData == null)
                {
                    continue;
                }

                if (primitiveIndex >= 0 && groupData.primitiveIndex != primitiveIndex)
                {
                    continue;
                }

                EnsurePropertyBlock(groupData).SetTexture(propertyId, value);
            }
        }

        public void SetTextureForAllInstanceGroups(string propertyName, Texture value, int primitiveIndex = -1)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            SetTextureForAllInstanceGroups(Shader.PropertyToID(propertyName), value, primitiveIndex);
        }

        public void SetRasterOverlayForPrimitive(
            int primitiveIndex,
            int overlayTextureCoordinateIndexPropertyID,
            float textureCoordinateIndex,
            int overlayTexturePropertyID,
            Texture overlayTexture,
            int overlayTranslationScalePropertyID,
            Vector4 overlayTranslationScale)
        {
            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                if (groupData == null || groupData.material == null || groupData.primitiveIndex != primitiveIndex)
                {
                    continue;
                }

                if (overlayTextureCoordinateIndexPropertyID >= 0)
                {
                    groupData.material.SetFloat(
                        overlayTextureCoordinateIndexPropertyID,
                        textureCoordinateIndex);
                }

                if (overlayTexturePropertyID >= 0)
                {
                    groupData.material.SetTexture(overlayTexturePropertyID, overlayTexture);
                    if (overlayTexturePropertyID == ClippingOverlayTexturePropertyId)
                    {
                        groupData.usesClippingOverlay = overlayTexture != null;
                        MarkClipCacheDirty();
                    }
                }

                if (overlayTranslationScalePropertyID >= 0)
                {
                    groupData.material.SetVector(
                        overlayTranslationScalePropertyID,
                        overlayTranslationScale);
                }
            }
        }

        public void ClearRasterOverlayTextureForPrimitive(
            int primitiveIndex,
            int overlayTexturePropertyID)
        {
            if (overlayTexturePropertyID < 0)
            {
                return;
            }

            foreach (InstanceGroupData groupData in _instanceGroups.Values)
            {
                if (groupData == null || groupData.material == null || groupData.primitiveIndex != primitiveIndex)
                {
                    continue;
                }

                groupData.material.SetTexture(overlayTexturePropertyID, null);
                if (overlayTexturePropertyID == ClippingOverlayTexturePropertyId)
                {
                    groupData.usesClippingOverlay = false;
                    MarkClipCacheDirty();
                }
            }
        }

        public void RemoveInstanceGroup(string groupId)
        {
            _instanceGroups.Remove(groupId);
        }

        private Transform[] CaptureInstanceTransforms(int expectedInstanceCount)
        {
            if (expectedInstanceCount <= 0)
            {
                return Array.Empty<Transform>();
            }

            Transform parentTransform = transform;
            int childCount = parentTransform.childCount;
            if (childCount <= 0)
            {
                return Array.Empty<Transform>();
            }

            if (childCount < expectedInstanceCount && Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"I3dmInstanceRenderer expected {expectedInstanceCount} instance transforms but only found {childCount}.",
                    this);
            }

            expectedInstanceCount = Mathf.Min(expectedInstanceCount, childCount);
            int startIndex = childCount - expectedInstanceCount;
            Transform[] result = new Transform[expectedInstanceCount];

            for (int i = 0; i < expectedInstanceCount; ++i)
            {
                result[i] = parentTransform.GetChild(startIndex + i);
            }

            return result;
        }

        private void EnsureInstancingSupportLogged()
        {
            if (_checkedInstancingSupport)
            {
                return;
            }

            if (!SystemInfo.supportsInstancing)
            {
                Debug.LogWarning(
                    "GPU instancing is not supported on this platform. i3dm instances will not render via instancing.",
                    this);
            }

            _checkedInstancingSupport = true;
        }

        private static void EnsureMaterialInstancing(Material material)
        {
            if (material != null && SystemInfo.supportsInstancing && !material.enableInstancing)
            {
                material.enableInstancing = true;
            }
        }

        private static MaterialPropertyBlock EnsurePropertyBlock(InstanceGroupData groupData)
        {
            if (groupData.propertyBlock == null)
            {
                groupData.propertyBlock = new MaterialPropertyBlock();
            }

            return groupData.propertyBlock;
        }

        private void MarkClipCacheDirty()
        {
            this._clipCacheDirty = true;
        }

        private void HandleGeoreferenceChanged()
        {
            this._georeferenceChanged = true;
            MarkClipCacheDirty();
        }

        private void SubscribeToGeoreferenceChanged(CesiumGeoreference georeference)
        {
            if (ReferenceEquals(this._subscribedGeoreference, georeference))
            {
                return;
            }

            if (this._subscribedGeoreference != null)
            {
                this._subscribedGeoreference.changed -= this.HandleGeoreferenceChanged;
            }

            this._subscribedGeoreference = georeference;
            if (this._subscribedGeoreference != null)
            {
                this._subscribedGeoreference.changed += this.HandleGeoreferenceChanged;
            }
        }

        private static bool MatricesApproximatelyEqual(Matrix4x4 a, Matrix4x4 b, float epsilon = 1e-6f)
        {
            for (int row = 0; row < 4; ++row)
            {
                for (int column = 0; column < 4; ++column)
                {
                    if (Mathf.Abs(a[row, column] - b[row, column]) > epsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static int ComputeClippingSourceFingerprint(CesiumPolygonRasterOverlay[] overlays)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + overlays.Length;

                for (int i = 0; i < overlays.Length; ++i)
                {
                    CesiumPolygonRasterOverlay overlay = overlays[i];
                    if (overlay == null)
                    {
                        hash = hash * 31;
                        continue;
                    }

                    bool isEligible =
                        overlay.isActiveAndEnabled &&
                        overlay.invertSelection &&
                        overlay.excludeSelectedTiles &&
                        string.Equals(overlay.materialKey, "Clipping", StringComparison.Ordinal);

                    hash = hash * 31 + overlay.GetInstanceID();
                    hash = hash * 31 + (isEligible ? 1 : 0);

                    List<CesiumCartographicPolygon> polygons = overlay.polygons;
                    int polygonCount = polygons != null ? polygons.Count : 0;
                    hash = hash * 31 + polygonCount;

                    for (int polygonIndex = 0; polygonIndex < polygonCount; ++polygonIndex)
                    {
                        CesiumCartographicPolygon polygon = polygons[polygonIndex];
                        if (polygon == null)
                        {
                            hash = hash * 31;
                            continue;
                        }

                        hash = hash * 31 + polygon.GetInstanceID();
                        hash = hash * 31 + (polygon.isActiveAndEnabled ? 1 : 0);
                    }
                }

                return hash;
            }
        }

        private void RefreshPolygonClippingContextIfNeeded()
        {
            Cesium3DTileset tileset = this._cachedTileset != null
                ? this._cachedTileset
                : this.GetComponentInParent<Cesium3DTileset>();
            if (!ReferenceEquals(this._cachedTileset, tileset))
            {
                this._cachedTileset = tileset;
                MarkClipCacheDirty();
            }

            if (tileset == null)
            {
                this._cachedGeoreference = null;
                SubscribeToGeoreferenceChanged(null);
                this._cachedPolygons.Clear();
                this._polygonClipActive = false;
                this._hasBounds = false;
                this._clipSourceFingerprint = 0;
                return;
            }

            Matrix4x4 worldToTileset = tileset.transform.worldToLocalMatrix;
            if (!MatricesApproximatelyEqual(this._worldToTileset, worldToTileset))
            {
                this._worldToTileset = worldToTileset;
                MarkClipCacheDirty();
            }

            CesiumGeoreference georeference = this._cachedGeoreference != null
                ? this._cachedGeoreference
                : tileset.GetComponentInParent<CesiumGeoreference>();
            if (!ReferenceEquals(this._cachedGeoreference, georeference))
            {
                this._cachedGeoreference = georeference;
                this._georeferenceChanged = true;
                MarkClipCacheDirty();
            }
            SubscribeToGeoreferenceChanged(this._cachedGeoreference);

            if (this._cachedGeoreference == null)
            {
                this._cachedPolygons.Clear();
                this._polygonClipActive = false;
                this._hasBounds = false;
                return;
            }

            CesiumPolygonRasterOverlay[] overlays = tileset.GetComponents<CesiumPolygonRasterOverlay>();
            int overlayFingerprint = ComputeClippingSourceFingerprint(overlays);
            bool shouldRebuild =
                this._clipCacheDirty ||
                this._georeferenceChanged ||
                this._clipSourceFingerprint != overlayFingerprint;

            if (!shouldRebuild)
            {
                return;
            }

            RebuildClippingCache(overlays, overlayFingerprint);
        }

        private void RebuildClippingCache(
            CesiumPolygonRasterOverlay[] overlays,
            int overlayFingerprint)
        {
            this._cachedPolygons.Clear();
            this._hasBounds = false;
            this._unionMinLatitude = double.PositiveInfinity;
            this._unionMaxLatitude = double.NegativeInfinity;

            List<double> unionLongitudes = new List<double>();

            for (int i = 0; i < overlays.Length; ++i)
            {
                CesiumPolygonRasterOverlay overlay = overlays[i];
                if (overlay == null || !overlay.isActiveAndEnabled)
                {
                    continue;
                }

                if (!overlay.invertSelection || !overlay.excludeSelectedTiles)
                {
                    continue;
                }

                if (!string.Equals(overlay.materialKey, "Clipping", StringComparison.Ordinal))
                {
                    continue;
                }

                List<CesiumCartographicPolygon> polygons = overlay.polygons;
                if (polygons == null || polygons.Count == 0)
                {
                    continue;
                }

                for (int polygonIndex = 0; polygonIndex < polygons.Count; ++polygonIndex)
                {
                    CesiumCartographicPolygon polygon = polygons[polygonIndex];
                    if (polygon == null || !polygon.isActiveAndEnabled)
                    {
                        continue;
                    }

                    List<double2> cartographicPoints =
                        polygon.GetCartographicPoints(this._worldToTileset);
                    if (cartographicPoints == null || cartographicPoints.Count < 3)
                    {
                        continue;
                    }

                    if (!TryComputePolygonBounds(
                        cartographicPoints,
                        out LongitudeInterval longitudeInterval,
                        out double minLatitude,
                        out double maxLatitude))
                    {
                        continue;
                    }

                    for (int pointIndex = 0; pointIndex < cartographicPoints.Count; ++pointIndex)
                    {
                        unionLongitudes.Add(cartographicPoints[pointIndex].x);
                    }

                    this._unionMinLatitude = Math.Min(this._unionMinLatitude, minLatitude);
                    this._unionMaxLatitude = Math.Max(this._unionMaxLatitude, maxLatitude);

                    this._cachedPolygons.Add(new CachedPolygon
                    {
                        points = cartographicPoints,
                        longitudeInterval = longitudeInterval,
                        minLatitude = minLatitude,
                        maxLatitude = maxLatitude
                    });
                }
            }

            this._polygonClipActive = this._cachedPolygons.Count > 0;
            if (this._polygonClipActive &&
                unionLongitudes.Count > 0 &&
                TryCreateLongitudeInterval(unionLongitudes, out LongitudeInterval unionLongitudeInterval))
            {
                this._unionLongitudeInterval = unionLongitudeInterval;
                this._hasBounds = true;
            }

            this._clipSourceFingerprint = overlayFingerprint;
            this._clipCacheDirty = false;
            this._georeferenceChanged = false;
        }

        private bool TryGetInstanceLongitudeLatitude(Matrix4x4 worldMatrix, out double2 longitudeLatitude)
        {
            longitudeLatitude = default;
            if (this._cachedGeoreference == null)
            {
                return false;
            }

            Vector3 worldPosition = worldMatrix.GetColumn(3);
            Vector3 tilesetSpace = this._worldToTileset.MultiplyPoint3x4(worldPosition);
            double3 ecef =
                this._cachedGeoreference.TransformUnityPositionToEarthCenteredEarthFixed(
                    new double3(tilesetSpace.x, tilesetSpace.y, tilesetSpace.z));
            longitudeLatitude =
                this._cachedGeoreference.ellipsoid
                    .CenteredFixedToLongitudeLatitudeHeight(ecef).xy;
            return true;
        }

        private bool MightIntersectClippingBounds(Matrix4x4 worldMatrix)
        {
            if (!this._hasBounds)
            {
                return true;
            }

            if (!TryGetInstanceLongitudeLatitude(worldMatrix, out double2 longitudeLatitude))
            {
                return true;
            }

            if (longitudeLatitude.y < this._unionMinLatitude ||
                longitudeLatitude.y > this._unionMaxLatitude)
            {
                return false;
            }

            if (!this._unionLongitudeInterval.Contains(longitudeLatitude.x))
            {
                return false;
            }

            for (int i = 0; i < this._cachedPolygons.Count; ++i)
            {
                CachedPolygon polygon = this._cachedPolygons[i];
                if (IsPointWithinBounds(
                    longitudeLatitude.x,
                    longitudeLatitude.y,
                    polygon.longitudeInterval,
                    polygon.minLatitude,
                    polygon.maxLatitude))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryComputePolygonBounds(
            List<double2> cartographicPoints,
            out LongitudeInterval longitudeInterval,
            out double minLatitude,
            out double maxLatitude)
        {
            longitudeInterval = default;
            minLatitude = double.PositiveInfinity;
            maxLatitude = double.NegativeInfinity;

            if (cartographicPoints == null || cartographicPoints.Count < 3)
            {
                return false;
            }

            List<double> polygonLongitudes = new List<double>(cartographicPoints.Count);
            for (int i = 0; i < cartographicPoints.Count; ++i)
            {
                polygonLongitudes.Add(cartographicPoints[i].x);
                double latitude = cartographicPoints[i].y;
                minLatitude = Math.Min(minLatitude, latitude);
                maxLatitude = Math.Max(maxLatitude, latitude);
            }

            return TryCreateLongitudeInterval(polygonLongitudes, out longitudeInterval);
        }

        private static double NormalizeLongitudeDegrees(double longitude)
        {
            double normalized = longitude % 360.0;
            if (normalized < -180.0)
            {
                normalized += 360.0;
            }
            else if (normalized >= 180.0)
            {
                normalized -= 360.0;
            }

            return normalized;
        }

        private static double NormalizeLongitude360(double longitude)
        {
            double normalized = NormalizeLongitudeDegrees(longitude);
            if (normalized < 0.0)
            {
                normalized += 360.0;
            }

            return normalized;
        }

        internal static bool IsPointWithinBounds(
            double longitude,
            double latitude,
            LongitudeInterval longitudeInterval,
            double minLatitude,
            double maxLatitude)
        {
            if (latitude < minLatitude || latitude > maxLatitude)
            {
                return false;
            }

            return longitudeInterval.Contains(longitude);
        }

        internal static bool TryCreateLongitudeInterval(
            IReadOnlyList<double> longitudes,
            out LongitudeInterval interval)
        {
            interval = default;
            if (longitudes == null || longitudes.Count == 0)
            {
                return false;
            }

            List<double> normalizedLongitudes = new List<double>(longitudes.Count);
            for (int i = 0; i < longitudes.Count; ++i)
            {
                double normalized = NormalizeLongitude360(longitudes[i]);
                normalizedLongitudes.Add(normalized);
            }

            normalizedLongitudes.Sort();
            if (normalizedLongitudes.Count == 1)
            {
                interval = new LongitudeInterval
                {
                    min = normalizedLongitudes[0],
                    max = normalizedLongitudes[0],
                    wraps = false
                };
                return true;
            }

            double maxGap = double.NegativeInfinity;
            int maxGapIndex = 0;

            for (int i = 0; i < normalizedLongitudes.Count; ++i)
            {
                int nextIndex = (i + 1) % normalizedLongitudes.Count;
                double current = normalizedLongitudes[i];
                double next = normalizedLongitudes[nextIndex];
                if (nextIndex == 0)
                {
                    next += 360.0;
                }

                double gap = next - current;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    maxGapIndex = i;
                }
            }

            int startIndex = (maxGapIndex + 1) % normalizedLongitudes.Count;
            double start = normalizedLongitudes[startIndex];
            double end = normalizedLongitudes[maxGapIndex];
            bool wraps = start > end;

            interval = new LongitudeInterval
            {
                min = start,
                max = end,
                wraps = wraps
            };

            return true;
        }
    }
}
