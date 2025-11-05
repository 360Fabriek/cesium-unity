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

        private readonly Dictionary<string, InstanceGroupData> _instanceGroups =
            new Dictionary<string, InstanceGroupData>();

        private bool _checkedInstancingSupport;

        [Serializable]
        public class InstanceGroupData
        {
            public const int DefaultMaxBatchSize = 1023;

            public Mesh mesh;
            public Material material;
            public Transform[] instanceTransforms = Array.Empty<Transform>();
            public Matrix4x4[] initialMatrices = Array.Empty<Matrix4x4>();
            public Matrix4x4[] batchBuffer;
            public MaterialPropertyBlock propertyBlock;
            public int maxInstancesPerBatch = DefaultMaxBatchSize;

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

            for (int batchStart = 0; batchStart < totalInstances; batchStart += maxBatchSize)
            {
                int batchSize = Mathf.Min(maxBatchSize, totalInstances - batchStart);
                bool hasValidInstance = false;

                for (int i = 0; i < batchSize; ++i)
                {
                    int instanceIndex = batchStart + i;
                    Matrix4x4 matrix = Matrix4x4.identity;

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

                    batchBuffer[i] = matrix;
                }

                if (!hasValidInstance)
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
            }
        }

        public void AddInstanceGroup(string groupId, Mesh mesh, Material material, List<double4x4> matrices)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return;
            }

            EnsureInstancingSupportLogged();

            var groupData = new InstanceGroupData
            {
                mesh = mesh,
                material = material
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
    }
}
