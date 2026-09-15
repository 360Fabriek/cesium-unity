#include "UnityPrepareInstancedRendererResources.h"
#include "UnityTransforms.h"

#include <Cesium3DTilesSelection/BoundingVolume.h>
#include <Cesium3DTilesSelection/Tile.h>
#include <CesiumGeospatial/BoundingRegionBuilder.h>
#include <CesiumGeospatial/WebMercatorProjection.h>
#include <CesiumGltfContent/GltfUtilities.h>
#include <CesiumGltf/ExtensionExtMeshFeatures.h>
#include <CesiumGltf/Material.h>
#include <CesiumRasterOverlays/RasterOverlay.h>
#include <CesiumRasterOverlays/RasterOverlayTile.h>
#include <CesiumRasterOverlays/RasterOverlayTileProvider.h>
#include <CesiumRasterOverlays/RasterOverlayUtilities.h>
#include <CesiumUtility/JsonValue.h>
#include <CesiumUtility/ScopeGuard.h>

#include <DotNet/CesiumForUnity/CesiumEllipsoid.h>
#include <DotNet/CesiumForUnity/CesiumGeoreference.h>
#include <DotNet/CesiumForUnity/CesiumInstancedRenderer.h>
#include <DotNet/CesiumForUnity/Helpers.h>
#include <DotNet/System/Array1.h>
#include <DotNet/System/Object.h>
#include <DotNet/System/String.h>
#include <DotNet/Unity/Mathematics/double3.h>
#include <DotNet/Unity/Mathematics/double4.h>
#include <DotNet/Unity/Mathematics/double4x4.h>
#include <DotNet/UnityEngine/Debug.h>
#include <DotNet/UnityEngine/Object.h>
#include <DotNet/UnityEngine/Texture.h>
#include <DotNet/UnityEngine/Transform.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>
#include <type_traits>
#include <unordered_map>

using namespace DotNet;
using namespace CesiumForUnityNative;
using namespace Cesium3DTilesSelection;

