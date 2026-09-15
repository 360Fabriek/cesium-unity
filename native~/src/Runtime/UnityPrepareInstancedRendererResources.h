#pragma once

#include "UnityPrepareRendererResources.h"

namespace CesiumForUnityNative {

/** Reuses the v4 loader/material ownership, but constructs one prototype per
 * source primitive and transfers placements to the managed batched renderer. */
class UnityPrepareInstancedRendererResources final
    : public UnityPrepareRendererResources {
public:
  explicit UnityPrepareInstancedRendererResources(
      const DotNet::UnityEngine::GameObject& tileset);

  CesiumAsync::Future<Cesium3DTilesSelection::TileLoadResultAndRenderResources>
  prepareInLoadThread(
      const CesiumAsync::AsyncSystem& asyncSystem,
      Cesium3DTilesSelection::TileLoadResult&& result,
      const glm::dmat4& transform,
      const std::any& options) override;

  void* prepareInMainThread(Cesium3DTilesSelection::Tile& tile, void* result) override;

  void free(Cesium3DTilesSelection::Tile& tile, void* loadResources,
      void* mainResources) noexcept override;

  void attachRasterInMainThread(
      const Cesium3DTilesSelection::Tile& tile,
      int32_t coordinate,
      const CesiumRasterOverlays::RasterOverlayTile& raster,
      void* resources,
      const glm::dvec2& translation,
      const glm::dvec2& scale) override;

  void detachRasterInMainThread(
      const Cesium3DTilesSelection::Tile& tile,
      int32_t coordinate,
      const CesiumRasterOverlays::RasterOverlayTile& raster,
      void* resources) noexcept override;

private:
  bool _enabled;
  glm::dvec3 _radii;
};
} // namespace CesiumForUnityNative
