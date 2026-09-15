# Keep instance accessors and shared prototype geometry intact when Native creates
# synthetic raster children. Only models explicitly marked by our batched backend
# take this path. The normal Native upsampler is unchanged for all other models.
# Patch a build-tree copy rather than modifying the checked-out submodule.
set(_instance_raster_source "${CMAKE_CURRENT_LIST_DIR}/../extern/cesium-native/CesiumRasterOverlays/src/RasterOverlayUtilities.cpp")
file(READ "${_instance_raster_source}" _instance_raster_code)
set(_instance_raster_anchor [=[  CESIUM_TRACE("upsampleGltfForRasterOverlays");
  Model result;]=])
set(_instance_raster_replacement [=[  CESIUM_TRACE("upsampleGltfForRasterOverlays");
  auto instanceMarker = parentModel.extras.find("CesiumUnityBatchedInstances");
  if (instanceMarker != parentModel.extras.end()) {
    const bool* enabled = std::get_if<bool>(&instanceMarker->second.value);
    if (enabled && *enabled) {
      // Coverage comes from the synthetic child's bounding region in the Unity
      // renderer. Copy accessors as well: legacy prototype clipping cannot clip
      // every placement and would invalidate EXT_mesh_gpu_instancing indices.
      Model instancedChild(parentModel);
      instancedChild.extras["CesiumUnityInstanceRasterChild"] = true;
      return instancedChild;
    }
  }
  Model result;]=])
string(FIND "${_instance_raster_code}" "${_instance_raster_anchor}" _instance_raster_match)
if(_instance_raster_match LESS 0)
  message(FATAL_ERROR "Instanced raster integration no longer matches cesium-native. Review PatchInstancedRasterUpsampling.cmake before updating the submodule.")
endif()
string(REPLACE "${_instance_raster_anchor}" "${_instance_raster_replacement}"
  _instance_raster_patched "${_instance_raster_code}")
set(_instance_raster_output "${CMAKE_CURRENT_BINARY_DIR}/instanced-raster/RasterOverlayUtilities.cpp")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/instanced-raster")
file(CONFIGURE OUTPUT "${_instance_raster_output}" CONTENT "${_instance_raster_patched}" @ONLY)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_instance_raster_source}")
get_target_property(_instance_raster_sources CesiumRasterOverlays SOURCES)
set(_instance_raster_found FALSE)
set(_instance_raster_sources_new)
foreach(_source IN LISTS _instance_raster_sources)
  if(_source MATCHES "(^|/)RasterOverlayUtilities\\.cpp$")
    list(APPEND _instance_raster_sources_new "${_instance_raster_output}")
    set(_instance_raster_found TRUE)
  else()
    list(APPEND _instance_raster_sources_new "${_source}")
  endif()
endforeach()
if(NOT _instance_raster_found)
  message(FATAL_ERROR "Could not replace Native's RasterOverlayUtilities.cpp source for instance-safe raster subdivision.")
endif()
set_property(TARGET CesiumRasterOverlays PROPERTY SOURCES "${_instance_raster_sources_new}")
