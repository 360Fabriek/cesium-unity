#!/usr/bin/env python3
"""Source-contract checks for material parity; NOT C#/Unity compilation or graphics tests.

Run from a package checkout with Python 3. Runtime tests are in
Tests/TestCesiumInstancedMaterials.cs and require the Unity Test Runner.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


def section(text, start, end):
    return text.split(start, 1)[1].split(end, 1)[0]


class MaterialContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        runtime = ROOT / 'Source/Runtime'
        cls.renderer = (runtime / 'CesiumInstancedRenderer.cs').read_text(encoding='utf-8')
        cls.materials = (runtime / 'CesiumInstancedRenderer.Materials.cs').read_text(encoding='utf-8')
        cls.settings = (runtime / 'CesiumInstancedRendering.cs').read_text(encoding='utf-8')
        cls.submit = section(cls.renderer, 'private void Submit(Camera camera)', 'private void BeforeCull(')

    def test_initialization_preserves_source_material_state(self):
        initialize = section(self.renderer, 'private void Initialize(', 'private void BuildBatches(')
        self.assertIn('_material = source.sharedMaterial;', initialize)
        self.assertIn('PrepareMaterialForInstancing(_material);', initialize)
        for mutation in ('_material.SetFloat(', '_material.SetVector(', '_material.SetTexture(',
                         '_material.EnableKeyword(', '_material.DisableKeyword('):
            self.assertNotIn(mutation, initialize)
        helper = section(self.materials, 'internal static void PrepareMaterialForInstancing(',
                         'internal static void CopySourceMaterialProperties(')
        self.assertIn('material.enableInstancing = true;', helper)
        self.assertNotRegex(helper, r'material\.(?:Set\w+|EnableKeyword|DisableKeyword)\s*\(')

    def test_slot_block_precedence_and_renderer_fallback(self):
        helper = section(self.materials, 'internal static void CopySourceMaterialProperties(',
                         '[ContextMenu(')
        self.assertIn('source.GetPropertyBlock(destination, 0);', helper)
        self.assertIn('if (destination.isEmpty) source.GetPropertyBlock(destination);', helper)
        self.assertLess(helper.index('GetPropertyBlock(destination, 0)'),
                        helper.index('if (destination.isEmpty)'))
        self.assertNotIn('SetPropertyBlock', helper)
        self.assertIn('CopySourceMaterialProperties(source, _properties);', self.renderer)

    def test_comparison_uses_existing_render_state_in_both_branches(self):
        self.assertIn('RenderParams parameters = _renderParams;', self.submit)
        self.assertIn('bool useGpuInstancing = UseGpuInstancingForDraw;', self.submit)
        self.assertIn('Graphics.RenderMeshInstanced(parameters, _mesh, 0, batch.matrices);', self.submit)
        self.assertIn('Graphics.RenderMesh(parameters, _mesh, 0, batch.matrices[i]);', self.submit)
        for mutation in ('SetMaterial', 'SetTexture', 'SetFloat', '_properties.Clear',
                         'RebuildBatches', 'RecreateTileset', '.enabled = true'):
            self.assertNotIn(mutation, self.submit)

    def test_diagnostic_does_not_bypass_instancing_capability(self):
        self.assertIn('internal bool UseGpuInstancingForDraw => _canInstance &&', self.materials)
        self.assertIn('!_materialDiagnosticSettings.forceIndividualDraws', self.materials)
        self.assertIn('_materialDiagnosticSettings = settings;', self.renderer)
        self.assertIn('public bool forceIndividualDraws = false;', self.settings)

    def test_live_probe_settings_are_forwarded(self):
        for property_name in ('lightProbeUsage', 'reflectionProbeUsage', 'shadowCastingMode',
                              'receiveShadows', 'renderingLayerMask'):
            self.assertIn(f'parameters.{property_name} = _sourceRenderer.{property_name};', self.submit)

    def test_diagnostic_toggle_is_excluded_from_reload_decision(self):
        validate = section(self.settings, 'private void OnValidate()', '[ContextMenu(')
        condition = section(validate, 'bool reload = ', 'RememberReloadSettings();')
        self.assertNotIn('forceIndividualDraws', condition)
        for setting in ('enableInstancedRendering', 'createInstanceColliders',
                        'maximumInstancesPerBatch', 'spatialBatchSize'):
            self.assertIn(setting, condition)
        self.assertLess(validate.index('if (!reload) return;'), validate.index('tileset.RecreateTileset();'))

    def test_diagnostics_are_on_demand_and_pipeline_independent(self):
        self.assertIn('[ContextMenu("Log Material Diagnostics")]', self.materials)
        self.assertIn('[ContextMenu("Log Material Diagnostics")]', self.settings)
        self.assertIn('Math.Min(8, renderers.Length)', self.settings)
        for allocation_or_log in ('AppendMaterialDiagnostics', 'StringBuilder', 'Debug.Log', 'GetComponentsInChildren'):
            self.assertNotIn(allocation_or_log, self.submit)
        for dependency in ('using UnityEngine.Rendering.Universal;', 'using UnityEngine.Rendering.HighDefinition;',
                           'Shader.SetGlobal', 'RenderSettings.ambientIntensity ='):
            self.assertNotIn(dependency, self.materials + self.settings)

    def test_new_mono_assets_have_distinct_metadata(self):
        paths = ('Source/Runtime/CesiumInstancedRenderer.Materials.cs', 'Tests/TestCesiumInstancedMaterials.cs')
        guids = []
        for path in paths:
            self.assertTrue((ROOT / path).is_file())
            meta = (ROOT / (path + '.meta')).read_text(encoding='utf-8')
            match = re.search(r'^guid: ([0-9a-f]{32})$', meta, re.MULTILINE)
            self.assertIsNotNone(match)
            guids.append(match.group(1))
        self.assertEqual(len(guids), len(set(guids)))


if __name__ == '__main__':
    unittest.main(verbosity=2)
