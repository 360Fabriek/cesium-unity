#!/usr/bin/env python3
"""Offline CMake-adapter execution and source-contract regression checks.

The CMake fixture contains the exact upstream replacement contexts, NOT the full
Native source. These tests do not compile C++, C#, Shader Graphs or Unity scenes.
Run tools~/test_projection.py separately for compiled float projection checks.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT / "native~/cmake/PatchInstancedRasterUpsampling.cmake"
COORDINATE_ANCHOR = "  const Ellipsoid& ellipsoid = getProjectionEllipsoid(projections.front());"
UPSAMPLE_ANCHOR = '  CESIUM_TRACE("upsampleGltfForRasterOverlays");\n  Model result;'
# Exact contexts from Native 80a22ff, embedded in a deliberately minimal fixture.
CONTEXT_FIXTURE = ("#include <CesiumGeometry/QuadtreeTileID.h>\n"
                   "void coordinateContext() {\n" + COORDINATE_ANCHOR + "\n}\n"
                   "void upsampleContext() {\n" + UPSAMPLE_ANCHOR + "\n}\n")


@unittest.skipUnless(shutil.which("cmake"), "CMake is required")
class AdapterExecution(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "extern/cesium-native/CesiumRasterOverlays/src/RasterOverlayUtilities.cpp"
        self.source.parent.mkdir(parents=True)
        (self.root / "cmake").mkdir()
        shutil.copyfile(ADAPTER, self.root / "cmake/PatchInstancedRasterUpsampling.cmake")
        self.build = self.root / "build"
        self.output = self.build / "instanced-raster/RasterOverlayUtilities.cpp"

    def tearDown(self):
        self.temporary.cleanup()

    def configure(self, text=CONTEXT_FIXTURE, duplicate_target=False, missing_target=False):
        self.source.write_bytes(text.encode())
        target_source = self.source.relative_to(self.root).as_posix()
        if missing_target:
            (self.root / "Other.cpp").write_text("int unrelated;\n")
            target_source = "Other.cpp"
        sources = target_source + (" " + target_source if duplicate_target else "")
        (self.root / "CMakeLists.txt").write_text(
            "cmake_minimum_required(VERSION 3.20)\n"
            "project(InstancedAdapterContextFixture LANGUAGES CXX)\n"
            f"add_library(CesiumRasterOverlays STATIC {sources})\n"
            "include(cmake/PatchInstancedRasterUpsampling.cmake)\n"
            "get_target_property(final_sources CesiumRasterOverlays SOURCES)\n"
            'file(WRITE "${CMAKE_BINARY_DIR}/sources.txt" "${final_sources}")\n')
        return subprocess.run(["cmake", "-S", str(self.root), "-B", str(self.build)],
                              text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)

    def test_line_endings_source_ownership_and_idempotence(self):
        for ending in ("\n", "\r\n"):
            with self.subTest(line_ending=repr(ending)):
                text = CONTEXT_FIXTURE.replace("\n", ending)
                result = self.configure(text)
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertEqual(text.encode(), self.source.read_bytes(), "Submodule source was modified")
                generated = self.output.read_bytes()
                self.assertNotIn(b"\r", generated)
                self.assertIn(b"CesiumUnityInstanceRasterChild", generated)
                self.assertIn(b"const GlobeRectangle coverage = globeRectangle", generated)
                self.assertEqual(str(self.output), (self.build / "sources.txt").read_text())
                again = self.configure(text)
                self.assertEqual(0, again.returncode, again.stdout)
                self.assertEqual(generated, self.output.read_bytes())

    def test_missing_source_context_fails_closed(self):
        result = self.configure(CONTEXT_FIXTURE.replace(COORDINATE_ANCHOR, "// changed upstream"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected exactly one source context", result.stdout)

    def test_duplicate_source_context_fails_closed(self):
        result = self.configure(CONTEXT_FIXTURE + COORDINATE_ANCHOR + "\n")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected exactly one source context", result.stdout)

    def test_duplicate_target_source_fails_closed(self):
        result = self.configure(duplicate_target=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Expected exactly one Native", result.stdout)

    def test_missing_target_source_fails_closed(self):
        result = self.configure(missing_target=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Expected exactly one Native", result.stdout)

    def test_generated_uv_fast_path_returns_before_legacy_generation(self):
        result = self.configure()
        self.assertEqual(0, result.returncode, result.stdout)
        generated = self.output.read_text()
        mapping = generated[generated.index("auto marker = model.extras"):generated.index("void upsampleContext")]
        self.assertIn("return RasterOverlayDetails", mapping)
        self.assertIn("std::move(projections)", mapping)
        self.assertNotIn("model.accessors", mapping)
        self.assertNotIn("model.buffers", mapping)
        self.assertIn("Model instancedChild(parentModel);", generated)
        self.assertIn("  Model result;", generated, "The ordinary upsampler must remain")


class IntegrationContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.renderer = (ROOT / "Source/Runtime/CesiumInstancedRenderer.cs").read_text()
        cls.integration = (ROOT / "Source/Runtime/CesiumInstancedRenderer.Integration.cs").read_text()
        cls.native = (ROOT / "native~/src/Runtime/UnityPrepareInstancedRendererResources.cpp").read_text()
        cls.math = (ROOT / "Source/Runtime/CesiumInstanceMath.cs").read_text()

    def test_native_release_precedes_base_resource_pooling(self):
        free = self.native[self.native.index("void UnityPrepareInstancedRendererResources::free("):]
        free = free[:free.index("void UnityPrepareInstancedRendererResources::attachRasterInMainThread")]
        self.assertLess(free.index("CesiumInstancedRenderer::Release"),
                        free.index("UnityPrepareRendererResources::free"))
        self.assertIn("this->free(tile, nullptr, main);", self.native)
        self.assertIn("Release(go);", self.renderer, "Reinterop must expose the new release barrier")

    def test_existing_loader_handoff_and_gameobjects_are_retained(self):
        self.assertIn("UnityPrepareRendererResources::prepareInMainThread(tile, load->baseResources)", self.native)
        self.assertIn("pending->removedExtensions.clear()", self.native)
        self.assertNotIn("static_cast<LoadThreadResult", self.native)
        self.assertIn("primitiveInfoByGameObject.find", self.native)
        self.assertNotIn("CesiumInstancedTileRenderer", self.native + self.renderer + self.integration)

    def test_borrowed_assets_not_destroyed_by_release(self):
        release = self.integration[self.integration.index("private void ReleaseRuntime()"):]
        self.assertNotIn(".Destroy(", release)
        self.assertNotIn("DestroyImmediate(", release)
        self.assertIn("_batches.Clear();", release)
        self.assertIn("_properties.Clear();", release)
        self.assertIn("_instances = Array.Empty<double4x4>();", release)
        self.assertIn("_released = true;", release)
        self.assertIn("if (_released) return;", release)

    def test_clipping_membership_rebuild_uses_full_bounds(self):
        self.assertIn("CesiumInstanceMath.IntersectsCoverage", self.renderer)
        self.assertIn("_baseEcef.Equals(_originalBaseEcef)", self.integration)
        self.assertIn("_mesh.bounds.Equals(_originalMeshBounds)", self.integration)
        self.assertIn("if (placementChanged || boundsChanged)", self.renderer)
        self.assertIn("_batches.Clear();", self.integration)
        self.assertIn("parent.Equals(_lastParent)", self.renderer)

    def test_raster_validation_is_transactional(self):
        setter = self.renderer[self.renderer.index("internal void SetRaster("):]
        self.assertLess(setter.index("CesiumInstanceMath.RasterMapping"), setter.index("overlay.texture = texture"))
        self.assertNotIn("SetVector(CoverageId", setter[:setter.index("internal static void DetachRaster")])
        self.assertIn("math.isfinite(rectangle)", self.math)
        self.assertIn("finite shader parameters", self.math)

    def test_component_workflows_keep_the_legacy_path(self):
        self.assertIn("GetComponentInParent<CesiumMetadata>()", self.renderer)
        self.assertIn("hasExtension<CesiumGltf::ExtensionExtMeshFeatures>()", self.native)
        self.assertIn("primitive.targets.empty()", self.native)
        self.assertIn('material->alphaMode != "BLEND"', self.native)

    def test_prototype_overrides_and_visibility_survive_integration(self):
        self.assertIn("source.GetPropertyBlock(_properties);", self.renderer)
        self.assertIn("_sourceRenderer.forceRenderingOff", self.renderer)
        self.assertIn("parameters.shadowCastingMode = _sourceRenderer.shadowCastingMode", self.renderer)
        self.assertIn("Graphics.RenderMeshInstanced", self.renderer)
        self.assertIn("Graphics.RenderMesh(parameters", self.renderer)


if __name__ == "__main__":
    unittest.main(verbosity=2)
