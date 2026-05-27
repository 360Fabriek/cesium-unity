# 360Fabriek Patched Redistribution Modifications

This package is a modified redistribution of Cesium for Unity from
https://github.com/CesiumGS/cesium-unity.

The upstream project is licensed under the Apache License, Version 2.0. The
license text is preserved in `LICENSE`, and attribution and redistribution
notes are preserved in `NOTICE`.

Prominent modification summary:

- Added build scripts: `build.sh` and `build.ps1`.
- Updated package build automation to include `NOTICE` and `MODIFICATIONS.md`.
- Updated package metadata and README text to identify this as a 360Fabriek
  patched redistribution rather than an official CesiumGS release.
- Updated build support for Unity 6000.3.13f1, including compatibility handling
  for URP player builds.
- Updated Reinterop build configuration to avoid stale or duplicate generated
  source files during packaging.
- Updated native build behavior for local macOS packaging and Expansion volume
  cache paths.
- Preserved upstream CesiumGS Apache-2.0 licensing and third-party attributions.

Unless otherwise noted, modifications are Copyright 2026 360Fabriek and are
distributed under the Apache License, Version 2.0.