namespace {
// Own only our wrapper. The base renderer's hand-off remains opaque; it is
// returned to that renderer unchanged in both the main-thread and abort paths.
struct BatchedLoadResult {
  void* baseResources = nullptr;
  bool batched = false;
  std::vector<std::shared_ptr<const GltfInstanceTransforms>> instances;
  std::vector<std::pair<size_t, std::any>> removedExtensions;
};

constexpr const char* enabledKey = "CesiumUnityBatchedInstances";
constexpr const char* childKey = "CesiumUnityInstanceRasterChild";
constexpr const char* boundsKey = "CesiumUnityInstanceBounds";

bool flag(const CesiumGltf::Model& model, const char* key) {
  auto it = model.extras.find(key);
  if (it == model.extras.end()) return false;
  const bool* value = std::get_if<bool>(&it->second.value);
  return value && *value;
}

bool eligible(const CesiumGltf::Model& model) {
  // Keep component-based metadata/picking, skinning and morph workflows on
  // the existing GameObject path. The batching adapter does not implement them.
  if (!model.animations.empty() || !model.skins.empty()) return false;
  bool hasInstances = false;
  bool supported = true;
  model.forEachPrimitiveInScene(-1, [&](const auto& gltf, const auto& node,
      const auto&, const auto& primitive, const auto&) {
    hasInstances |= node.template hasExtension<CesiumGltf::ExtensionExtMeshGpuInstancing>();
    const auto* material = CesiumGltf::Model::getSafe(&gltf.materials, primitive.material);
    supported &= primitive.mode == CesiumGltf::MeshPrimitive::Mode::TRIANGLES &&
        node.skin < 0 && primitive.targets.empty() &&
        !primitive.template hasExtension<CesiumGltf::ExtensionExtMeshFeatures>() &&
        (!material || material->alphaMode != "BLEND");
  });
  return hasInstances && supported;
}

std::optional<CesiumGeospatial::BoundingRegion> cachedRegion(
    const CesiumGltf::Model& model, const CesiumGeospatial::Ellipsoid& ellipsoid) {
  auto it = model.extras.find(boundsKey);
  if (it == model.extras.end()) return std::nullopt;
  const auto* values = std::get_if<std::vector<CesiumUtility::JsonValue>>(&it->second.value);
  if (!values || values->size() != 6) return std::nullopt;
  double v[6];
  for (size_t i = 0; i < 6; ++i) {
    const double* p = std::get_if<double>(&(*values)[i].value);
    if (!p || !std::isfinite(*p)) return std::nullopt;
    v[i] = *p;
  }
  if (v[1] > v[3] || v[4] > v[5]) return std::nullopt;
  return CesiumGeospatial::BoundingRegion(
      CesiumGeospatial::GlobeRectangle(v[0], v[1], v[2], v[3]), v[4], v[5], ellipsoid);
}

std::optional<CesiumGeospatial::BoundingRegion> computeRegion(
    CesiumGltf::Model& model, const glm::dmat4& tileTransform,
    const CesiumGeospatial::Ellipsoid& ellipsoid) {
  if (flag(model, enabledKey)) {
    auto cached = cachedRegion(model, ellipsoid);
    if (cached) return cached;
  }
  glm::dmat4 root = CesiumGltfContent::GltfUtilities::applyRtcCenter(model, tileTransform);
  root = CesiumGltfContent::GltfUtilities::applyGltfUpAxisTransform(model, root);
  CesiumGeospatial::BoundingRegionBuilder builder;
  bool anyPosition = false;
  std::unordered_map<const CesiumGltf::Node*, GltfInstanceTransforms> decoded;
  model.forEachPrimitiveInScene(-1, [&](const auto& gltf, const auto& node,
      const auto&, const auto& primitive, const glm::dmat4& nodeTransform) {
    auto it = primitive.attributes.find("POSITION");
    if (it == primitive.attributes.end()) return;
    CesiumGltf::AccessorView<glm::vec3> positions(gltf, it->second);
    if (positions.status() != CesiumGltf::AccessorViewStatus::Valid) return;
    const GltfInstanceTransforms* instances = nullptr;
    if (const auto* extension = node.template getExtension<CesiumGltf::ExtensionExtMeshGpuInstancing>()) {
      auto [entry, inserted] = decoded.try_emplace(&node);
      if (inserted) entry->second = readGltfInstanceTransforms(gltf, *extension);
      instances = &entry->second;
      if (!instances->error.empty()) return;
    }
    const size_t count = instances ? instances->transforms.size() : 1;
    for (size_t i = 0; i < count; ++i) {
      glm::dmat4 m = root * nodeTransform * (instances ? instances->transforms[i] : glm::dmat4(1.0));
      for (int64_t vertex = 0; vertex < positions.size(); ++vertex) {
        auto cartographic = ellipsoid.cartesianToCartographic(glm::dvec3(m * glm::dvec4(positions[vertex], 1.0)));
        if (cartographic) {
          builder.expandToIncludePosition(*cartographic);
          anyPosition = true;
        }
      }
    }
  });
  if (!anyPosition) return std::nullopt;
  auto region = builder.toRegion(ellipsoid);
  const auto& r = region.getRectangle();
  model.extras[boundsKey] = std::vector<CesiumUtility::JsonValue>{
      r.getWest(), r.getSouth(), r.getEast(), r.getNorth(),
      region.getMinimumHeight(), region.getMaximumHeight()};
  return region;
}

// Correct the geographic footprint before the result is installed on the tile.
// Prototype UVs generated by Native are not used by the batched renderer.
bool prepareMappings(TileLoadResult& result, const glm::dmat4& transform) {
  auto* model = std::get_if<CesiumGltf::Model>(&result.contentKind);
  if (!model || !eligible(*model)) return false;
  auto region = computeRegion(*model, transform, result.ellipsoid);
  if (!region) return false;
  if (flag(*model, childKey)) {
    const auto* childRegion = result.initialBoundingVolume
        ? getBoundingRegionFromBoundingVolume(*result.initialBoundingVolume) : nullptr;
    if (!childRegion) throw std::runtime_error("Instanced raster child has no geographic coverage bounds.");
    region = CesiumGeospatial::BoundingRegion(childRegion->getRectangle(),
        region->getMinimumHeight(), region->getMaximumHeight(), result.ellipsoid);
  }
  std::vector<CesiumGeospatial::Projection> projections;
  if (result.rasterOverlayDetails) projections = result.rasterOverlayDetails->rasterOverlayProjections;
  // Every primitive in this model uses placed-position mapping, including
  // non-instanced primitives in a mixed composite. Do not generate another set
  // of prototype UV buffers: they cannot represent the placements and would
  // grow the model at every raster refinement level.
  std::vector<CesiumGeometry::Rectangle> rectangles;
  rectangles.reserve(projections.size());
  for (const auto& projection : projections)
    rectangles.emplace_back(CesiumGeospatial::projectRectangleSimple(projection, region->getRectangle()));
  result.rasterOverlayDetails = CesiumRasterOverlays::RasterOverlayDetails{
      std::move(projections), std::move(rectangles), *region};
  // Native's prototype-only computed bounds must not replace the declared tile bounds.
  result.updatedBoundingVolume = result.initialBoundingVolume
      ? result.initialBoundingVolume : std::optional<BoundingVolume>(*region);
  result.updatedContentBoundingVolume = *region;
  model->extras[enabledKey] = true;
  return true;
}

CesiumGltfGameObject* renderObject(const Tile& tile) {
  const auto* content = tile.getContent().getRenderContent();
  return content ? static_cast<CesiumGltfGameObject*>(content->getRenderResources()) : nullptr;
}
} // namespace

