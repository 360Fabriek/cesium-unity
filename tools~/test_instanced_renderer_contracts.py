#!/usr/bin/env python3
"""Offline serialization/build-integration checks; not Unity/native compilation."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
RESOURCE = ROOT / 'Source/Runtime/Resources'

def objects(text):
    decoder = json.JSONDecoder()
    result = []
    while text.strip():
        text = text.lstrip()
        value, end = decoder.raw_decode(text)
        result.append(value)
        text = text[end:]
    return result

class GraphContracts(unittest.TestCase):
    def setUp(self):
        self.items = objects((RESOURCE / 'CesiumRasterOverlay.shadersubgraph').read_text())
        self.graph = self.items[0]
        self.by_id = {v['m_ObjectId']: v for v in self.items}

    def test_ids_are_unique(self):
        self.assertEqual(len(self.items), len(self.by_id))

    def test_edges_point_to_existing_slots(self):
        for edge in self.graph['m_Edges']:
            for key in ('m_InputSlot', 'm_OutputSlot'):
                endpoint = edge[key]
                node = self.by_id[endpoint['m_Node']['m_Id']]
                slots = [self.by_id[s['m_Id']]['m_Id'] for s in node['m_Slots']]
                self.assertIn(endpoint['m_SlotId'], slots)

    def test_interface_guids_are_unchanged(self):
        guids = [self.by_id[p['m_Id']]['m_Guid']['m_GuidSerialized'] for p in self.graph['m_Properties']]
        self.assertEqual(guids, ['83e390c8-0352-4898-86e2-9172c1b9394b',
            '6fcbe01a-3c9c-48dc-99a1-39447c83ffda', 'fab66303-04f0-4ac1-8673-91dec11133b2',
            'e055c41d-2685-42d1-a890-5f8a0de03f45'])
        output = self.by_id[self.graph['m_OutputNode']['m_Id']]
        self.assertEqual(self.by_id[output['m_Slots'][0]['m_Id']]['m_Id'], 1)

    def test_hlsl_binding_and_full_precision(self):
        function = next(v for v in self.items if v['m_Type'].endswith('.CustomFunctionNode'))
        meta = (RESOURCE / 'CesiumInstancedRaster.hlsl.meta').read_text()
        self.assertIn('guid: ' + function['m_FunctionSource'], meta)
        self.assertEqual(function['m_Precision'], 1)
        self.assertEqual(function['m_FunctionName'], 'CesiumRasterOverlay')
        position = next(v for v in self.items if v['m_Type'].endswith('.PositionNode'))
        self.assertEqual(position['m_Space'], 4)
        self.assertEqual(position['m_Precision'], 1)

    def test_graph_is_reproducible(self):
        path = ROOT / 'tools~/generate_instanced_overlay_graph.py'
        spec = importlib.util.spec_from_file_location('graph_generator', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(module.generate(), (RESOURCE / 'CesiumRasterOverlay.shadersubgraph').read_text())

    def test_existing_pipeline_targets_not_replaced(self):
        for name in ('CesiumDefaultTilesetShader', 'CesiumUnlitTilesetShader'):
            path = RESOURCE / (name + '.shadergraph')
            if not path.exists():
                self.skipTest('Run in a complete checkout to inspect the unchanged parent Shader Graphs.')
            values = objects(path.read_text())
            by_id = {v['m_ObjectId']: v for v in values}
            types = {by_id[v['m_Id']]['m_Type'].rsplit('.', 1)[-1] for v in values[0]['m_ActiveTargets']}
            self.assertEqual(types, {'BuiltInTarget', 'UniversalTarget', 'HDTarget'})

class BuildIntegration(unittest.TestCase):
    def test_base_handoff_stays_opaque_and_extensions_are_restored(self):
        text = (ROOT / 'native~/src/Runtime/UnityPrepareInstancedRendererResources.cpp').read_text()
        self.assertNotIn('static_cast<LoadThreadResult', text)
        self.assertIn('static_cast<BatchedLoadResult*', text)
        self.assertIn('load ? load->baseResources : nullptr', text)
        self.assertIn('model.nodes[entry.first].extensions.emplace', text)

    def test_factory_selects_batched_backend(self):
        text = (ROOT / 'native~/src/Runtime/UnityTilesetExternals.cpp').read_text()
        self.assertIn('make_shared<UnityPrepareInstancedRendererResources>', text)
        self.assertIn('include(cmake/PatchInstancedRasterUpsampling.cmake)', (ROOT / 'native~/CMakeLists.txt').read_text())

    def configure_fixture(self, missing=False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        cmake = root / 'native~/cmake'
        cmake.mkdir(parents=True)
        shutil.copyfile(ROOT / 'native~/cmake/PatchInstancedRasterUpsampling.cmake', cmake / 'PatchInstancedRasterUpsampling.cmake')
        source = root / 'native~/extern/cesium-native/CesiumRasterOverlays/src/RasterOverlayUtilities.cpp'
        source.parent.mkdir(parents=True)
        source.write_text('void fixture() {\n' + ('  changed upstream;\n' if missing else
            '  CESIUM_TRACE("upsampleGltfForRasterOverlays");\n  Model result;\n') + '}\n')
        original = source.read_bytes()
        project = root / 'CMakeLists.txt'
        project.write_text('cmake_minimum_required(VERSION 3.18)\nproject(Fixture LANGUAGES CXX)\n' +
            'add_library(CesiumRasterOverlays STATIC "' + source.as_posix() + '")\n' +
            'include("' + (cmake / 'PatchInstancedRasterUpsampling.cmake').as_posix() + '")\n' +
            'get_target_property(selected CesiumRasterOverlays SOURCES)\n' +
            'file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/selected.txt" "${selected}")\n')
        result = subprocess.run(['cmake', '-S', str(root), '-B', str(root / 'build')], capture_output=True, text=True)
        self.assertEqual(source.read_bytes(), original, 'Submodule checkout must not be mutated.')
        return root, result

    def test_cmake_patches_only_build_copy_and_replaces_target_source(self):
        root, result = self.configure_fixture()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        generated = root / 'build/instanced-raster/RasterOverlayUtilities.cpp'
        text = generated.read_text()
        self.assertEqual(text.count('Model instancedChild(parentModel);'), 1)
        self.assertIn('CesiumUnityInstanceRasterChild', text)
        self.assertEqual((root / 'build/selected.txt').read_text(), generated.as_posix())
        second = subprocess.run(['cmake', '-S', str(root), '-B', str(root / 'build')], capture_output=True, text=True)
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(generated.read_text(), text)

    def test_cmake_rejects_unrecognized_upstream(self):
        _, result = self.configure_fixture(missing=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('no longer matches cesium-native', result.stderr)

    def test_runtime_and_shader_mapping_keys_agree(self):
        renderer = (ROOT / 'Source/Runtime/CesiumInstancedRenderer.cs').read_text()
        shader = (RESOURCE / 'CesiumInstancedRaster.hlsl').read_text()
        for key in ('WorldToChart', 'Geodetic', 'RasterMapping', 'RasterProjection', 'Coverage', 'ClipCoverage'):
            self.assertIn('_CesiumInstance' + key, renderer)
            self.assertIn('_CesiumInstance' + key, shader)
        self.assertIn('clip(-1.0)', shader)
        self.assertIn('CoordinateIndex <= -2.0', shader)

if __name__ == '__main__':
    unittest.main(verbosity=2)
