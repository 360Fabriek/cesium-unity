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
        private const int UnityMaxBatchSize = 1023;
        private static readonly int ClippingOverlayTexturePropertyId =
            Shader.PropertyToID("_overlayTexture_Clipping");

        private readonly Dictionary<string, InstanceGroupData> _instanceGroups =
            new Dictionary<string, InstanceGroupData>();

        private bool _checkedInstancingSupport;
        private Cesium3DTileset _cachedTileset;
        private CesiumGeoreference _cachedGeoreference;
        private bool _polygonClipActive;
        private Matrix4x4 _worldToTileset;
        private readonly List<List<double2>> _clippingPolygons = new List<List<double2>>();

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
        }

        private void Update()
        {
            if (_instanceGroups.Count == 0)
            {
                return;
            }

            UpdatePolygonClippingContext();

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

                if (clipByPolygon && !IsInsideClippingPolygons(matrix))
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
                    groupData.propertyBlock);
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
                    groupData.propertyBlock);
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

        private void UpdatePolygonClippingContext()
        {
            this._polygonClipActive = false;
            this._clippingPolygons.Clear();

            Cesium3DTileset tileset = this._cachedTileset != null
                ? this._cachedTileset
                : this.GetComponentInParent<Cesium3DTileset>();
            if (tileset == null)
            {
                return;
            }

            this._cachedTileset = tileset;
            this._worldToTileset = tileset.transform.worldToLocalMatrix;

            CesiumGeoreference georeference = this._cachedGeoreference != null
                ? this._cachedGeoreference
                : tileset.GetComponentInParent<CesiumGeoreference>();
            if (georeference == null)
            {
                return;
            }

            this._cachedGeoreference = georeference;

            CesiumPolygonRasterOverlay[] overlays = tileset.GetComponents<CesiumPolygonRasterOverlay>();
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

                    this._clippingPolygons.Add(cartographicPoints);
                }
            }

            this._polygonClipActive = this._clippingPolygons.Count > 0;
        }

        private bool IsInsideClippingPolygons(Matrix4x4 worldMatrix)
        {
            Vector3 worldPosition = worldMatrix.GetColumn(3);
            Vector3 tilesetSpace = this._worldToTileset.MultiplyPoint3x4(worldPosition);
            double3 ecef =
                this._cachedGeoreference.TransformUnityPositionToEarthCenteredEarthFixed(
                    new double3(tilesetSpace.x, tilesetSpace.y, tilesetSpace.z));
            double2 lonLat =
                this._cachedGeoreference.ellipsoid
                    .CenteredFixedToLongitudeLatitudeHeight(ecef).xy;

            for (int i = 0; i < this._clippingPolygons.Count; ++i)
            {
                if (IsPointInsidePolygon(lonLat, this._clippingPolygons[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointInsidePolygon(double2 point, List<double2> polygon)
        {
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double2 a = polygon[i];
                double2 b = polygon[j];

                bool intersects =
                    ((a.y > point.y) != (b.y > point.y)) &&
                    (point.x < ((b.x - a.x) * (point.y - a.y)) / ((b.y - a.y) + 1e-15) + a.x);

                if (intersects)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