UnityPrepareInstancedRendererResources::UnityPrepareInstancedRendererResources(
    const UnityEngine::GameObject& tileset)
    : UnityPrepareRendererResources(tileset), _enabled(false), _radii(1.0) {
  auto component = tileset.GetComponent<CesiumForUnity::Cesium3DTileset>();
  _enabled = CesiumForUnity::CesiumInstancedRenderer::CanUse(component);
  if (_enabled) {
    auto georeference = tileset.GetComponentInParent<CesiumForUnity::CesiumGeoreference>();
    _radii = UnityTransforms::fromUnity(georeference.ellipsoid().radii());
  }
}

CesiumAsync::Future<TileLoadResultAndRenderResources>
UnityPrepareInstancedRendererResources::prepareInLoadThread(
    const CesiumAsync::AsyncSystem& asyncSystem, TileLoadResult&& result,
    const glm::dmat4& transform, const std::any& options) {
  auto pending = std::make_shared<BatchedLoadResult>();
  pending->batched = _enabled && prepareMappings(result, transform);
  if (pending->batched) {
    auto& model = std::get<CesiumGltf::Model>(result.contentKind);
    std::unordered_map<const CesiumGltf::Node*, std::shared_ptr<const GltfInstanceTransforms>> decoded;
    model.forEachPrimitiveInScene(-1, [&](const auto& gltf, const auto& node,
        const auto&, const auto&, const auto&) {
      std::shared_ptr<const GltfInstanceTransforms> instances;
      if (const auto* extension = node.template getExtension<CesiumGltf::ExtensionExtMeshGpuInstancing>()) {
        auto& entry = decoded[&node];
        if (!entry) entry = std::make_shared<const GltfInstanceTransforms>(readGltfInstanceTransforms(gltf, *extension));
        instances = entry;
      }
      pending->instances.emplace_back(std::move(instances));
    });
    // The base loader should prepare one prototype per primitive. Suppress the
    // instance extension only while its private mesh loader is running, then
    // restore it before publishing the model to Native. No base-private casts.
    for (size_t i = 0; i < model.nodes.size(); ++i) {
      auto it = model.nodes[i].extensions.find("EXT_mesh_gpu_instancing");
      if (it != model.nodes[i].extensions.end()) {
        pending->removedExtensions.emplace_back(i, std::move(it->second));
        model.nodes[i].extensions.erase(it);
      }
    }
  }
  return UnityPrepareRendererResources::prepareInLoadThread(asyncSystem, std::move(result), transform, options)
      .thenInMainThread([pending](TileLoadResultAndRenderResources&& ready) {
        if (pending->batched) {
          auto& model = std::get<CesiumGltf::Model>(ready.result.contentKind);
          for (auto& entry : pending->removedExtensions)
            model.nodes[entry.first].extensions.emplace("EXT_mesh_gpu_instancing", std::move(entry.second));
          pending->removedExtensions.clear();
        }
        if (ready.pRenderResources) {
          ready.pRenderResources = new BatchedLoadResult{
              ready.pRenderResources, pending->batched, std::move(pending->instances), {}};
        }
        return std::move(ready);
      });
}

