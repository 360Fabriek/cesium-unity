#pragma once

#include <CesiumGltf/AccessorView.h>
#include <CesiumGltf/ExtensionExtMeshGpuInstancing.h>
#include <CesiumGltf/Model.h>

#include <glm/gtc/matrix_transform.hpp>
#include <glm/gtc/quaternion.hpp>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <string>
#include <type_traits>
#include <vector>

namespace CesiumForUnityNative {

/**
 * Decoded, node-local EXT_mesh_gpu_instancing transforms.
 * An empty vector means NO instances, not one untransformed instance.
 * An absent extension is represented by a null shared_ptr in CesiumPrimitiveInfo.
 */
struct GltfInstanceTransforms {
  std::vector<glm::dmat4> transforms;
  std::string error;
};

namespace GltfInstanceTransformsDetail {

inline GltfInstanceTransforms invalid(const std::string& reason) {
  return {{}, "Invalid EXT_mesh_gpu_instancing: " + reason};
}

inline bool finite(const glm::dvec3& v) {
  return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
}

template <typename T> double rotationComponent(T value) {
  if constexpr (std::is_integral_v<T>) {
    // glTF signed-normalized decoding maps the most-negative value to -1.
    return std::max(
        static_cast<double>(value) /
            static_cast<double>(std::numeric_limits<T>::max()),
        -1.0);
  } else {
    return static_cast<double>(value);
  }
}

template <typename T>
bool readRotations(
    const CesiumGltf::Model& model,
    const CesiumGltf::Accessor& accessor,
    std::vector<glm::dquat>& rotations) {
  using Value = CesiumGltf::AccessorTypes::VEC4<T>;
  const CesiumGltf::AccessorView<Value> view(model, accessor);
  if (view.status() != CesiumGltf::AccessorViewStatus::Valid ||
      view.size() != static_cast<int64_t>(rotations.size())) {
    return false;
  }
  for (int64_t i = 0; i < view.size(); ++i) {
    const auto& v = view[i].value;
    // glTF stores xyzw; GLM's quaternion constructor accepts wxyz.
    glm::dquat q(
        rotationComponent(v[3]),
        rotationComponent(v[0]),
        rotationComponent(v[1]),
        rotationComponent(v[2]));
    const double lengthSquared = glm::dot(q, q);
    if (!std::isfinite(lengthSquared) || lengthSquared <= 0.0) {
      return false;
    }
    // Quantized unit quaternions can have a small length error.
    rotations[static_cast<size_t>(i)] = q / std::sqrt(lengthSquared);
  }
  return true;
}

} // namespace GltfInstanceTransformsDetail

/**
 * Decode only the glTF extension, never an I3DM/CMPT binary. Cesium Native's
 * converters have already handled legacy formats, RTC, ENU, quantization,
 * embedded/external GLBs and conversion into node-local glTF transforms.
 *
 * This function does not call Unity and may run on a worker thread. It validates
 * every attribute's index/count, including custom attributes, before allocating
 * or reading transform data. Optional TRS attributes use the glTF defaults.
 */
inline GltfInstanceTransforms readGltfInstanceTransforms(
    const CesiumGltf::Model& model,
    const CesiumGltf::ExtensionExtMeshGpuInstancing& extension) {
  using namespace CesiumGltf;
  using namespace GltfInstanceTransformsDetail;
  if (extension.attributes.empty()) {
    return invalid("attributes is empty");
  }

  int64_t count = -1;
  for (const auto& attribute : extension.attributes) {
    const Accessor* pAccessor =
        Model::getSafe(&model.accessors, attribute.second);
    if (!pAccessor || pAccessor->count < 0 ||
        pAccessor->count > std::numeric_limits<int32_t>::max()) {
      return invalid(attribute.first + " has an invalid accessor or count");
    }
    if (count >= 0 && pAccessor->count != count) {
      return invalid("attribute counts do not match");
    }
    count = pAccessor->count;
  }

  const auto findAccessor = [&](const char* name) -> const Accessor* {
    auto it = extension.attributes.find(name);
    return it == extension.attributes.end()
               ? nullptr
               : Model::getSafe(&model.accessors, it->second);
  };
  const Accessor* pTranslation = findAccessor("TRANSLATION");
  const Accessor* pRotation = findAccessor("ROTATION");
  const Accessor* pScale = findAccessor("SCALE");

  using Vec3 = AccessorTypes::VEC3<float>;
  AccessorView<Vec3> translations;
  AccessorView<Vec3> scales;
  const auto readVec3 = [&](const Accessor* pAccessor,
                            AccessorView<Vec3>& view) -> bool {
    if (!pAccessor) {
      return true;
    }
    if (pAccessor->type != Accessor::Type::VEC3 ||
        pAccessor->componentType != Accessor::ComponentType::FLOAT ||
        pAccessor->normalized) {
      return false;
    }
    // A zero count is tolerated as an empty group, but not as a base mesh.
    if (count == 0) {
      return true;
    }
    view = AccessorView<Vec3>(model, *pAccessor);
    return view.status() == AccessorViewStatus::Valid && view.size() == count;
  };
  if (!readVec3(pTranslation, translations)) {
    return invalid("TRANSLATION must be a valid, non-normalized FLOAT VEC3");
  }
  if (!readVec3(pScale, scales)) {
    return invalid("SCALE must be a valid, non-normalized FLOAT VEC3");
  }

  if (pRotation) {
    const bool isFloat =
        pRotation->componentType == Accessor::ComponentType::FLOAT &&
        !pRotation->normalized;
    const bool isNormalizedSigned = pRotation->normalized &&
        (pRotation->componentType == Accessor::ComponentType::BYTE ||
         pRotation->componentType == Accessor::ComponentType::SHORT);
    if (pRotation->type != Accessor::Type::VEC4 ||
        (!isFloat && !isNormalizedSigned)) {
      return invalid("ROTATION must be FLOAT or normalized BYTE/SHORT VEC4");
    }
  }
  if (count == 0) {
    return {};
  }

  std::vector<glm::dquat> rotations(
      static_cast<size_t>(count), glm::dquat(1.0, 0.0, 0.0, 0.0));
  if (pRotation) {
    bool valid = false;
    switch (pRotation->componentType) {
    case Accessor::ComponentType::FLOAT:
      valid = readRotations<float>(model, *pRotation, rotations);
      break;
    case Accessor::ComponentType::BYTE:
      valid = readRotations<int8_t>(model, *pRotation, rotations);
      break;
    case Accessor::ComponentType::SHORT:
      valid = readRotations<int16_t>(model, *pRotation, rotations);
      break;
    }
    if (!valid) {
      return invalid("ROTATION has unreadable data or a non-finite/zero quaternion");
    }
  }

  GltfInstanceTransforms result;
  result.transforms.reserve(static_cast<size_t>(count));
  for (int64_t i = 0; i < count; ++i) {
    glm::dvec3 translation(0.0);
    glm::dvec3 scale(1.0);
    if (pTranslation) {
      const auto& v = translations[i].value;
      translation = glm::dvec3(v[0], v[1], v[2]);
    }
    if (pScale) {
      const auto& v = scales[i].value;
      scale = glm::dvec3(v[0], v[1], v[2]);
    }
    if (!finite(translation) || !finite(scale)) {
      return invalid("TRANSLATION or SCALE contains a non-finite value");
    }
    result.transforms.emplace_back(
        glm::translate(glm::dmat4(1.0), translation) *
        glm::mat4_cast(rotations[static_cast<size_t>(i)]) *
        glm::scale(glm::dmat4(1.0), scale));
  }
  return result;
}

} // namespace CesiumForUnityNative
