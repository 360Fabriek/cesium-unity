#ifndef CESIUM_INSTANCED_RASTER_INCLUDED
#define CESIUM_INSTANCED_RASTER_INCLUDED

// Per-draw bindings, never Shader.SetGlobal* state. Ordinary tiles use their
// existing UVs and do not enable these bindings.
float4x4 _CesiumInstanceWorldToChart;
float4 _CesiumInstanceGeodetic; // sin(phi0), cos(phi0), prime-vertical radius, e^2
float4 _CesiumInstanceRasterMapping[8]; // origin UV, UV per projection radian
float4 _CesiumInstanceRasterProjection[8]; // Mercator flag, reference Mercator angle
float4 _CesiumInstanceCoverage; // west/south/east/north, relative to chart origin
float _CesiumInstanceClipCoverage;

// Invert the oblate ellipsoid in an origin-relative ENU chart. In particular,
// do NOT reconstruct a single-precision six-million-metre ECEF position and
// subtract nearly equal latitudes/longitudes afterwards.
float3 CesiumInstanceAngularDelta(float3 enu)
{
    float s0 = _CesiumInstanceGeodetic.x;
    float c0 = _CesiumInstanceGeodetic.y;
    float n0 = _CesiumInstanceGeodetic.z;
    float e2 = _CesiumInstanceGeodetic.w;
    float dr = c0 * enu.z - s0 * enu.y;
    float dz = s0 * enu.z + c0 * enu.y;
    float radial = n0 * c0 + dr;
    float p = sqrt(radial * radial + enu.x * enu.x);
    // Rationalized difference hypot(radial, east) - referenceRadius.
    float dp = dr + enu.x * enu.x / max(p + radial, 1e-10);
    float longitudeDelta = atan2(enu.x, radial);
    float latitudeDelta = 0.0;
    [unroll] for (int i = 0; i < 4; ++i)
    {
        float halfSin = sin(latitudeDelta * 0.5);
        float ds = -2.0 * s0 * halfSin * halfSin + c0 * sin(latitudeDelta);
        float a = e2 * ds * (2.0 * s0 + ds) / (1.0 - e2 * s0 * s0);
        float t = sqrt(max(1.0 - a, 1e-10));
        float dn = n0 * a / (t * (1.0 + t));
        float correctedZ = dz + e2 * (n0 * ds + dn * (s0 + ds));
        latitudeDelta = atan2(c0 * correctedZ - s0 * dp,
            n0 + c0 * dp + s0 * correctedZ);
    }
    float h = sin(latitudeDelta * 0.5);
    float sineDelta = -2.0 * s0 * h * h + c0 * sin(latitudeDelta);
    float ratio = sineDelta / max(c0 * c0 - s0 * sineDelta, 1e-10);
    ratio = clamp(ratio, -0.9999999, 0.9999999);
    float squared = ratio * ratio;
    // atanh addition identity; the series avoids cancellation in log(1+epsilon).
    float mercatorDelta = abs(ratio) < 0.125
        ? ratio * (1.0 + squared * (1.0 / 3.0 + squared * (1.0 / 5.0 + squared / 7.0)))
        : 0.5 * log((1.0 + ratio) / (1.0 - ratio));
    return float3(longitudeDelta, latitudeDelta, mercatorDelta);
}

void CesiumRasterOverlay_float(float4 BaseColor, float CoordinateIndex,
    UnityTexture2D Raster, float4 TranslationScale, float3 AbsoluteWorld,
    float2 LegacyUV, out float4 Color)
{
    Color = BaseColor;
#ifndef SHADERGRAPH_PREVIEW
    float3 delta = 0.0;
    bool positionMapping = CoordinateIndex <= -2.0;
    if (positionMapping || _CesiumInstanceClipCoverage > 0.5)
    {
        float3 enu = mul(_CesiumInstanceWorldToChart, float4(AbsoluteWorld, 1.0)).xyz;
        delta = CesiumInstanceAngularDelta(enu);
        if (_CesiumInstanceClipCoverage > 0.5)
        {
            // Half-open coverage prevents adjacent upsampled children drawing the
            // same surface. These are geometry bounds, NOT ancestor-image bounds.
            if (delta.x < _CesiumInstanceCoverage.x || delta.y < _CesiumInstanceCoverage.y ||
                delta.x >= _CesiumInstanceCoverage.z || delta.y >= _CesiumInstanceCoverage.w)
                clip(-1.0);
        }
    }
    if (CoordinateIndex == -1.0 && _CesiumInstanceGeodetic.z > 0.0) return;
    float2 uv = LegacyUV * TranslationScale.zw + TranslationScale.xy;
    if (positionMapping)
    {
        int slot = (int)(-CoordinateIndex - 2.0);
        if (slot < 0 || slot >= 8) return;
        float4 mapping = _CesiumInstanceRasterMapping[slot];
        float4 projection = _CesiumInstanceRasterProjection[slot];
        float mercatorDelta = clamp(delta.z, -3.141592653589793 - projection.y,
            3.141592653589793 - projection.y);
        uv = mapping.xy + float2(delta.x, projection.x > 0.5 ? mercatorDelta : delta.y) * mapping.zw;
        // Do not smear an image's edge texels over geometry outside its coverage.
        // Absence of imagery is not itself a geometry-clipping instruction.
        if (any(uv < 0.0) || any(uv > 1.0)) return;
    }
    // Cesium's raster images are top-down; preserve the legacy V convention.
    uv.y = 1.0 - uv.y;
    float4 sampleColor = SAMPLE_TEXTURE2D(Raster.tex, Raster.samplerstate, Raster.GetTransformedUV(uv));
    Color = lerp(BaseColor, sampleColor, sampleColor.a);
#endif
}
#endif