void* UnityPrepareInstancedRendererResources::prepareInMainThread(Tile& tile, void* result) {
  if (!result) return nullptr;
  std::unique_ptr<BatchedLoadResult> load(static_cast<BatchedLoadResult*>(result));
  if (!load->batched)
    return UnityPrepareRendererResources::prepareInMainThread(tile, load->baseResources);
  const auto* content = tile.getContent().getRenderContent();
  auto original = std::move(load->instances);
  auto* main = static_cast<CesiumGltfGameObject*>(
      UnityPrepareRendererResources::prepareInMainThread(tile, load->baseResources));
  if (!main || !main->pGameObject) return main;
  CesiumUtility::ScopeGuard cleanup([&]() {
    // This goes through our release barrier before the base returns shared
    // meshes to its pool, including a partially configured prototype hierarchy.
    this->free(tile, nullptr, main);
  });
  if (original.size() != main->primitiveInfos.size())
    throw std::runtime_error("Instanced primitive mapping changed during mesh preparation.");
  for (size_t i = 0; i < original.size(); ++i) main->primitiveInfos[i].pInstanceTransforms = original[i];
  const std::vector<glm::dmat4> identity{glm::dmat4(1.0)};
  const auto& region = content->getRasterOverlayDetails().boundingRegion.getRectangle();
  const auto rectangle = UnityTransforms::toUnityMathematics(glm::dvec4(
      region.getWest(), region.getSouth(), region.getEast(), region.getNorth()));
  const auto reference = cachedRegion(content->getModel(), CesiumGeospatial::Ellipsoid(_radii));
  const auto& referenceBounds = reference ? reference->getRectangle() : region;
  const auto referenceRectangle = UnityTransforms::toUnityMathematics(glm::dvec4(
      referenceBounds.getWest(), referenceBounds.getSouth(), referenceBounds.getEast(), referenceBounds.getNorth()));
  const auto& coverageBox = content->getRasterOverlayDetails().boundingRegion.getBoundingBox();
  const auto& axes = coverageBox.getHalfAxes();
  const auto coverageBoxToEcef = UnityTransforms::toUnityMathematics(glm::dmat4(
      glm::dvec4(axes[0], 0.0), glm::dvec4(axes[1], 0.0), glm::dvec4(axes[2], 0.0),
      glm::dvec4(coverageBox.getCenter(), 1.0)));
  UnityEngine::Transform root = main->pGameObject->transform();
  static_assert(sizeof(glm::dmat4) == 16 * sizeof(double));
  for (int32_t i = 0; i < root.childCount(); ++i) {
    auto child = root.GetChild(i).gameObject();
    auto found = main->primitiveInfoByGameObject.find(CesiumForUnity::Helpers::GetObjectId(child));
    if (found == main->primitiveInfoByGameObject.end()) continue;
    const auto& instances = original[found->second];
    const auto& matrices = instances ? instances->transforms : identity;
    if (matrices.empty() || matrices.size() > size_t(std::numeric_limits<int32_t>::max()) ||
        (instances && !instances->error.empty())) {
      child.SetActive(false);
      if (instances && !instances->error.empty())
        UnityEngine::Debug::LogWarning(System::String(instances->error));
      continue;
    }
    CesiumForUnity::CesiumInstancedRenderer::Configure(child,
        static_cast<int64_t>(reinterpret_cast<intptr_t>(matrices.data())),
        static_cast<int32_t>(matrices.size()), UnityTransforms::toUnityMathematics(_radii),
        rectangle, referenceRectangle, coverageBoxToEcef, flag(content->getModel(), childKey));
  }
  cleanup.release();
  return main;
}

