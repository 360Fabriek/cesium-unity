#include "GpuInstancingRasterOverlayTileExcluder.h"

#include <Cesium3DTilesSelection/Tile.h>
#include <Cesium3DTilesSelection/TileContent.h>
#include <CesiumGltf/ExtensionExtMeshGpuInstancing.h>

#include <variant>

namespace CesiumForUnityNative {
namespace {

bool hasGpuInstancingNodes(
    const Cesium3DTilesSelection::TileRenderContent& renderContent) {
  const CesiumGltf::Model& model = renderContent.getModel();
  for (const CesiumGltf::Node& node : model.nodes) {
    const auto* pGpuInstancing =
        node.getExtension<CesiumGltf::ExtensionExtMeshGpuInstancing>();
    if (pGpuInstancing && !pGpuInstancing->attributes.empty()) {
      return true;
    }
  }

  return false;
}

} // namespace

bool GpuInstancingRasterOverlayTileExcluder::shouldExclude(
    const Cesium3DTilesSelection::Tile& tile) const noexcept {
  if (!std::holds_alternative<CesiumGeometry::UpsampledQuadtreeNode>(
          tile.getTileID())) {
    return false;
  }

  const Cesium3DTilesSelection::Tile* pParent = tile.getParent();
  if (pParent == nullptr) {
    return false;
  }

  const Cesium3DTilesSelection::TileRenderContent* pRenderContent =
      pParent->getContent().getRenderContent();
  return pRenderContent != nullptr && hasGpuInstancingNodes(*pRenderContent);
}

} // namespace CesiumForUnityNative
