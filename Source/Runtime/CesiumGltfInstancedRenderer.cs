using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CesiumForUnity
{
    [ExecuteInEditMode]
    [AddComponentMenu("")]
    internal class CesiumGltfInstancedRenderer : MonoBehaviour
    {
        private const int MaxInstancesPerBatch = 1023;
        private const string DefaultTilesetMaterialName =
            "CesiumDefaultTilesetMaterial";
        private const string InstanceChildNamePrefix = "Instance ";
        private static readonly Matrix4x4 MirrorXMatrix =
            Matrix4x4.Scale(new Vector3(-1.0f, 1.0f, 1.0f));

        private Mesh _mesh;
        private Mesh _mirroredMesh;
        private Material _material;
        private Matrix4x4[] _instanceLocalMatrices = Array.Empty<Matrix4x4>();
        private readonly List<Transform> _instanceTransforms = new List<Transform>();
        private Matrix4x4[][] _worldMatrixBatches = Array.Empty<Matrix4x4[]>();
        private Matrix4x4[][] _mirroredWorldMatrixBatches = Array.Empty<Matrix4x4[]>();
        private InstanceData[][] _worldInstanceBatches = Array.Empty<InstanceData[]>();
        private InstanceData[][] _mirroredWorldInstanceBatches = Array.Empty<InstanceData[]>();
        private Bounds[] _worldBoundsBatches = Array.Empty<Bounds>();
        private Bounds[] _mirroredWorldBoundsBatches = Array.Empty<Bounds>();
        private int _worldMatrixCount;
        private int _mirroredWorldMatrixCount;
        private Matrix4x4 _lastLocalToWorldMatrix;
        private int _lastChildCount = -1;
        private bool _worldMatricesDirty = true;
        private bool _failedToCreateMirroredMesh;
        private bool _useGpuInstancing;

        private struct InstanceData
        {
            public Matrix4x4 objectToWorld;
        }

        public Mesh mesh
        {
            get => this._mesh;
            set
            {
                if (this._mesh == value)
                {
                    return;
                }

                this.DestroyMirroredMesh();
                this._mesh = value;
                this._failedToCreateMirroredMesh = false;
            }
        }

        public Material material
        {
            get => this._material;
            set
            {
                this._material = value;
                this._useGpuInstancing =
                    this._material != null &&
                    this._material.name.StartsWith(
                        DefaultTilesetMaterialName,
                        StringComparison.Ordinal);
                this.ApplyInstancingFlagToMaterial();
            }
        }

        public Matrix4x4[] instanceLocalMatrices
        {
            get => this._instanceLocalMatrices;
            set
            {
                this._instanceLocalMatrices = value ?? Array.Empty<Matrix4x4>();
                this.RebuildBatches();
            }
        }

        private void OnEnable()
        {
            this._worldMatricesDirty = true;
        }

        private void OnTransformParentChanged()
        {
            this._worldMatricesDirty = true;
        }

        private void OnTransformChildrenChanged()
        {
            this._lastChildCount = -1;
            this._worldMatricesDirty = true;
        }

        private void Update()
        {
            if (this._mesh == null ||
                this._material == null ||
                this._instanceLocalMatrices.Length == 0 ||
                this._worldMatrixBatches.Length == 0)
            {
                return;
            }

            this.UpdateWorldMatricesIfNeeded();
            this.DrawBatches(
                this._mesh,
                this._worldMatrixBatches,
                this._worldInstanceBatches,
                this._worldBoundsBatches,
                this._worldMatrixCount);

            if (this._mirroredWorldMatrixCount > 0)
            {
                this.DrawBatches(
                    this.GetMirroredMesh(),
                    this._mirroredWorldMatrixBatches,
                    this._mirroredWorldInstanceBatches,
                    this._mirroredWorldBoundsBatches,
                    this._mirroredWorldMatrixCount);
            }
        }

        private void OnDestroy()
        {
            this.DestroyMirroredMesh();
        }

        private void DrawBatches(
            Mesh meshToDraw,
            Matrix4x4[][] batches,
            InstanceData[][] instanceBatches,
            Bounds[] batchBounds,
            int matrixCount)
        {
            if (meshToDraw == null || matrixCount == 0)
            {
                return;
            }

            int remaining = matrixCount;
            for (int batchIndex = 0; batchIndex < batches.Length && remaining > 0; batchIndex++)
            {
                Matrix4x4[] batch = batches[batchIndex];
                int count = Mathf.Min(batch.Length, remaining);

                if (this._useGpuInstancing && this._material.enableInstancing)
                {
                    RenderParams renderParams = new RenderParams(this._material)
                    {
                        layer = this.gameObject.layer,
                        receiveShadows = true,
                        shadowCastingMode = ShadowCastingMode.On,
                        worldBounds = batchBounds[batchIndex]
                    };

                    Graphics.RenderMeshInstanced(
                        renderParams,
                        meshToDraw,
                        0,
                        instanceBatches[batchIndex],
                        count);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        Graphics.DrawMesh(
                            meshToDraw,
                            batch[i],
                            this._material,
                            this.gameObject.layer,
                            null,
                            0,
                            null,
                            ShadowCastingMode.On,
                            true);
                    }
                }

                remaining -= count;
            }
        }

        private void RebuildBatches()
        {
            int instanceCount = this._instanceLocalMatrices.Length;
            int batchCount = (instanceCount + MaxInstancesPerBatch - 1) / MaxInstancesPerBatch;
            this._worldMatrixBatches = this.CreateMatrixBatches(batchCount);
            this._mirroredWorldMatrixBatches = this.CreateMatrixBatches(batchCount);
            this._worldInstanceBatches = this.CreateInstanceBatches(batchCount);
            this._mirroredWorldInstanceBatches =
                this.CreateInstanceBatches(batchCount);
            this._worldBoundsBatches = new Bounds[batchCount];
            this._mirroredWorldBoundsBatches = new Bounds[batchCount];
            this._worldMatrixCount = 0;
            this._mirroredWorldMatrixCount = 0;
            this._worldMatricesDirty = true;
        }

        private Matrix4x4[][] CreateMatrixBatches(int batchCount)
        {
            Matrix4x4[][] result = new Matrix4x4[batchCount][];
            for (int i = 0; i < batchCount; i++)
            {
                int count = Mathf.Min(
                    MaxInstancesPerBatch,
                    this._instanceLocalMatrices.Length - i * MaxInstancesPerBatch);
                result[i] = new Matrix4x4[count];
            }

            return result;
        }

        private InstanceData[][] CreateInstanceBatches(int batchCount)
        {
            InstanceData[][] result = new InstanceData[batchCount][];
            for (int i = 0; i < batchCount; i++)
            {
                int count = Mathf.Min(
                    MaxInstancesPerBatch,
                    this._instanceLocalMatrices.Length - i * MaxInstancesPerBatch);
                result[i] = new InstanceData[count];
            }

            return result;
        }

        private void UpdateWorldMatricesIfNeeded()
        {
            Matrix4x4 localToWorld = this.transform.localToWorldMatrix;
            bool useChildTransforms = this.TryUseChildTransforms();

            if (!this._worldMatricesDirty && this._lastLocalToWorldMatrix == localToWorld)
            {
                return;
            }

            this._lastLocalToWorldMatrix = localToWorld;
            this._worldMatrixCount = 0;
            this._mirroredWorldMatrixCount = 0;

            for (int i = 0; i < this._instanceLocalMatrices.Length; i++)
            {
                Matrix4x4 worldMatrix = useChildTransforms
                    ? this._instanceTransforms[i].localToWorldMatrix
                    : localToWorld * this._instanceLocalMatrices[i];
                if (worldMatrix.determinant < 0.0f)
                {
                    this.SetBatchMatrix(
                        this._mirroredWorldMatrixBatches,
                        this._mirroredWorldInstanceBatches,
                        this._mirroredWorldBoundsBatches,
                        this._mirroredWorldMatrixCount++,
                        worldMatrix * MirrorXMatrix);
                }
                else
                {
                    this.SetBatchMatrix(
                        this._worldMatrixBatches,
                        this._worldInstanceBatches,
                        this._worldBoundsBatches,
                        this._worldMatrixCount++,
                        worldMatrix);
                }
            }

            this._worldMatricesDirty = false;
        }

        private bool TryUseChildTransforms()
        {
            if (this._lastChildCount == this.transform.childCount &&
                this._instanceTransforms.Count == this._instanceLocalMatrices.Length)
            {
                return true;
            }

            this._lastChildCount = this.transform.childCount;
            this._instanceTransforms.Clear();

            for (int i = 0; i < this.transform.childCount; i++)
            {
                Transform child = this.transform.GetChild(i);
                if (child.name.StartsWith(
                    InstanceChildNamePrefix,
                    StringComparison.Ordinal))
                {
                    this._instanceTransforms.Add(child);
                }
            }

            return this._instanceTransforms.Count == this._instanceLocalMatrices.Length;
        }

        private void SetBatchMatrix(
            Matrix4x4[][] batches,
            InstanceData[][] instanceBatches,
            Bounds[] batchBounds,
            int index,
            Matrix4x4 matrix)
        {
            int batchIndex = index / MaxInstancesPerBatch;
            int elementIndex = index % MaxInstancesPerBatch;
            batches[batchIndex][elementIndex] = matrix;
            instanceBatches[batchIndex][elementIndex].objectToWorld = matrix;

            Bounds worldBounds = this.TransformBounds(this._mesh.bounds, matrix);
            if (elementIndex == 0)
            {
                batchBounds[batchIndex] = worldBounds;
            }
            else
            {
                batchBounds[batchIndex].Encapsulate(worldBounds);
            }
        }

        private Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            Vector3 extents = bounds.extents;

            Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0.0f, 0.0f));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0.0f, extents.y, 0.0f));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0.0f, 0.0f, extents.z));

            extents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

            return new Bounds(center, extents * 2.0f);
        }

        private void ApplyInstancingFlagToMaterial()
        {
            if (this._material != null && this._useGpuInstancing)
            {
                this._material.enableInstancing = true;
            }
        }

        private Mesh GetMirroredMesh()
        {
            if (this._mirroredMesh != null ||
                this._failedToCreateMirroredMesh)
            {
                return this._mirroredMesh ?? this._mesh;
            }

            try
            {
                this._mirroredMesh = UnityEngine.Object.Instantiate(this._mesh);
                this._mirroredMesh.name = this._mesh.name + " (Mirrored X)";
                this._mirroredMesh.hideFlags = HideFlags.HideAndDontSave;

                Vector3[] vertices = this._mirroredMesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i].x = -vertices[i].x;
                }

                this._mirroredMesh.vertices = vertices;

                Vector3[] normals = this._mirroredMesh.normals;
                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i].x = -normals[i].x;
                }

                this._mirroredMesh.normals = normals;

                Vector4[] tangents = this._mirroredMesh.tangents;
                for (int i = 0; i < tangents.Length; i++)
                {
                    tangents[i].x = -tangents[i].x;
                    tangents[i].w = -tangents[i].w;
                }

                this._mirroredMesh.tangents = tangents;

                for (int subMesh = 0; subMesh < this._mirroredMesh.subMeshCount; subMesh++)
                {
                    int[] triangles = this._mirroredMesh.GetTriangles(subMesh);
                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                    {
                        int temp = triangles[i + 1];
                        triangles[i + 1] = triangles[i + 2];
                        triangles[i + 2] = temp;
                    }

                    this._mirroredMesh.SetTriangles(triangles, subMesh);
                }

                this._mirroredMesh.RecalculateBounds();
            }
            catch (Exception exception)
            {
                this._failedToCreateMirroredMesh = true;
                Debug.LogWarning(
                    "Failed to create mirrored mesh for glTF instances. " +
                    "Instances with negative-determinant transforms may have incorrect normals. " +
                    exception.Message);
                return this._mesh;
            }

            return this._mirroredMesh;
        }

        private void DestroyMirroredMesh()
        {
            if (this._mirroredMesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(this._mirroredMesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(this._mirroredMesh);
            }

            this._mirroredMesh = null;
        }
    }
}