void UnityPrepareInstancedRendererResources::free(Tile& tile, void* loadResources, void* mainResources) noexcept {
  std::unique_ptr<BatchedLoadResult> load(static_cast<BatchedLoadResult*>(loadResources));
  try {
    auto* object = static_cast<CesiumGltfGameObject*>(mainResources);
    if (object && object->pGameObject && *object->pGameObject != nullptr)
      CesiumForUnity::CesiumInstancedRenderer::Release(*object->pGameObject);
  } catch (...) {
    // Managed objects may already be gone during an AppDomain reload. Native
    // ownership must still be released through the original renderer.
  }
  UnityPrepareRendererResources::free(tile, load ? load->baseResources : nullptr, mainResources);
}

void UnityPrepareInstancedRendererResources::attachRasterInMainThread(
    const Tile& tile, int32_t coordinate, const CesiumRasterOverlays::RasterOverlayTile& raster,
    void* resources, const glm::dvec2& translation, const glm::dvec2& scale) {
  const auto* content = tile.getContent().getRenderContent();
  if (!_enabled || !content || !flag(content->getModel(), enabledKey)) {
    UnityPrepareRendererResources::attachRasterInMainThread(tile, coordinate, raster, resources, translation, scale);
    return;
  }
  // Raster resources are borrowed by property blocks, never owned by materials.
  auto* object = renderObject(tile);
  auto* texture = static_cast<UnityEngine::Texture*>(resources);
  if (!object || !object->pGameObject || *object->pGameObject == nullptr ||
      !texture || *texture == nullptr) return;
  const auto& rectangle = raster.getRectangle();
  const auto& projection = raster.getTileProvider().getProjection();
  CesiumForUnity::CesiumInstancedRenderer::AttachRaster(*object->pGameObject,
      System::String(raster.getOverlay().getName()), *texture,
      UnityTransforms::toUnityMathematics(glm::dvec4(rectangle.minimumX, rectangle.minimumY,
          rectangle.maximumX, rectangle.maximumY)),
      std::holds_alternative<CesiumGeospatial::WebMercatorProjection>(projection),
      CesiumGeospatial::getProjectionEllipsoid(projection).getMaximumRadius());
}

void UnityPrepareInstancedRendererResources::detachRasterInMainThread(
    const Tile& tile, int32_t coordinate, const CesiumRasterOverlays::RasterOverlayTile& raster,
    void* resources) noexcept {
  const auto* content = tile.getContent().getRenderContent();
  if (!_enabled || !content || !flag(content->getModel(), enabledKey)) {
    UnityPrepareRendererResources::detachRasterInMainThread(tile, coordinate, raster, resources);
    return;
  }
  try {
    auto* object = renderObject(tile);
    auto* texture = static_cast<UnityEngine::Texture*>(resources);
    if (_enabled && object && object->pGameObject && *object->pGameObject != nullptr && texture)
      CesiumForUnity::CesiumInstancedRenderer::DetachRaster(*object->pGameObject,
          System::String(raster.getOverlay().getName()), *texture);
  } catch (...) { /* AppDomain reload: the native overlay still owns the texture. */ }
}
