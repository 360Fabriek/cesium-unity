using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if ENABLE_XR_MODULE
using UnityEngine.XR;
#endif

namespace CesiumForUnity
{
    [ExecuteInEditMode]
    [AddComponentMenu("")]
    internal class InstancedTileRenderer : MonoBehaviour
    {
        private const int _maxInstancesPerBatch = 1023;

        private Mesh _mesh;
        private Material _material;
        private Matrix4x4[] _localInstanceMatrices = Array.Empty<Matrix4x4>();
        private Vector4[] _instanceFeatures = Array.Empty<Vector4>();
        private readonly List<Matrix4x4[]> _worldMatrixBatches = new List<Matrix4x4[]>();
        private readonly List<Vector4[]> _featureBatches = new List<Vector4[]>();
        private readonly List<MaterialPropertyBlock> _propertyBlocks = new List<MaterialPropertyBlock>();
        private readonly int _instanceFeaturesID = Shader.PropertyToID("_CesiumInstanceFeatures");

        private Bounds _localBounds;
        private bool _createPickingProxies;
        private bool _pickingProxiesCreated;
        private bool _initialized;

        public Material sharedMaterial => this._material;

        public int instanceCount => this._localInstanceMatrices.Length;

        public bool isEnabledForRendering { get; set; } = true;

        private void OnEnable()
        {
            Camera.onPreCull += this.HandleCameraPreCull;
            RenderPipelineManager.beginCameraRendering += this.HandleBeginCameraRendering;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= this.HandleCameraPreCull;
            RenderPipelineManager.beginCameraRendering -= this.HandleBeginCameraRendering;
        }

        public void Initialize(
            Mesh mesh,
            Material material,
            Matrix4x4[] instanceMatrices,
            Vector4[] instanceFeatures,
            bool createPickingProxies)
        {
            this._mesh = mesh;
            this._material = material;
            this._material.enableInstancing = true;
            this._localInstanceMatrices = instanceMatrices ?? Array.Empty<Matrix4x4>();
            this._instanceFeatures = instanceFeatures ?? Array.Empty<Vector4>();
            this._createPickingProxies = createPickingProxies;

            this.BuildBatches();
            this._localBounds = this.ComputeLocalBounds();
            this.CreatePickingProxiesIfNeeded();
            this._initialized = true;
        }

        private void CreatePickingProxiesIfNeeded()
        {
            if (!this._createPickingProxies || this._pickingProxiesCreated || this._mesh == null)
            {
                return;
            }

            for (int i = 0; i < this._localInstanceMatrices.Length; ++i)
            {
                GameObject proxy = new GameObject($"Instanced Physics Proxy {i}");
                proxy.hideFlags = HideFlags.HideInHierarchy;
                proxy.layer = this.gameObject.layer;

                Transform proxyTransform = proxy.transform;
                proxyTransform.SetParent(this.transform, false);

                Matrix4x4 matrix = this._localInstanceMatrices[i];
                proxyTransform.localPosition = matrix.GetColumn(3);
                proxyTransform.localRotation = matrix.rotation;
                proxyTransform.localScale = matrix.lossyScale;

                MeshCollider meshCollider = proxy.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = this._mesh;

                InstancedTilePickingProxy pickingProxy =
                    proxy.AddComponent<InstancedTilePickingProxy>();
                pickingProxy.instanceIndex = i;
            }

            this._pickingProxiesCreated = true;
        }

        private void BuildBatches()
        {
            this._worldMatrixBatches.Clear();
            this._featureBatches.Clear();
            this._propertyBlocks.Clear();

            int totalInstances = this._localInstanceMatrices.Length;
            for (int offset = 0; offset < totalInstances; offset += _maxInstancesPerBatch)
            {
                int batchCount = Mathf.Min(_maxInstancesPerBatch, totalInstances - offset);
                this._worldMatrixBatches.Add(new Matrix4x4[batchCount]);

                Vector4[] featureBatch = new Vector4[batchCount];
                for (int i = 0; i < batchCount; ++i)
                {
                    if (offset + i < this._instanceFeatures.Length)
                    {
                        featureBatch[i] = this._instanceFeatures[offset + i];
                    }
                }

                this._featureBatches.Add(featureBatch);

                MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
                propertyBlock.SetVectorArray(this._instanceFeaturesID, featureBatch);
                this._propertyBlocks.Add(propertyBlock);
            }
        }

        private Bounds ComputeLocalBounds()
        {
            if (this._mesh == null || this._localInstanceMatrices.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            Bounds meshBounds = this._mesh.bounds;
            Vector3[] corners = new Vector3[8];
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            Vector3 extents = meshBounds.extents;
            Vector3 center = meshBounds.center;

            corners[0] = center + new Vector3(-extents.x, -extents.y, -extents.z);
            corners[1] = center + new Vector3(-extents.x, -extents.y, extents.z);
            corners[2] = center + new Vector3(-extents.x, extents.y, -extents.z);
            corners[3] = center + new Vector3(-extents.x, extents.y, extents.z);
            corners[4] = center + new Vector3(extents.x, -extents.y, -extents.z);
            corners[5] = center + new Vector3(extents.x, -extents.y, extents.z);
            corners[6] = center + new Vector3(extents.x, extents.y, -extents.z);
            corners[7] = center + new Vector3(extents.x, extents.y, extents.z);

            for (int matrixIndex = 0; matrixIndex < this._localInstanceMatrices.Length; ++matrixIndex)
            {
                Matrix4x4 matrix = this._localInstanceMatrices[matrixIndex];
                for (int cornerIndex = 0; cornerIndex < corners.Length; ++cornerIndex)
                {
                    Vector3 point = matrix.MultiplyPoint3x4(corners[cornerIndex]);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }

            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);

#if ENABLE_XR_MODULE
            if (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview)
            {
                bounds.Expand(bounds.size * 0.1f);
            }
#endif

            return bounds;
        }

        private void HandleCameraPreCull(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

            this.RenderForCamera(camera);
        }

        private void HandleBeginCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            this.RenderForCamera(camera);
        }

        private void RenderForCamera(Camera camera)
        {
            if (!this._initialized || !this.isEnabledForRendering || this._mesh == null || this._material == null || camera == null)
            {
                return;
            }

            Matrix4x4 localToWorld = this.transform.localToWorldMatrix;

            int instanceIndex = 0;
            for (int batchIndex = 0; batchIndex < this._worldMatrixBatches.Count; ++batchIndex)
            {
                Matrix4x4[] batch = this._worldMatrixBatches[batchIndex];
                for (int i = 0; i < batch.Length; ++i)
                {
                    batch[i] = localToWorld * this._localInstanceMatrices[instanceIndex++];
                }

                Graphics.DrawMeshInstanced(
                    this._mesh,
                    0,
                    this._material,
                    batch,
                    batch.Length,
                    this._propertyBlocks[batchIndex],
                    ShadowCastingMode.On,
                    true,
                    this.gameObject.layer,
                    camera,
                    LightProbeUsage.BlendProbes,
                    null);
            }
        }
    }
}
