#include "Cesium3DTilesetImpl.h"

#include "CameraManager.h"
#include "CesiumEllipsoidImpl.h"
#include "CesiumIonServerHelper.h"
#include "UnityPrepareRendererResources.h"
#include "UnityTileExcluderAdaptor.h"
#include "UnityTilesetExternals.h"

#include <Cesium3DTilesSelection/BoundingVolume.h>
#include <Cesium3DTilesSelection/EllipsoidTilesetLoader.h>
#include <Cesium3DTilesSelection/TileContent.h>
#include <Cesium3DTilesSelection/Tileset.h>
#include <CesiumGeospatial/GlobeTransforms.h>
#include <CesiumIonClient/Connection.h>
#include <CesiumRasterOverlays/IonRasterOverlay.h>
#include <CesiumUtility/Math.h>

#include <DotNet/CesiumForUnity/Cesium3DTileset.h>
#include <DotNet/CesiumForUnity/Cesium3DTilesetLoadFailureDetails.h>
#include <DotNet/CesiumForUnity/Cesium3DTilesetLoadType.h>
#include <DotNet/CesiumForUnity/CesiumCameraManager.h>
#include <DotNet/CesiumForUnity/CesiumDataSource.h>
#include <DotNet/CesiumForUnity/CesiumEllipsoid.h>
#include <DotNet/CesiumForUnity/CesiumGeoreference.h>
#include <DotNet/CesiumForUnity/CesiumIonServer.h>
#include <DotNet/CesiumForUnity/CesiumRasterOverlay.h>
#include <DotNet/CesiumForUnity/CesiumSampleHeightResult.h>
#include <DotNet/CesiumForUnity/CesiumTileExcluder.h>
#include <DotNet/Reinterop/ReinteropNativeException.h>
#include <DotNet/CesiumForUnity/I3dmInstanceRenderer.h>
#include <DotNet/System/Exception.h>
#include <DotNet/System/Object.h>
#include <DotNet/System/String.h>
#include <DotNet/System/Threading/Tasks/Task1.h>
#include <DotNet/System/Threading/Tasks/TaskCompletionSource1.h>
#include <DotNet/Unity/Mathematics/double3.h>
#include <DotNet/UnityEngine/Application.h>
#include <DotNet/UnityEngine/Camera.h>
#include <DotNet/UnityEngine/Component.h>
#include <DotNet/UnityEngine/Debug.h>
#include <DotNet/UnityEngine/Experimental/Rendering/FormatUsage.h>
#include <DotNet/UnityEngine/Experimental/Rendering/GraphicsFormat.h>
#include <DotNet/UnityEngine/GameObject.h>
#include <DotNet/UnityEngine/Material.h>
#include <DotNet/UnityEngine/Object.h>
#include <DotNet/UnityEngine/Quaternion.h>
#include <DotNet/UnityEngine/SystemInfo.h>
#include <DotNet/UnityEngine/Time.h>
#include <DotNet/UnityEngine/Transform.h>
#include <DotNet/UnityEngine/Vector3.h>

#include <string>
#include <unordered_map>
#include <unordered_set>
#include <variant>

#if UNITY_EDITOR
#include <DotNet/UnityEditor/CallbackFunction.h>
#include <DotNet/UnityEditor/EditorApplication.h>
#include <DotNet/UnityEditor/EditorUtility.h>
#include <DotNet/UnityEditor/SceneView.h>
#endif

using namespace Cesium3DTilesSelection;
using namespace DotNet;

