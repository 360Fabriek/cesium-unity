#pragma once

#include <Cesium3DTilesSelection/ITileExcluder.h>

namespace CesiumForUnityNative {

class GpuInstancingRasterOverlayTileExcluder
    : public Cesium3DTilesSelection::ITileExcluder {
public:
  virtual void startNewFrame() noexcept override {}
  virtual bool shouldExclude(
      const Cesium3DTilesSelection::Tile& tile) const noexcept override;
};

} // namespace CesiumForUnityNative
