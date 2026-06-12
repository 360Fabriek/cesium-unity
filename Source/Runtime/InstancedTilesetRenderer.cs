using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace CesiumForUnity
{
    public static class MatrixUtils
    {
        public static double4x4 Matrix4x4ToDouble4x4(Matrix4x4 m)
        {
            return new double4x4(
                new double4(m.m00, m.m10, m.m20, m.m30),
                new double4(m.m01, m.m11, m.m21, m.m31),
                new double4(m.m02, m.m12, m.m22, m.m32),
                new double4(m.m03, m.m13, m.m23, m.m33));
        }

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
    public class InstancedTilesetRenderer : MonoBehaviour
    {
        public enum RaycastColliderMode
        {
            Disabled,
            BoxPerInstance
        }

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
        private const int DefaultMaxRaycastColliders = 2048;
        private static readonly int ClippingOverlayTexturePropertyId =
            Shader.PropertyToID("_overlayTexture_Clipping");

        private readonly Dictionary<string, InstanceGroupData> _instanceGroups =
            new Dictionary<string, InstanceGroupData>();
        private readonly Dictionary<string, List<GameObject>> _raycastColliderObjects =
            new Dictionary<string, List<GameObject>>();

        private bool _checkedInstancingSupport;
        private Cesium3DTileset _cachedTileset;
        private CesiumGeoreference _cachedGeoreference;
        private CesiumGeoreference _subscribedGeoreference;
        private bool _polygonClipActive;
        private Matrix4x4 _worldToTileset;
        private bool _clipCacheDirty = true;
        private bool _georeferenceChanged = true;
        private int _instanceMatrixCacheVersion;
        private int _clipSourceFingerprint;
        private readonly List<CachedPolygon> _cachedPolygons = new List<CachedPolygon>();
        private LongitudeInterval _unionLongitudeInterval;
        private double _unionMinLatitude;
        private double _unionMaxLatitude;
        private bool _hasBounds;

        [SerializeField]
        private RaycastColliderMode _raycastColliderMode = RaycastColliderMode.Disabled;

        [SerializeField]
        private int _maxRaycastColliders = DefaultMaxRaycastColliders;

        [SerializeField]
        private bool _disableChildRenderers = true;

        [Serializable]
        public class InstanceGroupData
        {
            public const int DefaultMaxBatchSize = 1023;

            public Mesh mesh;
            public Material material;
            public int primitiveIndex = -1;
            public Transform[] instanceTransforms = Array.Empty<Transform>();
            public double4x4[] localToGlobeFixedMatrices = Array.Empty<double4x4>();
            public Matrix4x4[] initialMatrices = Array.Empty<Matrix4x4>();
            public Matrix4x4[] batchBuffer;
            public MaterialPropertyBlock propertyBlock;
            public int maxInstancesPerBatch = DefaultMaxBatchSize;
            public bool usesClippingOverlay;
            public Matrix4x4[] cachedWorldMatrices = Array.Empty<Matrix4x4>();
            public bool[] cachedMatrixValid = Array.Empty<bool>();
            public double2[] cachedLongitudeLatitudes = Array.Empty<double2>();
            public bool[] cachedLongitudeLatitudeValid = Array.Empty<bool>();
            public int cachedVersion = -1;
            public int renderedFrame = -1;

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
            ApplyTilesetSettings();
            MarkClipCacheDirty();
            RebuildRaycastColliders();
        }

        private void OnDisable()
        {
            SubscribeToGeoreferenceChanged(null);
            DestroyRaycastColliders();
        }

        private void OnDestroy()
        {
            DestroyRaycastColliders();
        }

        private void OnValidate()
        {
            this._maxRaycastColliders = Math.Max(0, this._maxRaycastColliders);
            foreach (InstanceGroupData groupData in this._instanceGroups.Values)
            {
                if (groupData != null)
                {
                    groupData.maxInstancesPerBatch =
                        Mathf.Clamp(groupData.maxInstancesPerBatch, 1, UnityMaxBatchSize);
                }
            }

            if (!this.isActiveAndEnabled)
            {
                return;
            }

            RebuildRaycastColliders();
        }

        private void Update()
        {
            if (_instanceGroups.Count == 0)
            {
                return;
            }

            RefreshPolygonClippingContextIfNeeded();

            foreach (KeyValuePair<string, InstanceGroupData> pair in _instanceGroups)
            {
                RenderInstanceGroup(pair.Value);
                UpdateRaycastColliderStates(pair.Key, pair.Value);
            }
        }

        private void RenderInstanceGroup(InstanceGroupData groupData)
        {
            if (groupData == null || !groupData.IsReady)
            {
                return;
            }

            if (groupData.renderedFrame == Time.frameCount)
            {
                return;
            }
            groupData.renderedFrame = Time.frameCount;

            EnsureMaterialInstancing(groupData.material);

            Transform[] instanceTransforms = groupData.instanceTransforms ?? Array.Empty<Transform>();
            Matrix4x4[] fallbackMatrices = groupData.initialMatrices ?? Array.Empty<Matrix4x4>();
            int totalInstances = Math.Max(instanceTransforms.Length, fallbackMatrices.Length);
            if (totalInstances == 0)
            {
                return;
            }

            RefreshGroupWorldCache(groupData, totalInstances);

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
                if (!TryGetCachedInstanceMatrix(groupData, instanceIndex, out Transform sourceTransform, out Matrix4x4 matrix) ||
                    !IsInstanceVisible(groupData, sourceTransform, matrix, clipByPolygon, instanceIndex))
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
            ApplyTilesetSettings();

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
                groupData.localToGlobeFixedMatrices = matrices.ToArray();
                groupData.initialMatrices =
                    MatrixUtils.Double4x4ArrayToMatrix4x4Array(groupData.localToGlobeFixedMatrices);
                groupData.instanceTransforms = CaptureInstanceTransforms(instanceCount);
            }
            else
            {
                groupData.localToGlobeFixedMatrices = Array.Empty<double4x4>();
                groupData.initialMatrices = Array.Empty<Matrix4x4>();
                groupData.instanceTransforms = Array.Empty<Transform>();
            }

            int effectiveBatchSize = Mathf.Clamp(groupData.maxInstancesPerBatch, 1, UnityMaxBatchSize);
            groupData.batchBuffer = new Matrix4x4[effectiveBatchSize];

            EnsureMaterialInstancing(groupData.material);
            groupData.maxInstancesPerBatch =
                Mathf.Clamp(groupData.maxInstancesPerBatch, 1, UnityMaxBatchSize);

            RefreshPolygonClippingContextIfNeeded();
            RefreshGroupWorldCache(groupData, Math.Max(groupData.instanceTransforms.Length, groupData.initialMatrices.Length));

            _instanceGroups[groupId] = groupData;
            if (this._disableChildRenderers)
            {
                DisableCapturedChildRenderers(groupData.instanceTransforms);
            }

            RebuildRaycastColliders(groupId, groupData);
        }

        internal void ApplyTilesetSettings()
        {
            Cesium3DTileset tileset = this.GetComponentInParent<Cesium3DTileset>();
            if (tileset == null)
            {
                return;
            }

            this._raycastColliderMode = tileset.instancedTileColliderMode;
        }

        public void ClearTileSelectionBounds()
        {
        }

        public void AddTileSelectionBounds(
            double west,
            double south,
            double east,
            double north,
            bool wrapsLongitude)
        {
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
            DestroyRaycastColliders(groupId);
            _instanceGroups.Remove(groupId);
        }

        public RaycastColliderMode colliderMode
        {
            get => this._raycastColliderMode;
            set
            {
                if (this._raycastColliderMode == value)
                {
                    return;
                }

                this._raycastColliderMode = value;
                RebuildRaycastColliders();
            }
        }

        public int maxRaycastColliders
        {
            get => this._maxRaycastColliders;
            set
            {
                int clamped = Math.Max(0, value);
                if (this._maxRaycastColliders == clamped)
                {
                    return;
                }

                this._maxRaycastColliders = clamped;
                RebuildRaycastColliders();
            }
        }

        public bool disableChildRenderers
        {
            get => this._disableChildRenderers;
            set
            {
                this._disableChildRenderers = value;
                if (value)
                {
                    foreach (InstanceGroupData groupData in this._instanceGroups.Values)
                    {
                        if (groupData != null)
                        {
                            DisableCapturedChildRenderers(groupData.instanceTransforms);
                        }
                    }
                }
            }
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
                    $"InstancedTilesetRenderer expected {expectedInstanceCount} instance transforms but only found {childCount}.",
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

        private static void DisableCapturedChildRenderers(Transform[] instanceTransforms)
        {
            if (instanceTransforms == null)
            {
                return;
            }

            for (int i = 0; i < instanceTransforms.Length; ++i)
            {
                Transform instanceTransform = instanceTransforms[i];
                if (instanceTransform == null)
                {
                    continue;
                }

                Renderer[] renderers = instanceTransform.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; ++rendererIndex)
                {
                    renderers[rendererIndex].enabled = false;
                }
            }
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

        private bool TryGetInstanceMatrix(
            InstanceGroupData groupData,
            int instanceIndex,
            out Transform sourceTransform,
            out Matrix4x4 matrix)
        {
            sourceTransform = null;
            matrix = Matrix4x4.identity;
            if (groupData == null || instanceIndex < 0)
            {
                return false;
            }

            Transform[] instanceTransforms = groupData.instanceTransforms ?? Array.Empty<Transform>();
            double4x4[] globeFixedMatrices =
                groupData.localToGlobeFixedMatrices ?? Array.Empty<double4x4>();
            Matrix4x4[] fallbackMatrices = groupData.initialMatrices ?? Array.Empty<Matrix4x4>();

            if (instanceIndex < instanceTransforms.Length)
            {
                sourceTransform = instanceTransforms[instanceIndex];
            }

            if (instanceIndex < globeFixedMatrices.Length &&
                TryTransformGlobeFixedMatrixToUnityWorld(globeFixedMatrices[instanceIndex], out matrix))
            {
                return true;
            }

            if (instanceIndex < instanceTransforms.Length && instanceTransforms[instanceIndex] != null)
            {
                matrix = sourceTransform.localToWorldMatrix;
                return true;
            }

            if (instanceIndex < fallbackMatrices.Length)
            {
                matrix = fallbackMatrices[instanceIndex];
                return true;
            }

            return false;
        }

        private bool TryGetCachedInstanceMatrix(
            InstanceGroupData groupData,
            int instanceIndex,
            out Transform sourceTransform,
            out Matrix4x4 matrix)
        {
            sourceTransform = null;
            matrix = Matrix4x4.identity;
            if (groupData == null || instanceIndex < 0)
            {
                return false;
            }

            Transform[] instanceTransforms = groupData.instanceTransforms ?? Array.Empty<Transform>();
            if (instanceIndex < instanceTransforms.Length)
            {
                sourceTransform = instanceTransforms[instanceIndex];
            }

            if (groupData.cachedMatrixValid != null &&
                instanceIndex < groupData.cachedMatrixValid.Length &&
                groupData.cachedMatrixValid[instanceIndex] &&
                groupData.cachedWorldMatrices != null &&
                instanceIndex < groupData.cachedWorldMatrices.Length)
            {
                matrix = groupData.cachedWorldMatrices[instanceIndex];
                return true;
            }

            return TryGetInstanceMatrix(groupData, instanceIndex, out sourceTransform, out matrix);
        }

        private void RefreshGroupWorldCache(InstanceGroupData groupData, int totalInstances)
        {
            if (groupData == null)
            {
                return;
            }

            if (totalInstances <= 0)
            {
                groupData.cachedWorldMatrices = Array.Empty<Matrix4x4>();
                groupData.cachedMatrixValid = Array.Empty<bool>();
                groupData.cachedLongitudeLatitudes = Array.Empty<double2>();
                groupData.cachedLongitudeLatitudeValid = Array.Empty<bool>();
                groupData.cachedVersion = this._instanceMatrixCacheVersion;
                return;
            }

            if (groupData.cachedVersion == this._instanceMatrixCacheVersion &&
                groupData.cachedWorldMatrices != null &&
                groupData.cachedWorldMatrices.Length == totalInstances)
            {
                return;
            }

            if (groupData.cachedWorldMatrices == null ||
                groupData.cachedWorldMatrices.Length != totalInstances)
            {
                groupData.cachedWorldMatrices = new Matrix4x4[totalInstances];
                groupData.cachedMatrixValid = new bool[totalInstances];
                groupData.cachedLongitudeLatitudes = new double2[totalInstances];
                groupData.cachedLongitudeLatitudeValid = new bool[totalInstances];
            }

            groupData.cachedVersion = this._instanceMatrixCacheVersion;
            double4x4[] globeFixedMatrices =
                groupData.localToGlobeFixedMatrices ?? Array.Empty<double4x4>();

            for (int i = 0; i < totalInstances; ++i)
            {
                bool valid = TryGetInstanceMatrix(
                    groupData,
                    i,
                    out _,
                    out Matrix4x4 matrix);
                groupData.cachedMatrixValid[i] = valid;
                if (!valid)
                {
                    continue;
                }

                groupData.cachedWorldMatrices[i] = matrix;
                groupData.cachedLongitudeLatitudeValid[i] =
                    TryCalculateInstanceLongitudeLatitude(
                        i < globeFixedMatrices.Length ? globeFixedMatrices[i] : (double4x4?)null,
                        matrix,
                        out groupData.cachedLongitudeLatitudes[i]);
            }
        }

        private bool TryTransformGlobeFixedMatrixToUnityWorld(
            double4x4 localToGlobeFixedMatrix,
            out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (this._cachedGeoreference == null)
            {
                return false;
            }

            double4x4 georeferenceLocalToWorld =
                MatrixUtils.Matrix4x4ToDouble4x4(
                    this._cachedGeoreference.transform.localToWorldMatrix);
            double4x4 localToGeoreference =
                math.mul(this._cachedGeoreference.ecefToLocalMatrix, localToGlobeFixedMatrix);
            matrix = MatrixUtils.Double4x4ToMatrix4x4(
                math.mul(georeferenceLocalToWorld, localToGeoreference));
            return true;
        }

        private bool IsInstanceVisible(
            InstanceGroupData groupData,
            Transform sourceTransform,
            Matrix4x4 matrix,
            bool clipByPolygon,
            int instanceIndex)
        {
            if (groupData == null)
            {
                return false;
            }

            bool requiresClippingLongitudeLatitude = clipByPolygon && this._hasBounds;
            if (!requiresClippingLongitudeLatitude)
            {
                return true;
            }

            if (!TryGetCachedInstanceLongitudeLatitude(
                groupData,
                instanceIndex,
                sourceTransform,
                matrix,
                out double2 longitudeLatitude))
            {
                return false;
            }

            if (requiresClippingLongitudeLatitude &&
                !MightIntersectClippingBounds(longitudeLatitude))
            {
                return false;
            }

            return true;
        }

        private bool TryGetCachedInstanceLongitudeLatitude(
            InstanceGroupData groupData,
            int instanceIndex,
            Transform sourceTransform,
            Matrix4x4 matrix,
            out double2 longitudeLatitude)
        {
            longitudeLatitude = default;
            if (groupData != null &&
                groupData.cachedLongitudeLatitudeValid != null &&
                instanceIndex >= 0 &&
                instanceIndex < groupData.cachedLongitudeLatitudeValid.Length &&
                groupData.cachedLongitudeLatitudeValid[instanceIndex] &&
                groupData.cachedLongitudeLatitudes != null &&
                instanceIndex < groupData.cachedLongitudeLatitudes.Length)
            {
                longitudeLatitude = groupData.cachedLongitudeLatitudes[instanceIndex];
                return true;
            }

            return TryGetInstanceLongitudeLatitude(sourceTransform, matrix, out longitudeLatitude);
        }

        private bool TryCalculateInstanceLongitudeLatitude(
            double4x4? localToGlobeFixedMatrix,
            Matrix4x4 worldMatrix,
            out double2 longitudeLatitude)
        {
            longitudeLatitude = default;
            if (this._cachedGeoreference == null ||
                this._cachedGeoreference.ellipsoid == null)
            {
                return false;
            }

            if (localToGlobeFixedMatrix.HasValue)
            {
                double3 longitudeLatitudeHeight =
                    this._cachedGeoreference.ellipsoid
                        .CenteredFixedToLongitudeLatitudeHeight(
                            localToGlobeFixedMatrix.Value.c3.xyz);
                longitudeLatitude = longitudeLatitudeHeight.xy;
                return true;
            }

            Vector3 worldPosition = worldMatrix.GetColumn(3);
            double3 ecef =
                this._cachedGeoreference.TransformUnityPositionToEarthCenteredEarthFixed(
                    new double3(worldPosition.x, worldPosition.y, worldPosition.z));
            longitudeLatitude =
                this._cachedGeoreference.ellipsoid
                    .CenteredFixedToLongitudeLatitudeHeight(ecef).xy;
            return true;
        }

        private void RebuildRaycastColliders()
        {
            DestroyRaycastColliders();
            foreach (KeyValuePair<string, InstanceGroupData> pair in this._instanceGroups)
            {
                RebuildRaycastColliders(pair.Key, pair.Value);
            }
        }

        private void RebuildRaycastColliders(string groupId, InstanceGroupData groupData)
        {
            DestroyRaycastColliders(groupId);

            if (this._raycastColliderMode != RaycastColliderMode.BoxPerInstance ||
                !this.isActiveAndEnabled ||
                groupData == null ||
                groupData.mesh == null ||
                this._maxRaycastColliders <= 0)
            {
                return;
            }

            Transform[] transforms = groupData.instanceTransforms ?? Array.Empty<Transform>();
            Matrix4x4[] matrices = groupData.initialMatrices ?? Array.Empty<Matrix4x4>();
            int totalInstances = Math.Max(transforms.Length, matrices.Length);
            if (totalInstances == 0)
            {
                return;
            }

            int maxColliderCount = Math.Min(totalInstances, this._maxRaycastColliders);
            List<GameObject> colliderObjects = new List<GameObject>(maxColliderCount);
            Bounds localBounds = groupData.mesh.bounds;

            for (int i = 0; i < totalInstances && colliderObjects.Count < maxColliderCount; ++i)
            {
                if (!TryGetInstanceMatrix(groupData, i, out Transform sourceTransform, out Matrix4x4 matrix))
                {
                    continue;
                }

                GameObject colliderObject = new GameObject(
                    $"Instanced Tile Raycast {groupId} Instance {i}");
                colliderObject.hideFlags = HideFlags.DontSave | HideFlags.HideInHierarchy;
                colliderObject.layer = this.gameObject.layer;
                colliderObject.transform.SetParent(this.transform, false);

                if (sourceTransform != null)
                {
                    colliderObject.transform.SetPositionAndRotation(
                        sourceTransform.position,
                        sourceTransform.rotation);
                    colliderObject.transform.localScale = sourceTransform.lossyScale;
                }
                else
                {
                    colliderObject.transform.SetPositionAndRotation(
                        matrix.GetColumn(3),
                        matrix.rotation);
                    colliderObject.transform.localScale = matrix.lossyScale;
                }

                BoxCollider boxCollider = colliderObject.AddComponent<BoxCollider>();
                boxCollider.center = localBounds.center;
                boxCollider.size = localBounds.size;
                boxCollider.enabled = IsInstanceVisible(
                    groupData,
                    sourceTransform,
                    matrix,
                    this._polygonClipActive && groupData.usesClippingOverlay,
                    i);

                InstancedTilesetRaycastHit hit =
                    colliderObject.AddComponent<InstancedTilesetRaycastHit>();
                hit.instancedRenderer = this;
                hit.groupId = groupId;
                hit.instanceIndex = i;
                hit.primitiveIndex = groupData.primitiveIndex;

                colliderObjects.Add(colliderObject);
            }

            this._raycastColliderObjects[groupId] = colliderObjects;

            if (totalInstances > colliderObjects.Count && Debug.isDebugBuild)
            {
                Debug.LogWarning(
                    $"InstancedTilesetRenderer created {colliderObjects.Count} raycast colliders for {totalInstances} instances. Increase maxRaycastColliders to raycast all instances.",
                    this);
            }
        }

        private void UpdateRaycastColliderStates(string groupId, InstanceGroupData groupData)
        {
            if (!this._raycastColliderObjects.TryGetValue(groupId, out List<GameObject> colliderObjects))
            {
                return;
            }

            bool clipByPolygon = this._polygonClipActive &&
                groupData != null &&
                groupData.usesClippingOverlay;
            for (int i = 0; i < colliderObjects.Count; ++i)
            {
                GameObject colliderObject = colliderObjects[i];
                if (colliderObject == null)
                {
                    continue;
                }

                Collider collider = colliderObject.GetComponent<Collider>();
                if (collider == null)
                {
                    continue;
                }

                InstancedTilesetRaycastHit hit =
                    colliderObject.GetComponent<InstancedTilesetRaycastHit>();
                int instanceIndex = hit != null ? hit.instanceIndex : i;
                bool visible =
                    TryGetInstanceMatrix(groupData, instanceIndex, out Transform sourceTransform, out Matrix4x4 matrix) &&
                    IsInstanceVisible(groupData, sourceTransform, matrix, clipByPolygon, instanceIndex);
                if (collider.enabled != visible)
                {
                    collider.enabled = visible;
                }
            }
        }

        private void DestroyRaycastColliders()
        {
            foreach (string groupId in new List<string>(this._raycastColliderObjects.Keys))
            {
                DestroyRaycastColliders(groupId);
            }
        }

        private void DestroyRaycastColliders(string groupId)
        {
            if (!this._raycastColliderObjects.TryGetValue(groupId, out List<GameObject> colliderObjects))
            {
                return;
            }

            for (int i = 0; i < colliderObjects.Count; ++i)
            {
                GameObject colliderObject = colliderObjects[i];
                if (colliderObject == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(colliderObject);
                }
                else
                {
                    DestroyImmediate(colliderObject);
                }
            }

            this._raycastColliderObjects.Remove(groupId);
        }

        private void MarkClipCacheDirty()
        {
            this._clipCacheDirty = true;
        }

        private void HandleGeoreferenceChanged()
        {
            this._georeferenceChanged = true;
            ++this._instanceMatrixCacheVersion;
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
                ++this._instanceMatrixCacheVersion;
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

        private bool TryGetInstanceLongitudeLatitude(
            Transform sourceTransform,
            Matrix4x4 worldMatrix,
            out double2 longitudeLatitude)
        {
            longitudeLatitude = default;
            if (this._cachedGeoreference == null ||
                this._cachedGeoreference.ellipsoid == null)
            {
                return false;
            }

            if (sourceTransform != null &&
                sourceTransform.TryGetComponent(out CesiumGlobeAnchor anchor))
            {
                double3 anchorLongitudeLatitudeHeight =
                    this._cachedGeoreference.ellipsoid
                        .CenteredFixedToLongitudeLatitudeHeight(
                            anchor.localToGlobeFixedMatrix.c3.xyz);
                longitudeLatitude = anchorLongitudeLatitudeHeight.xy;
                return true;
            }

            Vector3 worldPosition = worldMatrix.GetColumn(3);
            double3 ecef =
                this._cachedGeoreference.TransformUnityPositionToEarthCenteredEarthFixed(
                    new double3(worldPosition.x, worldPosition.y, worldPosition.z));
            longitudeLatitude =
                this._cachedGeoreference.ellipsoid
                    .CenteredFixedToLongitudeLatitudeHeight(ecef).xy;
            return true;
        }

        private bool MightIntersectClippingBounds(double2 longitudeLatitude)
        {
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
