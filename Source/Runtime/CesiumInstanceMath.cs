using System;
using Unity.Mathematics;
using UnityEngine;

namespace CesiumForUnity
{
    /// <summary>Double-precision placement and origin-relative raster projection.</summary>
    internal static class CesiumInstanceMath
    {
        internal static double4x4 ToDouble(Matrix4x4 m)
        {
            return new double4x4(
                new double4(m.m00, m.m10, m.m20, m.m30),
                new double4(m.m01, m.m11, m.m21, m.m31),
                new double4(m.m02, m.m12, m.m22, m.m32),
                new double4(m.m03, m.m13, m.m23, m.m33));
        }

        internal static Matrix4x4 ToFloat(double4x4 m)
        {
            return new Matrix4x4(
                new Vector4((float)m.c0.x, (float)m.c0.y, (float)m.c0.z, (float)m.c0.w),
                new Vector4((float)m.c1.x, (float)m.c1.y, (float)m.c1.z, (float)m.c1.w),
                new Vector4((float)m.c2.x, (float)m.c2.y, (float)m.c2.z, (float)m.c2.w),
                new Vector4((float)m.c3.x, (float)m.c3.y, (float)m.c3.z, (float)m.c3.w));
        }

        // abs(M) * extents includes all eight corners, including mirrored/sheared placements.
        internal static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
        {
            Vector3 e = bounds.extents;
            Vector3 extents = new Vector3(
                Mathf.Abs(matrix.m00) * e.x + Mathf.Abs(matrix.m01) * e.y + Mathf.Abs(matrix.m02) * e.z,
                Mathf.Abs(matrix.m10) * e.x + Mathf.Abs(matrix.m11) * e.y + Mathf.Abs(matrix.m12) * e.z,
                Mathf.Abs(matrix.m20) * e.x + Mathf.Abs(matrix.m21) * e.y + Mathf.Abs(matrix.m22) * e.z);
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), 2.0f * extents);
        }

        // Both boxes enclose the actual geometry/coverage. Reject only a proven
        // separation; overlap never removes an instance merely because its origin
        // is outside the coverage. The shader resolves the actual boundary.
        internal static bool IntersectsCoverage(double4x4 meshToChart, Bounds meshBounds,
            double4x4 coverageBoxToChart)
        {
            double3 localCenter = (double3)(float3)meshBounds.center;
            double3 e = (double3)(float3)meshBounds.extents + math.max(new double3(1e-5), math.abs(localCenter) * 1e-6);
            double3 center = math.mul(meshToChart, new double4(localCenter, 1)).xyz;
            double3 extent = math.abs(meshToChart.c0.xyz) * e.x + math.abs(meshToChart.c1.xyz) * e.y + math.abs(meshToChart.c2.xyz) * e.z;
            double3 coverageExtent = math.abs(coverageBoxToChart.c0.xyz) + math.abs(coverageBoxToChart.c1.xyz) + math.abs(coverageBoxToChart.c2.xyz);
            return math.all(math.abs(center - coverageBoxToChart.c3.xyz) <= extent + coverageExtent + 0.01);
        }

        internal static double4x4 ChartToEcef(double longitude, double latitude, double3 radii)
        {
            double s = Math.Sin(latitude), c = Math.Cos(latitude);
            double sl = Math.Sin(longitude), cl = Math.Cos(longitude);
            double e2 = 1.0 - radii.z * radii.z / (radii.x * radii.x);
            double n = radii.x / Math.Sqrt(1.0 - e2 * s * s);
            return new double4x4(
                new double4(-sl, cl, 0.0, 0.0),
                new double4(-s * cl, -s * sl, c, 0.0),
                new double4(c * cl, c * sl, s, 0.0),
                new double4(n * c * cl, n * c * sl, n * (1.0 - e2) * s, 1.0));
        }

        internal static double MercatorAngle(double latitude)
        {
            // Match WebMercatorProjection's maximum latitude (atan(sinh(pi))).
            latitude = Math.Max(-1.4844222297453324, Math.Min(1.4844222297453324, latitude));
            return Math.Log(Math.Tan(Math.PI * 0.25 + latitude * 0.5));
        }

        internal static Vector4 RasterMapping(double longitude, double latitude,
            double4 rectangle, bool webMercator, double radius)
        {
            double width = rectangle.z - rectangle.x, height = rectangle.w - rectangle.y;
            if (!(width > 0.0) || !(height > 0.0) || !(radius > 0.0))
                throw new ArgumentException("Raster rectangle and projection radius must be positive.");
            // Choose the equivalent longitude nearest the centre of the attached image.
            double centreLongitude = (rectangle.x + width * 0.5) / radius;
            longitude += Math.Round((centreLongitude - longitude) / (2.0 * Math.PI)) * 2.0 * Math.PI;
            return new Vector4(
                (float)((longitude * radius - rectangle.x) / width),
                (float)(((webMercator ? MercatorAngle(latitude) : latitude) * radius - rectangle.y) / height),
                (float)(radius / width), (float)(radius / height));
        }
    }
}
