using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace CesiumForUnity
{
    internal partial class CesiumInstancedRenderer
    {
        private CesiumInstancedRendering _materialDiagnosticSettings;

        // This switch changes submission only. It must not recreate the tiles,
        // replace materials, discard raster bindings, or re-enable the prototype.
        internal bool UseGpuInstancingForDraw => _canInstance &&
            (_materialDiagnosticSettings == null || !_materialDiagnosticSettings.forceIndividualDraws);

        internal static void PrepareMaterialForInstancing(Material material)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            // Do not force alpha clipping, alter culling, rewrite PBR factors,
            // change shader keywords, or replace textures to make a dark draw brighter.
            material.enableInstancing = true;
        }

        internal static void CopySourceMaterialProperties(MeshRenderer source, MaterialPropertyBlock destination)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            // Each native prototype has one material/submesh. Unity uses the
            // material-index block INSTEAD OF the renderer block when both exist.
            // Merging the blocks here would also differ from a normal MeshRenderer.
            source.GetPropertyBlock(destination, 0);
            if (destination.isEmpty) source.GetPropertyBlock(destination);
        }

        [ContextMenu("Log Material Diagnostics")]
        private void LogMaterialDiagnostics()
        {
            var text = new StringBuilder();
            AppendMaterialDiagnostics(text);
            Debug.Log(text.ToString(), this);
        }

        internal void AppendMaterialDiagnostics(StringBuilder text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            text.AppendLine("Prototype: " + gameObject.name);
            if (_released || !_initialized || _material == null || _mesh == null)
            {
                text.AppendLine("  Not initialized, released, or missing a render resource.");
                return;
            }
            bool transformsValid = UpdateTransforms();
            int placements = 0, negativeDeterminants = 0;
            foreach (Batch batch in _batches)
            {
                placements += batch.indices.Length;
                if (transformsValid)
                    foreach (Matrix4x4 matrix in batch.matrices)
                        if (matrix.determinant < 0.0f) ++negativeDeterminants;
            }
            text.AppendLine("  Draw API: " + (UseGpuInstancingForDraw ? "RenderMeshInstanced" : "RenderMesh"));
            text.AppendLine("  Shader: " + (_material.shader == null ? "<missing>" : _material.shader.name));
            text.AppendLine("  Queue: " + _material.renderQueue + "; keywords: " + string.Join(", ", _material.shaderKeywords));
            text.AppendLine("  Placements: " + placements + "; batches: " + _batches.Count +
                "; valid transforms: " + transformsValid + "; negative world determinants: " + negativeDeterminants);
            text.AppendLine("  Normals: " + _mesh.HasVertexAttribute(VertexAttribute.Normal) +
                "; tangents: " + _mesh.HasVertexAttribute(VertexAttribute.Tangent) +
                "; colors: " + _mesh.HasVertexAttribute(VertexAttribute.Color) +
                "; UV0: " + _mesh.HasVertexAttribute(VertexAttribute.TexCoord0));
            if (_sourceRenderer != null)
                text.AppendLine("  Light probes: " + _sourceRenderer.lightProbeUsage +
                    "; reflection probes: " + _sourceRenderer.reflectionProbeUsage +
                    "; shadows: " + _sourceRenderer.shadowCastingMode);
            foreach (string property in new[] { "_baseColorFactor", "_metallicRoughnessFactor" })
            {
                bool overridden = _properties != null && _properties.HasProperty(property);
                if (overridden || _material.HasProperty(property))
                    text.AppendLine("  " + property + ": " +
                        (overridden ? _properties.GetVector(property) : _material.GetVector(property)).ToString("G9") +
                        (overridden ? " (property block)" : " (material)"));
            }
            foreach (string property in new[] { "_AlphaClip", "_BUILTIN_AlphaClip", "_AlphaCutoffEnable", "_Cull", "_CullMode", "_BUILTIN_CullMode" })
                if (_material.HasProperty(property))
                    text.AppendLine("  " + property + ": " + _material.GetFloat(property));
            foreach (string property in new[] { "_baseColorTexture", "_metallicRoughnessTexture", "_normalMapTexture" })
            {
                bool overridden = _properties != null && _properties.HasProperty(property);
                if (!overridden && !_material.HasProperty(property)) continue;
                Texture texture = overridden ? _properties.GetTexture(property) : _material.GetTexture(property);
                text.AppendLine("  " + property + ": " + (texture == null ? "<default/unassigned>" :
                    texture.name + " [" + texture.width + "x" + texture.height + "]") +
                    (overridden ? " (property block)" : " (material)"));
            }
            text.AppendLine("  Geometry coverage clipping: " + _clipCoverage);
            foreach (Overlay overlay in _overlays)
                if (overlay != null)
                    text.AppendLine("  Raster binding: " + overlay.key + "; texture present: " + (overlay.texture != null));
        }
    }
}
