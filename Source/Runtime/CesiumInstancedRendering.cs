using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace CesiumForUnity
{
    /// <summary>
    /// Optional settings for the batched triangle renderer. Without this component,
    /// compatible instanced tiles use the defaults below. Geometry/collider settings
    /// require a reload; forceIndividualDraws is a live diagnostic switch.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Cesium3DTileset))]
    [AddComponentMenu("Cesium/Cesium Instanced Rendering")]
    public class CesiumInstancedRendering : MonoBehaviour
    {
        [Tooltip("Use the batched renderer with Cesium's default lit and unlit materials.")]
        public bool enableInstancedRendering = true;

        [Range(1, 128)]
        [Tooltip("Maximum placements per draw. 128 also accommodates smaller instancing constant buffers.")]
        public int maximumInstancesPerBatch = 128;

        [Min(1.0f)]
        [Tooltip("Size in metres of the spatial cells used to group placements. This does not clip geometry.")]
        public float spatialBatchSize = 128.0f;

        [Tooltip("Preserve per-instance collision when the tileset's Create Physics Meshes is enabled. Raster clipping is visual, not a collision mesh cut.")]
        public bool createInstanceColliders = true;

        [Tooltip("Diagnostic only: use individual RenderMesh calls with the SAME materials, placements and raster clipping. Slower; leave off normally. Does not select the legacy loader.")]
        public bool forceIndividualDraws = false;

        [NonSerialized] private bool _haveReloadSettings;
        [NonSerialized] private bool _lastEnabled, _lastColliders;
        [NonSerialized] private int _lastMaximum;
        [NonSerialized] private float _lastCellSize;

        private void OnEnable()
        {
            RememberReloadSettings();
        }

        private void RememberReloadSettings()
        {
            _haveReloadSettings = true;
            _lastEnabled = enableInstancedRendering;
            _lastColliders = createInstanceColliders;
            _lastMaximum = maximumInstancesPerBatch;
            _lastCellSize = spatialBatchSize;
        }

        private void OnValidate()
        {
            maximumInstancesPerBatch = Mathf.Clamp(maximumInstancesPerBatch, 1, 128);
            if (float.IsNaN(spatialBatchSize) || float.IsInfinity(spatialBatchSize))
                spatialBatchSize = 128.0f;
            spatialBatchSize = Mathf.Max(1.0f, spatialBatchSize);
            bool reload = !_haveReloadSettings || _lastEnabled != enableInstancedRendering ||
                _lastColliders != createInstanceColliders || _lastMaximum != maximumInstancesPerBatch ||
                _lastCellSize != spatialBatchSize;
            RememberReloadSettings();
            // Do not reload between diagnostic A/B captures. That could change
            // the selected tiles/raster LOD and invalidate the comparison.
            if (!reload) return;
            Cesium3DTileset tileset = GetComponent<Cesium3DTileset>();
            if (tileset != null) tileset.RecreateTileset();
        }

        [ContextMenu("Log Material Diagnostics")]
        public void LogMaterialDiagnostics()
        {
            var text = new StringBuilder();
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Texture reflection = ReflectionProbe.defaultTexture;
            LightProbes probes = LightmapSettings.lightProbes;
            text.AppendLine("Cesium instanced material diagnostics");
            text.AppendLine("Unity: " + Application.unityVersion + "; graphics API: " + SystemInfo.graphicsDeviceType);
            text.AppendLine("Pipeline: " + (pipeline == null ? "Built-in" : pipeline.GetType().FullName));
            text.AppendLine("Color space: " + QualitySettings.activeColorSpace + "; ambient mode: " + RenderSettings.ambientMode +
                "; ambient intensity: " + RenderSettings.ambientIntensity);
            text.AppendLine("Light probes: " + (probes == null ? 0 : probes.count) +
                "; default reflection: " + (reflection == null ? "<none>" : reflection.name));
            text.AppendLine("Force individual draws: " + forceIndividualDraws);
            var renderers = GetComponentsInChildren<CesiumInstancedRenderer>(true);
            text.AppendLine("Loaded prototype renderers: " + renderers.Length + "; samples below: at most 8.");
            for (int i = 0; i < Math.Min(8, renderers.Length); ++i)
                renderers[i].AppendMaterialDiagnostics(text);
            text.AppendLine("For a specific affected prototype, use Log Material Diagnostics on its CesiumInstancedRenderer component.");
            Debug.Log(text.ToString(), this);
        }
    }
}
