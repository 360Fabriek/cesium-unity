# Extend the existing GameObject batching adapter, not the submodule checkout.
# Marked models retain their prototype and instance accessors during subdivision.
# Their placed-position shaders do not need more prototype UV buffers per level.
function(_cesium_instance_replace_once variable old replacement)
  set(_text "${${variable}}")
  string(LENGTH "${_text}" _before)
  string(LENGTH "${old}" _length)
  string(REPLACE "${old}" "" _without "${_text}")
  string(LENGTH "${_without}" _after)
  math(EXPR _removed "${_before} - ${_after}")
  if(NOT _removed EQUAL _length)
    message(FATAL_ERROR "Instanced raster integration expected exactly one source context. Review PatchInstancedRasterUpsampling.cmake before updating cesium-native: ${old}")
  endif()
  string(REPLACE "${old}" "${replacement}" _text "${_text}")
  set(${variable} "${_text}" PARENT_SCOPE)
endfunction()

set(_instance_raster_source "${CMAKE_CURRENT_LIST_DIR}/../extern/cesium-native/CesiumRasterOverlays/src/RasterOverlayUtilities.cpp")
file(READ "${_instance_raster_source}" _instance_raster_code)
# Windows checkouts may contain CRLF; generated C++ consistently uses LF.
string(REPLACE "\r\n" "\n" _instance_raster_code "${_instance_raster_code}")
_cesium_instance_replace_once(_instance_raster_code
  "#include <CesiumGeometry/QuadtreeTileID.h>"
  "#include <cmath>\n#include <CesiumGeometry/QuadtreeTileID.h>")

_cesium_instance_replace_once(_instance_raster_code
  [=[  const Ellipsoid& ellipsoid = getProjectionEllipsoid(projections.front());]=]
  [=[  const Ellipsoid& ellipsoid = getProjectionEllipsoid(projections.front());

  auto marker = model.extras.find("CesiumUnityBatchedInstances");
  const bool* batched = marker == model.extras.end()
      ? nullptr : std::get_if<bool>(&marker->second.value);
  if (batched && *batched) {
    // The original GameObject adapter already computed actual placed bounds.
    // Reuse those heights, but adopt this child's own declared coverage. Never
    // use the currently attached (possibly ancestor) image as geometry bounds.
    auto bounds = model.extras.find("CesiumUnityInstanceBounds");
    const auto* values = bounds == model.extras.end() ? nullptr
        : std::get_if<std::vector<CesiumUtility::JsonValue>>(&bounds->second.value);
    if (!values || values->size() != 6)
      throw std::runtime_error("Instanced raster model has no cached placed bounds.");
    double v[6];
    for (size_t i = 0; i < 6; ++i) {
      v[i] = (*values)[i].getSafeNumberOrDefault(std::numeric_limits<double>::quiet_NaN());
      if (!std::isfinite(v[i]))
        throw std::runtime_error("Instanced raster model has non-finite placed bounds.");
    }
    if (v[1] > v[3] || v[4] > v[5])
      throw std::runtime_error("Instanced raster model has inverted placed bounds.");
    const GlobeRectangle coverage = globeRectangle ? *globeRectangle
        : GlobeRectangle(v[0], v[1], v[2], v[3]);
    std::vector<Rectangle> rectangles;
    rectangles.reserve(projections.size());
    for (const Projection& projection : projections)
      rectangles.emplace_back(projectRectangleSimple(projection, coverage));
    // No accessors or buffers are appended. Mixed ordinary/instanced triangle
    // content uses the same placed-position shader through the existing adapter.
    return RasterOverlayDetails{std::move(projections), std::move(rectangles),
        BoundingRegion(coverage, v[4], v[5], ellipsoid)};
  }]=])

_cesium_instance_replace_once(_instance_raster_code
  [=[  CESIUM_TRACE("upsampleGltfForRasterOverlays");
  Model result;]=]
  [=[  CESIUM_TRACE("upsampleGltfForRasterOverlays");
  auto instanceMarker = parentModel.extras.find("CesiumUnityBatchedInstances");
  if (instanceMarker != parentModel.extras.end()) {
    const bool* enabled = std::get_if<bool>(&instanceMarker->second.value);
    if (enabled && *enabled) {
      // Keep the current adapter's child marker and all instance/accessor IDs.
      // Intersecting instances are clipped by fragments, not removed wholesale.
      Model instancedChild(parentModel);
      instancedChild.extras["CesiumUnityInstanceRasterChild"] = true;
      return instancedChild;
    }
  }
  Model result;]=])

set(_instance_raster_output "${CMAKE_CURRENT_BINARY_DIR}/instanced-raster/RasterOverlayUtilities.cpp")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/instanced-raster")
file(CONFIGURE OUTPUT "${_instance_raster_output}" CONTENT "${_instance_raster_code}" @ONLY)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_instance_raster_source}")
get_target_property(_instance_raster_sources CesiumRasterOverlays SOURCES)
set(_instance_raster_found 0)
set(_instance_raster_sources_new)
foreach(_source IN LISTS _instance_raster_sources)
  string(REPLACE "\\" "/" _normalized "${_source}")
  if(_normalized MATCHES "(^|/)RasterOverlayUtilities\\.cpp$")
    list(APPEND _instance_raster_sources_new "${_instance_raster_output}")
    math(EXPR _instance_raster_found "${_instance_raster_found} + 1")
  else()
    list(APPEND _instance_raster_sources_new "${_source}")
  endif()
endforeach()
if(NOT _instance_raster_found EQUAL 1)
  message(FATAL_ERROR "Expected exactly one Native RasterOverlayUtilities.cpp target source, got ${_instance_raster_found}.")
endif()
set_property(TARGET CesiumRasterOverlays PROPERTY SOURCES "${_instance_raster_sources_new}")
