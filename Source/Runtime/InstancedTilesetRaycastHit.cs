using UnityEngine;

namespace CesiumForUnity
{
    /// <summary>
    /// Identifies a raycast proxy collider created for an instanced tile.
    /// </summary>
    [AddComponentMenu("")]
    public class InstancedTilesetRaycastHit : MonoBehaviour
    {
        public InstancedTilesetRenderer instancedRenderer { get; internal set; }
        public string groupId { get; internal set; }
        public int primitiveIndex { get; internal set; }
        public int instanceIndex { get; internal set; }
    }
}
