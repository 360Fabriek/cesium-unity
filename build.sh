#!/usr/bin/env bash
# Modified by 360Fabriek for the patched Cesium for Unity redistribution.
# See NOTICE and MODIFICATIONS.md.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
UNITY_EXECUTABLE="${UNITY_EXECUTABLE:-/Volumes/Expansion/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity}"
EZVCPKG_BASEDIR="${EZVCPKG_BASEDIR:-/Volumes/Expansion/BuildCaches/ezvcpkg}"
VCPKG_DEFAULT_BINARY_CACHE="${VCPKG_DEFAULT_BINARY_CACHE:-/Volumes/Expansion/BuildCaches/vcpkg-archives}"
DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"

INCLUDE_UWP=0
PLATFORMS=(Editor Android iOS Linux macOS Web Windows)
ARTIFACT_SUFFIX=""

usage() {
  cat <<'USAGE'
Usage: ./build.sh [options] [--platform PLATFORM ...]

Options:
  --unity-executable PATH  Unity executable to run.
  --unity-version VERSION  Use Unity version lookup instead of --unity-executable.
  --unity-base-path PATH   Base path for Unity version lookup.
  --include-uwp           Include UWP / WindowsStoreApps.
  --platform PLATFORM     Override default platforms. Can be repeated.
  --artifact-suffix TEXT  Copy the package to a suffixed filename after build.
  -h, --help              Show this help.

Defaults:
  Unity executable: /Volumes/Expansion/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity
  Platforms: Editor Android iOS Linux macOS Web Windows

Notes:
  UWP is opt-in because WindowsStoreApps is unsupported by Unity on macOS.
  Linux is passed through because Package.cs lists it, but the current Package.cs
  does not have a Linux build block.
USAGE
}

UNITY_ARGS=()
CUSTOM_PLATFORMS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --unity-executable)
      UNITY_EXECUTABLE="$2"
      shift 2
      ;;
    --unity-version)
      UNITY_ARGS+=(--unity-version "$2")
      UNITY_EXECUTABLE=""
      shift 2
      ;;
    --unity-base-path)
      UNITY_ARGS+=(--unity-base-path "$2")
      shift 2
      ;;
    --include-uwp)
      INCLUDE_UWP=1
      shift
      ;;
    --platform)
      CUSTOM_PLATFORMS+=("$2")
      shift 2
      ;;
    --artifact-suffix)
      ARTIFACT_SUFFIX="$2"
      shift 2
      ;;
    --artifact-suffix=*)
      ARTIFACT_SUFFIX="${1#*=}"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ ${#CUSTOM_PLATFORMS[@]} -gt 0 ]]; then
  PLATFORMS=("${CUSTOM_PLATFORMS[@]}")
elif [[ "$INCLUDE_UWP" == "1" ]]; then
  PLATFORMS=(Editor Android iOS Linux macOS UWP Web Windows)
fi

if [[ -n "$UNITY_EXECUTABLE" ]]; then
  if [[ ! -x "$UNITY_EXECUTABLE" ]]; then
    echo "Unity executable not found or not executable: $UNITY_EXECUTABLE" >&2
    exit 1
  fi
  UNITY_ARGS+=(--unity-executable "$UNITY_EXECUTABLE")
fi

PACKAGE_ARGS=(package "${UNITY_ARGS[@]}")
for platform in "${PLATFORMS[@]}"; do
  PACKAGE_ARGS+=(--platform "$platform")
done

export EZVCPKG_BASEDIR
export VCPKG_DEFAULT_BINARY_CACHE
export DOTNET_ROLL_FORWARD

cd "$SCRIPT_DIR"

echo "Using package root: $SCRIPT_DIR"
echo "Using EZVCPKG_BASEDIR: $EZVCPKG_BASEDIR"
echo "Using VCPKG_DEFAULT_BINARY_CACHE: $VCPKG_DEFAULT_BINARY_CACHE"
echo "Building platforms: ${PLATFORMS[*]}"

dotnet run --project Build~ -- "${PACKAGE_ARGS[@]}"

if [[ -n "$ARTIFACT_SUFFIX" ]]; then
  VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' package.json | head -n 1)"
  SOURCE_PACKAGE="$(cd "$SCRIPT_DIR/../.." && pwd)/com.cesium.unity-${VERSION}.tgz"
  PATCHED_PACKAGE="$(cd "$SCRIPT_DIR/../.." && pwd)/com.cesium.unity-${VERSION}${ARTIFACT_SUFFIX}.tgz"

  if [[ ! -f "$SOURCE_PACKAGE" ]]; then
    echo "Expected package not found: $SOURCE_PACKAGE" >&2
    exit 1
  fi

  cp "$SOURCE_PACKAGE" "$PATCHED_PACKAGE"
  echo "Copied patched package: $PATCHED_PACKAGE"
fi
