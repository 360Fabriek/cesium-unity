using UnityEngine;

namespace CesiumForUnity
{
    /// <summary>
    /// Optional settings for the batched triangle renderer. Without this component,
    /// compatible instanced tiles use the defaults below. Changing these settings
    /// requires reloading the tileset.
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

        private void OnValidate()
        {
            maximumInstancesPerBatch = Mathf.Clamp(maximumInstancesPerBatch, 1, 128);
            if (float.IsNaN(spatialBatchSize) || float.IsInfinity(spatialBatchSize))
                spatialBatchSize = 128.0f;
            spatialBatchSize = Mathf.Max(1.0f, spatialBatchSize);
            Cesium3DTileset tileset = GetComponent<Cesium3DTileset>();
            if (tileset != null)
                tileset.RecreateTileset();
        }
    }
}