namespace CesiumForUnityNative {
namespace {

const TileRenderContent* getRenderContent(const Tile* pTile) {
  if (!pTile) {
    return nullptr;
  }

  // A tile may leave the Done state before its Unity render resources are
  // actually freed on the main thread. We still need access to those resources
  // so they can be deactivated immediately when the tile drops out of the
  // selected set.
  return pTile->getContent().getRenderContent();
}

CesiumGltfGameObject* getTileGameObject(const Tile* pTile) {
  const TileRenderContent* pRenderContent = getRenderContent(pTile);
  if (!pRenderContent) {
    return nullptr;
  }

  CesiumGltfGameObject* pCesiumGameObject =
      static_cast<CesiumGltfGameObject*>(pRenderContent->getRenderResources());
  if (!pCesiumGameObject || !pCesiumGameObject->pGameObject) {
    return nullptr;
  }

  return pCesiumGameObject;
}

bool hasI3dmInstanceRenderer(const Tile* pTile) {
  CesiumGltfGameObject* pCesiumGameObject = getTileGameObject(pTile);
  if (!pCesiumGameObject) {
    return false;
  }

  return pCesiumGameObject->pGameObject
             ->GetComponent<CesiumForUnity::I3dmInstanceRenderer>() != nullptr;
}

CesiumForUnity::I3dmInstanceRenderer getI3dmInstanceRenderer(const Tile* pTile) {
  CesiumGltfGameObject* pCesiumGameObject = getTileGameObject(pTile);
  if (!pCesiumGameObject) {
    return nullptr;
  }

  return pCesiumGameObject->pGameObject
      ->GetComponent<CesiumForUnity::I3dmInstanceRenderer>();
}

std::string getTileUrl(const Tile* pTile) {
  const TileRenderContent* pRenderContent = getRenderContent(pTile);
  if (!pRenderContent) {
    return std::string();
  }

  const auto urlIt =
      pRenderContent->getModel().extras.find("Cesium3DTiles_TileUrl");
  if (urlIt == pRenderContent->getModel().extras.end()) {
    return std::string();
  }

  return urlIt->second.getStringOrDefault("");
}

std::string getBaseTileUrl(const Tile* pTile) {
  std::string url = getTileUrl(pTile);
  const std::string::size_type upsampledIndex = url.find(" upsampled");
  if (upsampledIndex != std::string::npos) {
    url.resize(upsampledIndex);
  }

  return url;
}

const Tile* resolveActivationTile(const Tile* pTile) {
  if (!pTile || !std::holds_alternative<CesiumGeometry::UpsampledQuadtreeNode>(
                    pTile->getTileID())) {
    return pTile;
  }

  const std::string selectedBaseUrl = getBaseTileUrl(pTile);
  if (selectedBaseUrl.empty()) {
    return pTile;
  }

  const Tile* pAncestor = pTile->getParent();
  while (pAncestor) {
    if (hasI3dmInstanceRenderer(pAncestor) &&
        getBaseTileUrl(pAncestor) == selectedBaseUrl) {
      return pAncestor;
    }

    pAncestor = pAncestor->getParent();
  }

  return pTile;
}

struct TileSelectionBounds {
  double west;
  double south;
  double east;
  double north;
  bool wrapsLongitude;
};

std::optional<TileSelectionBounds>
getTileSelectionBounds(const Tile& tile) {
  std::optional<CesiumGeospatial::GlobeRectangle> globeRectangle =
      estimateGlobeRectangle(tile.getBoundingVolume());
  if (!globeRectangle) {
    return std::nullopt;
  }

  return TileSelectionBounds{
      CesiumUtility::Math::radiansToDegrees(globeRectangle->getWest()),
      CesiumUtility::Math::radiansToDegrees(globeRectangle->getSouth()),
      CesiumUtility::Math::radiansToDegrees(globeRectangle->getEast()),
      CesiumUtility::Math::radiansToDegrees(globeRectangle->getNorth()),
      globeRectangle->getEast() < globeRectangle->getWest()};
}

void updateI3dmTileSelectionBounds(
    const std::unordered_set<Tile::ConstPointer>& tilesToActivate,
    const std::unordered_map<const Tile*, std::vector<TileSelectionBounds>>&
        forwardedBoundsByTile) {
  for (Tile::ConstPointer pTile : tilesToActivate) {
    CesiumForUnity::I3dmInstanceRenderer renderer =
        getI3dmInstanceRenderer(pTile);
    if (renderer == nullptr) {
      continue;
    }

    renderer.ClearTileSelectionBounds();

    auto boundsIt = forwardedBoundsByTile.find(pTile);
    if (boundsIt == forwardedBoundsByTile.end()) {
      continue;
    }

    for (const TileSelectionBounds& bounds : boundsIt->second) {
      renderer.AddTileSelectionBounds(
          bounds.west,
          bounds.south,
          bounds.east,
          bounds.north,
          bounds.wrapsLongitude);
    }
  }
}

void setTileActiveStateIfNeeded(const Tile* pTile, bool desiredActive) {
  CesiumGltfGameObject* pCesiumGameObject = getTileGameObject(pTile);
  if (!pCesiumGameObject) {
    return;
  }

  if (pCesiumGameObject->pGameObject->activeInHierarchy() != desiredActive) {
    pCesiumGameObject->pGameObject->SetActive(desiredActive);
  }
}

} // namespace

Cesium3DTilesetImpl::Cesium3DTilesetImpl(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset)
    : _pTileset(),
      _lastUpdateResult(),
      _activeTiles(),
#if UNITY_EDITOR
      _updateInEditorCallback(nullptr),
#endif
      _creditSystem(nullptr),
      _cameraManager(nullptr),
      _destroyTilesetOnNextUpdate(false),
      _lastOpaqueMaterialHash(0) {
}

Cesium3DTilesetImpl::~Cesium3DTilesetImpl() {}

void Cesium3DTilesetImpl::SetShowCreditsOnScreen(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset,
    bool value) {
  if (this->_pTileset) {
    this->_pTileset->setShowCreditsOnScreen(value);
  }
}

void Cesium3DTilesetImpl::Start(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {}

void Cesium3DTilesetImpl::Update(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
#if UNITY_EDITOR
  // In the Editor, outside Play mode, we arrange for UpdateInternal to be
  // called directly. See the OnEnable method. The return here avoids
  // double-calling UpdateInternal.
  if (UnityEngine::Application::isEditor() &&
      !UnityEditor::EditorApplication::isPlaying()) {
    return;
  }
#endif

  this->UpdateInternal(tileset);
}

void Cesium3DTilesetImpl::UpdateInternal(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  assert(tileset.enabled());

  // If "Suspend Update" is true, return early.
  if (tileset.suspendUpdate()) {
    return;
  }

  if (this->_destroyTilesetOnNextUpdate) {
    this->DestroyTileset(tileset);
  }

#if UNITY_EDITOR
  if (UnityEngine::Application::isEditor() &&
      !UnityEditor::EditorApplication::isPlaying()) {
    // If "Update In Editor" is false, return early.
    if (!tileset.updateInEditor()) {
      return;
    }

    // If the opaque material or any of its properties have changed, recreate
    // the tileset to reflect those changes.
    if (tileset.opaqueMaterial() != nullptr) {
      int32_t opaqueMaterialHash = tileset.opaqueMaterial().ComputeCRC();
      if (_lastOpaqueMaterialHash != opaqueMaterialHash) {
        this->DestroyTileset(tileset);
        _lastOpaqueMaterialHash = opaqueMaterialHash;
      }
    }
  }

#endif
  if (this->_creditSystem == nullptr || this->_cameraManager == nullptr) {
    // Refresh the tileset so it creates and uses a new credit system and camera
    // manager.
    this->DestroyTileset(tileset);
  }

  if (!this->_pTileset) {
    this->LoadTileset(tileset);
    if (!this->_pTileset)
      return;
  }

  std::vector<ViewState> viewStates =
      CameraManager::getAllCameras(tileset, *this);

  const ViewUpdateResult& updateResult = this->_pTileset->updateViewGroup(
      this->_pTileset->getDefaultViewGroup(),
      viewStates,
      DotNet::UnityEngine::Time::deltaTime());
  this->_pTileset->loadTiles();

  this->updateLastViewUpdateResultState(tileset, updateResult);

  std::unordered_set<Tile::ConstPointer> tilesToActivate;
  std::unordered_map<const Tile*, std::vector<TileSelectionBounds>>
      forwardedBoundsByTile;
  tilesToActivate.reserve(updateResult.tilesToRenderThisFrame.size());
  for (Tile::ConstPointer pTile : updateResult.tilesToRenderThisFrame) {
    const Tile* pActivationTile = resolveActivationTile(pTile);
    tilesToActivate.insert(pActivationTile);

    if (pActivationTile != pTile) {
      std::optional<TileSelectionBounds> bounds = getTileSelectionBounds(*pTile);
      if (bounds) {
        forwardedBoundsByTile[pActivationTile].emplace_back(*bounds);
      }
    }
  }

  updateI3dmTileSelectionBounds(tilesToActivate, forwardedBoundsByTile);

  for (Tile::ConstPointer pTile : this->_activeTiles) {
    if (tilesToActivate.find(pTile) != tilesToActivate.end()) {
      continue;
    }
    setTileActiveStateIfNeeded(pTile, false);
  }

  for (Tile::ConstPointer pTile : tilesToActivate) {
    setTileActiveStateIfNeeded(pTile, true);
  }

  this->_activeTiles = std::move(tilesToActivate);
}

void Cesium3DTilesetImpl::OnValidate(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  // Check if "Suspend Update" was the modified value.
  if (tileset.suspendUpdate() != tileset.previousSuspendUpdate()) {
    // If so, don't destroy the tileset.
    tileset.previousSuspendUpdate(tileset.suspendUpdate());
  } else {
    // Otherwise, destroy the tileset so it can be recreated with new settings.
    // Unity does not allow us to destroy GameObjects and MonoBehaviours in this
    // callback, so instead it is marked to happen later.
    this->_destroyTilesetOnNextUpdate = true;
  }
}

void Cesium3DTilesetImpl::OnEnable(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
#if UNITY_EDITOR
  // In the Editor, Update will only be called when something
  // changes. We need to call it continuously to allow tiles to
  // load.
  if (UnityEngine::Application::isEditor() &&
      !UnityEditor::EditorApplication::isPlaying()) {
    this->_updateInEditorCallback = UnityEditor::CallbackFunction(
        [this, tileset]() {
          try {
            this->UpdateInternal(tileset);
          } catch (const Reinterop::ReinteropNativeException& e) {
            UnityEngine::Debug::LogWarning(System::String(
                "Stopping Cesium editor updates after a managed exception in "
                "the update callback: " +
                e.GetDotNetException().Message().ToStlString()));
            this->removeUpdateInEditorCallback();
          }
        });
    UnityEditor::EditorApplication::update(
        UnityEditor::EditorApplication::update() +
        this->_updateInEditorCallback);
  }
#endif
}

void Cesium3DTilesetImpl::OnDisable(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
#if UNITY_EDITOR
  this->removeUpdateInEditorCallback();
#endif

  this->_creditSystem = nullptr;
  this->_cameraManager = nullptr;

  this->DestroyTileset(tileset);
}

void Cesium3DTilesetImpl::RecreateTileset(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  this->DestroyTileset(tileset);
}

namespace {

struct CalculateECEFCameraPosition {
  const CesiumGeospatial::Ellipsoid& ellipsoid;

  glm::dvec3 operator()(const CesiumGeometry::BoundingSphere& sphere) {
    const glm::dvec3& center = sphere.getCenter();
    glm::dmat4 enuToEcef =
        glm::dmat4(CesiumGeospatial::GlobeTransforms::eastNorthUpToFixedFrame(
            center,
            ellipsoid));
    glm::dvec3 offset = sphere.getRadius() * glm::normalize(
                                                 glm::dvec3(enuToEcef[0]) +
                                                 glm::dvec3(enuToEcef[1]) +
                                                 glm::dvec3(enuToEcef[2]));
    glm::dvec3 position = center + offset;
    return position;
  }

  glm::dvec3
  operator()(const CesiumGeometry::OrientedBoundingBox& orientedBoundingBox) {
    const glm::dvec3& center = orientedBoundingBox.getCenter();
    glm::dmat4 enuToEcef =
        glm::dmat4(CesiumGeospatial::GlobeTransforms::eastNorthUpToFixedFrame(
            center,
            ellipsoid));
    const glm::dmat3& halfAxes = orientedBoundingBox.getHalfAxes();
    glm::dvec3 offset =
        glm::length(halfAxes[0] + halfAxes[1] + halfAxes[2]) *
        glm::normalize(
            glm::dvec3(enuToEcef[0]) + glm::dvec3(enuToEcef[1]) +
            glm::dvec3(enuToEcef[2]));
    glm::dvec3 position = center + offset;
    return position;
  }

  glm::dvec3
  operator()(const CesiumGeospatial::BoundingRegion& boundingRegion) {
    return (*this)(boundingRegion.getBoundingBox());
  }

  glm::dvec3
  operator()(const CesiumGeospatial::BoundingRegionWithLooseFittingHeights&
                 boundingRegionWithLooseFittingHeights) {
    return (*this)(boundingRegionWithLooseFittingHeights.getBoundingRegion()
                       .getBoundingBox());
  }

  glm::dvec3 operator()(const CesiumGeospatial::S2CellBoundingVolume& s2) {
    return (*this)(s2.computeBoundingRegion(ellipsoid));
  }

  glm::dvec3 operator()(const CesiumGeometry::BoundingCylinderRegion& cyl) {
    return (*this)(cyl.toOrientedBoundingBox());
  }
};
} // namespace

void Cesium3DTilesetImpl::updateOverlayMaterialKeys(
    const DotNet::System::Array1<DotNet::CesiumForUnity::CesiumRasterOverlay>&
        overlays) {
  if (!this->_pTileset ||
      !this->_pTileset->getExternals().pPrepareRendererResources) {
    return;
  }

  std::vector<std::string> overlayMaterialKeys(overlays.Length());
  for (int32_t i = 0, len = overlays.Length(); i < len; ++i) {
    CesiumForUnity::CesiumRasterOverlay overlay = overlays[i];
    overlayMaterialKeys[i] = overlay.materialKey().ToStlString();
  }

  // Use material keys to resolve the property IDs in TilesetMaterialProperties.
  UnityPrepareRendererResources* pRendererResources =
      static_cast<UnityPrepareRendererResources*>(
          this->_pTileset->getExternals().pPrepareRendererResources.get());
  pRendererResources->getMaterialProperties().updateOverlayParameterIDs(
      overlayMaterialKeys);
}

void Cesium3DTilesetImpl::UpdateOverlayMaterialKeys(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  this->updateOverlayMaterialKeys(
      tileset.gameObject()
          .GetComponents<CesiumForUnity::CesiumRasterOverlay>());
}

void Cesium3DTilesetImpl::FocusTileset(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {

#if UNITY_EDITOR
  UnityEditor::SceneView lastActiveEditorView =
      UnityEditor::SceneView::lastActiveSceneView();
  if (!this->_pTileset || !this->_pTileset->getRootTile() ||
      lastActiveEditorView == nullptr) {
    return;
  }

  UnityEngine::Camera editorCamera = lastActiveEditorView.camera();
  if (editorCamera == nullptr) {
    return;
  }

  DotNet::CesiumForUnity::CesiumGeoreference georeferenceComponent =
      tileset.gameObject()
          .GetComponentInParent<DotNet::CesiumForUnity::CesiumGeoreference>();
  if (georeferenceComponent == nullptr) {
    return;
  }

  const CesiumGeospatial::LocalHorizontalCoordinateSystem& georeferenceCrs =
      georeferenceComponent.NativeImplementation().getCoordinateSystem(
          georeferenceComponent);
  const glm::dmat4& ecefToUnityWorld =
      georeferenceCrs.getEcefToLocalTransformation();

  const CesiumGeospatial::Ellipsoid& ellipsoid =
      georeferenceComponent.ellipsoid().NativeImplementation().GetEllipsoid();

  const BoundingVolume& boundingVolume =
      this->_pTileset->getRootTile()->getBoundingVolume();
  glm::dvec3 ecefCameraPosition =
      std::visit(CalculateECEFCameraPosition{ellipsoid}, boundingVolume);
  glm::dvec3 unityCameraPosition =
      glm::dvec3(ecefToUnityWorld * glm::dvec4(ecefCameraPosition, 1.0));

  glm::dvec3 ecefCenter =
      Cesium3DTilesSelection::getBoundingVolumeCenter(boundingVolume);
  glm::dvec3 unityCenter =
      glm::dvec3(ecefToUnityWorld * glm::dvec4(ecefCenter, 1.0));
  glm::dvec3 unityCameraFront =
      glm::normalize(unityCenter - unityCameraPosition);
  glm::dvec3 unityCameraRight =
      glm::normalize(glm::cross(glm::dvec3(0.0, 0.0, 1.0), unityCameraFront));
  glm::dvec3 unityCameraUp =
      glm::normalize(glm::cross(unityCameraFront, unityCameraRight));

  UnityEngine::Vector3 unityCameraPositionf;
  unityCameraPositionf.x = static_cast<float>(unityCameraPosition.x);
  unityCameraPositionf.y = static_cast<float>(unityCameraPosition.y);
  unityCameraPositionf.z = static_cast<float>(unityCameraPosition.z);

  UnityEngine::Vector3 unityCameraFrontf;
  unityCameraFrontf.x = static_cast<float>(unityCameraFront.x);
  unityCameraFrontf.y = static_cast<float>(unityCameraFront.y);
  unityCameraFrontf.z = static_cast<float>(unityCameraFront.z);

  lastActiveEditorView.pivot(unityCameraPositionf);
  lastActiveEditorView.rotation(UnityEngine::Quaternion::LookRotation(
      unityCameraFrontf,
      UnityEngine::Vector3::up()));
#endif
}

float Cesium3DTilesetImpl::ComputeLoadProgress(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  if (getTileset() == nullptr) {
    return 0;
  }
  return getTileset()->computeLoadProgress();
}

System::Threading::Tasks::Task1<CesiumForUnity::CesiumSampleHeightResult>
Cesium3DTilesetImpl::SampleHeightMostDetailed(
    const CesiumForUnity::Cesium3DTileset& tileset,
    const System::Array1<Unity::Mathematics::double3>&
        longitudeLatitudeHeightPositions) {
  if (this->getTileset() == nullptr) {
    // Calling DestroyTileset ensures _destroyTilesetOnNextUpdate is reset.
    this->DestroyTileset(tileset);
    this->LoadTileset(tileset);
  }

  System::Threading::Tasks::TaskCompletionSource1<
      CesiumForUnity::CesiumSampleHeightResult>
      promise{};

  std::vector<CesiumGeospatial::Cartographic> positions;
  positions.reserve(longitudeLatitudeHeightPositions.Length());

  for (int32_t i = 0, len = longitudeLatitudeHeightPositions.Length(); i < len;
       ++i) {
    Unity::Mathematics::double3 position = longitudeLatitudeHeightPositions[i];
    positions.emplace_back(CesiumGeospatial::Cartographic::fromDegrees(
        position.x,
        position.y,
        position.z));
  }

  auto sampleHeights = [this, &positions]() mutable {
    if (this->getTileset()) {
      return this->getTileset()
          ->sampleHeightMostDetailed(positions)
          .catchImmediately([positions = std::move(positions)](
                                std::exception&& exception) mutable {
            std::vector<bool> sampleSuccess(positions.size(), false);
            return Cesium3DTilesSelection::SampleHeightResult{
                std::move(positions),
                std::move(sampleSuccess),
                {exception.what()}};
          });
    } else {
      std::vector<bool> sampleSuccess(positions.size(), false);
      return getAsyncSystem().createResolvedFuture(
          Cesium3DTilesSelection::SampleHeightResult{
              std::move(positions),
              std::move(sampleSuccess),
              {"Could not sample heights from tileset because it has not "
               "been created."}});
    }
  };

  sampleHeights()
      .thenImmediately(
          [promise](Cesium3DTilesSelection::SampleHeightResult&& result) {
            System::Array1<Unity::Mathematics::double3> positions(
                result.positions.size());
            for (size_t i = 0; i < result.positions.size(); ++i) {
              const CesiumGeospatial::Cartographic& positionRadians =
                  result.positions[i];
              positions.Item(
                  i,
                  Unity::Mathematics::double3{
                      CesiumUtility::Math::radiansToDegrees(
                          positionRadians.longitude),
                      CesiumUtility::Math::radiansToDegrees(
                          positionRadians.latitude),
                      positionRadians.height});
            }

            System::Array1<bool> sampleSuccess(result.sampleSuccess.size());
            for (size_t i = 0; i < result.sampleSuccess.size(); ++i) {
              sampleSuccess.Item(i, result.sampleSuccess[i]);
            }

            System::Array1<System::String> warnings(result.warnings.size());
            for (size_t i = 0; i < result.warnings.size(); ++i) {
              warnings.Item(i, System::String(result.warnings[i]));
            }

            CesiumForUnity::CesiumSampleHeightResult unityResult;
            unityResult.longitudeLatitudeHeightPositions(positions);
            unityResult.sampleSuccess(sampleSuccess);
            unityResult.warnings(warnings);

            promise.SetResult(unityResult);
          })
      .catchImmediately([promise](std::exception&& exception) {
        promise.SetException(
            System::Exception(System::String(exception.what())));
      });

  return promise.Task();
}

Tileset* Cesium3DTilesetImpl::getTileset() { return this->_pTileset.get(); }

const Tileset* Cesium3DTilesetImpl::getTileset() const {
  return this->_pTileset.get();
}
const DotNet::CesiumForUnity::CesiumCreditSystem&
Cesium3DTilesetImpl::getCreditSystem() const {
  return this->_creditSystem;
}

void Cesium3DTilesetImpl::setCreditSystem(
    const DotNet::CesiumForUnity::CesiumCreditSystem& creditSystem) {
  this->_creditSystem = creditSystem;
}

const DotNet::CesiumForUnity::CesiumCameraManager&
Cesium3DTilesetImpl::getCameraManager() const {
  return this->_cameraManager;
}

void Cesium3DTilesetImpl::setCameraManager(
    const DotNet::CesiumForUnity::CesiumCameraManager& cameraManager) {
  this->_cameraManager = cameraManager;
}

void Cesium3DTilesetImpl::updateLastViewUpdateResultState(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset,
    const Cesium3DTilesSelection::ViewUpdateResult& currentResult) {
  if (!tileset.logSelectionStats())
    return;

  const ViewUpdateResult& previousResult = this->_lastUpdateResult;
  if (currentResult.tilesToRenderThisFrame.size() !=
          previousResult.tilesToRenderThisFrame.size() ||
      currentResult.workerThreadTileLoadQueueLength !=
          previousResult.workerThreadTileLoadQueueLength ||
      currentResult.mainThreadTileLoadQueueLength !=
          previousResult.mainThreadTileLoadQueueLength ||
      currentResult.tilesVisited != previousResult.tilesVisited ||
      currentResult.culledTilesVisited != previousResult.culledTilesVisited ||
      currentResult.tilesCulled != previousResult.tilesCulled ||
      currentResult.maxDepthVisited != previousResult.maxDepthVisited) {
    SPDLOG_LOGGER_INFO(
        this->_pTileset->getExternals().pLogger,
        "{0}: Visited {1}, Culled Visited {2}, Rendered {3}, Culled {4}, Max "
        "Depth Visited {5}, Loading-Worker {6}, Loading-Main {7} "
        "Total Tiles Resident {8}, Frame {9}",
        tileset.gameObject().name().ToStlString(),
        currentResult.tilesVisited,
        currentResult.culledTilesVisited,
        currentResult.tilesToRenderThisFrame.size(),
        currentResult.tilesCulled,
        currentResult.maxDepthVisited,
        currentResult.workerThreadTileLoadQueueLength,
        currentResult.mainThreadTileLoadQueueLength,
        this->_pTileset->getNumberOfTilesLoaded(),
        currentResult.frameNumber);
  }

  this->_lastUpdateResult = currentResult;
}

void Cesium3DTilesetImpl::DestroyTileset(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  // Remove any existing raster overlays
  System::Array1<CesiumForUnity::CesiumRasterOverlay> overlays =
      tileset.gameObject().GetComponents<CesiumForUnity::CesiumRasterOverlay>();
  for (int32_t i = 0, len = overlays.Length(); i < len; ++i) {
    CesiumForUnity::CesiumRasterOverlay overlay = overlays[i];
    overlay.RemoveFromTileset();
  }

  this->_pTileset.reset();
  this->_activeTiles.clear();

  this->_destroyTilesetOnNextUpdate = false;
}

#if UNITY_EDITOR
void Cesium3DTilesetImpl::removeUpdateInEditorCallback() {
  if (this->_updateInEditorCallback == nullptr) {
    return;
  }

  UnityEditor::EditorApplication::update(
      UnityEditor::EditorApplication::update() - this->_updateInEditorCallback);
  this->_updateInEditorCallback.Dispose();
  this->_updateInEditorCallback = nullptr;
}
#endif

void Cesium3DTilesetImpl::LoadTileset(
    const DotNet::CesiumForUnity::Cesium3DTileset& tileset) {
  TilesetOptions options{};
  options.rendererOptions = std::make_any<CreateModelOptions>(tileset);
  options.maximumScreenSpaceError = tileset.maximumScreenSpaceError();
  options.preloadAncestors = tileset.preloadAncestors();
  options.preloadSiblings = tileset.preloadSiblings();
  options.forbidHoles = tileset.forbidHoles();
  options.maximumSimultaneousTileLoads = tileset.maximumSimultaneousTileLoads();
  options.maximumCachedBytes = tileset.maximumCachedBytes();
  options.loadingDescendantLimit = tileset.loadingDescendantLimit();
  options.enableFrustumCulling = tileset.enableFrustumCulling();
  options.enableFogCulling = tileset.enableFogCulling();
  options.enforceCulledScreenSpaceError =
      tileset.enforceCulledScreenSpaceError();
  options.culledScreenSpaceError = tileset.culledScreenSpaceError();
  // options.enableLodTransitionPeriod = tileset.useLodTransitions();
  // options.lodTransitionLength = tileset.lodTransitionLength();
  options.showCreditsOnScreen = tileset.showCreditsOnScreen();
  options.loadErrorCallback =
      [tileset](const TilesetLoadFailureDetails& details) {
        int typeValue = (int)details.type;
        CesiumForUnity::Cesium3DTilesetLoadFailureDetails unityDetails(
            tileset,
            CesiumForUnity::Cesium3DTilesetLoadType(typeValue),
            details.statusCode,
            System::String(details.message));

        CesiumForUnity::Cesium3DTileset::BroadcastCesium3DTilesetLoadFailure(
            unityDetails);
      };

  // Generous per-frame time limits for loading / unloading on main thread.
  options.mainThreadLoadingTimeLimit = 5.0;
  options.tileCacheUnloadTimeLimit = 5.0;

  DotNet::CesiumForUnity::CesiumGeoreference georeferenceComponent =
      tileset.gameObject()
          .GetComponentInParent<DotNet::CesiumForUnity::CesiumGeoreference>();
  if (georeferenceComponent != nullptr) {
    options.ellipsoid =
        georeferenceComponent.ellipsoid().NativeImplementation().GetEllipsoid();
  }

  TilesetContentOptions contentOptions{};
  contentOptions.generateMissingNormalsSmooth = tileset.generateSmoothNormals();

  CesiumGltf::SupportedGpuCompressedPixelFormats supportedFormats;
  supportedFormats.ETC2_RGBA = UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
          RGBA_ETC2_SRGB,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.ETC1_RGB = UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
          RGB_ETC_UNorm,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.BC1_RGB = DotNet::UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
          RGBA_DXT1_SRGB,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.BC3_RGBA =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RGBA_DXT5_SRGB,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.BC4_R = DotNet::UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::R_BC4_SNorm,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.BC5_RG = DotNet::UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
          RG_BC5_SNorm,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.BC7_RGBA =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RGBA_BC7_SRGB,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.ASTC_4x4_RGBA =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RGBA_ASTC4X4_SRGB,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.PVRTC1_4_RGB =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RGB_PVRTC_4Bpp_SRGB,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.PVRTC1_4_RGBA =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RGBA_PVRTC_4Bpp_SRGB,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats
      .ETC2_EAC_R11 = DotNet::UnityEngine::SystemInfo::IsFormatSupported(
      DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::R_EAC_UNorm,
      DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);
  supportedFormats.ETC2_EAC_RG11 =
      DotNet::UnityEngine::SystemInfo::IsFormatSupported(
          DotNet::UnityEngine::Experimental::Rendering::GraphicsFormat::
              RG_EAC_UNorm,
          DotNet::UnityEngine::Experimental::Rendering::FormatUsage::Sample);

  contentOptions.ktx2TranscodeTargets =
      CesiumGltf::Ktx2TranscodeTargets(supportedFormats, false);

  contentOptions.applyTextureTransform = false;

  options.contentOptions = contentOptions;

  CesiumForUnity::CesiumCameraManager cameraManager =
      CesiumForUnity::CesiumCameraManager::GetOrCreate(tileset.gameObject());
  this->setCameraManager(cameraManager);

  this->_lastUpdateResult = ViewUpdateResult();
  this->_activeTiles.clear();

  if (tileset.tilesetSource() ==
      CesiumForUnity::CesiumDataSource::FromCesiumIon) {
    System::String ionAccessToken = tileset.ionAccessToken();
    if (System::String::IsNullOrEmpty(ionAccessToken)) {
      ionAccessToken = tileset.ionServer().defaultIonAccessToken();
    }

    std::string ionAssetEndpointUrl =
        tileset.ionServer().apiUrl().ToStlString();

    if (!ionAssetEndpointUrl.empty()) {
      // Make sure the URL ends with a slash
      if (*ionAssetEndpointUrl.rbegin() != '/')
        ionAssetEndpointUrl += '/';

      this->_pTileset = std::make_unique<Tileset>(
          createTilesetExternals(tileset),
          tileset.ionAssetID(),
          ionAccessToken.ToStlString(),
          options,
          ionAssetEndpointUrl);
    } else {
      // Resolve the API URL if it's not already in progress.
      resolveCesiumIonApiUrl(tileset.ionServer());
    }
  } else if (
      tileset.tilesetSource() ==
      CesiumForUnity::CesiumDataSource::FromEllipsoid) {
    this->_pTileset = EllipsoidTilesetLoader::createTileset(
        createTilesetExternals(tileset),
        options);
  } else {
    this->_pTileset = std::make_unique<Tileset>(
        createTilesetExternals(tileset),
        tileset.url().ToStlString(),
        options);
  }

  // Add any overlay components.
  System::Array1<CesiumForUnity::CesiumRasterOverlay> overlays =
      tileset.gameObject().GetComponents<CesiumForUnity::CesiumRasterOverlay>();
  for (int32_t i = 0, len = overlays.Length(); i < len; ++i) {
    CesiumForUnity::CesiumRasterOverlay overlay = overlays[i];
    overlay.AddToTileset();
  }

  this->updateOverlayMaterialKeys(overlays);

  // Add any tile excluder components.
  System::Array1<CesiumForUnity::CesiumTileExcluder> excluders =
      tileset.gameObject()
          .GetComponentsInParent<CesiumForUnity::CesiumTileExcluder>();
  for (int32_t i = 0, len = excluders.Length(); i < len; ++i) {
    CesiumForUnity::CesiumTileExcluder excluder = excluders[i];
    if (!excluder.enabled()) {
      continue;
    }

    excluder.AddToTileset(tileset);
  }

  // If the tileset has an opaque material, set its hash here to avoid
  // destroying it on the first tick after creation.
  if (tileset.opaqueMaterial() != nullptr) {
    int32_t opaqueMaterialHash = tileset.opaqueMaterial().ComputeCRC();
    this->_lastOpaqueMaterialHash = opaqueMaterialHash;
  }
}
} // namespace CesiumForUnityNative
