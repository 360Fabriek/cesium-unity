using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

public class TestCesiumInstancedRenderer
{
    private static readonly double3 Radii = new double3(6378137.0, 6378137.0, 6356752.3142451793);

    [Test]
    public void MatrixConversionPreservesColumns()
    {
        Matrix4x4 m = Matrix4x4.TRS(new Vector3(3, 5, 7), Quaternion.Euler(10, 20, 30), new Vector3(-2, 3, 4));
        Assert.AreEqual(m, CesiumInstanceMath.ToFloat(CesiumInstanceMath.ToDouble(m)));
    }

    [Test]
    public void TransformedBoundsContainEveryCornerWithReflectionAndShear()
    {
        Matrix4x4 m = Matrix4x4.TRS(new Vector3(-5, 6, 9), Quaternion.Euler(40, 30, 20), new Vector3(-2, 3, 1));
        m.m01 += 0.75f;
        Bounds input = new Bounds(new Vector3(1, 2, 3), new Vector3(4, 8, 2));
        Bounds output = CesiumInstanceMath.TransformBounds(m, input);
        output.Expand(0.0001f);
        for (int i = 0; i < 8; ++i)
        {
            Vector3 sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            Assert.IsTrue(output.Contains(m.MultiplyPoint3x4(input.center + Vector3.Scale(sign, input.extents))));
        }
    }

    [Test]
    public void BoundsIncludeVisibleCanopyWhenInstanceOriginIsOutsideCoverage()
    {
        // The instance origin is left of x=0, but the mesh extends into x>=0.
        var m = Matrix4x4.Translate(new Vector3(-1, 0, 0));
        var bounds = CesiumInstanceMath.TransformBounds(m, new Bounds(Vector3.zero, Vector3.one * 4));
        Assert.Less(m.m03, 0);
        Assert.Greater(bounds.max.x, 0);
    }

    [Test]
    public void CoverageCullingKeepsStraddlersButRejectsFullySeparatedBounds()
    {
        double4x4 coverage = double4x4.identity;
        coverage.c3.x = 3.0; // Box spans x=2..4.
        var mesh = new Bounds(Vector3.zero, Vector3.one * 4);
        double4x4 instance = double4x4.identity;
        instance.c3.x = 1.0; // Origin is outside, but rightmost geometry is x=3.
        Assert.IsTrue(CesiumInstanceMath.IntersectsCoverage(instance, mesh, coverage));
        instance.c3.x = -1.0; // Rightmost geometry is x=1, separated from coverage.
        Assert.IsFalse(CesiumInstanceMath.IntersectsCoverage(instance, mesh, coverage));
    }

    [Test]
    public void ChartOriginAgreesWithEllipsoidAndAxesAreOrthonormal()
    {
        double longitude = 4.48 * Math.PI / 180, latitude = 51.92 * Math.PI / 180;
        double4x4 chart = CesiumInstanceMath.ChartToEcef(longitude, latitude, Radii);
        double3x3 axes = new double3x3(chart.c0.xyz, chart.c1.xyz, chart.c2.xyz);
        Assert.That(math.determinant(axes), Is.EqualTo(1).Within(1e-12));
        Assert.That(math.dot(axes.c0, axes.c1), Is.EqualTo(0).Within(1e-12));
        double surface = math.csum(chart.c3.xyz * chart.c3.xyz / (Radii * Radii));
        Assert.That(surface, Is.EqualTo(1).Within(1e-12));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RasterMappingUsesAttachedImageBoundsNotChildBounds(bool mercator)
    {
        double radius = Radii.x, longitude = 0.1, latitude = 0.5;
        double y = (mercator ? CesiumInstanceMath.MercatorAngle(latitude) : latitude) * radius;
        var parentImage = new double4(longitude * radius - 40, y - 80, longitude * radius + 120, y + 240);
        Vector4 mapping = CesiumInstanceMath.RasterMapping(longitude, latitude, parentImage, mercator, radius);
        Assert.That(mapping.x, Is.EqualTo(0.25).Within(1e-6));
        Assert.That(mapping.y, Is.EqualTo(0.25).Within(1e-6));
        Assert.That(mapping.z, Is.EqualTo(radius / 160).Within(0.01));
    }

    [Test]
    public void RasterMappingChoosesCorrectSideOfAntimeridian()
    {
        double radius = Radii.x;
        Vector4 mapping = CesiumInstanceMath.RasterMapping(-Math.PI + 0.005, 0,
            new double4(Math.PI * radius, -100, (Math.PI + 0.01) * radius, 100), false, radius);
        Assert.That(mapping.x, Is.EqualTo(0.5).Within(1e-5));
    }

    [Test]
    public void InvalidRasterExtentIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CesiumInstanceMath.RasterMapping(0, 0, new double4(0), false, Radii.x));
    }

    [Test]
    public void BatchesKeepCrossingInstancesAndRasterReplacementIsIdentitySafe()
    {
        GameObject root = new GameObject("Instancing test");
        root.SetActive(false);
        Mesh mesh = null;
        Material material = null;
        Texture2D ancestor = null, replacement = null;
        try
        {
            var georeference = root.AddComponent<CesiumGeoreference>();
            georeference.Initialize();
            var tile = new GameObject("tile");
            tile.transform.SetParent(root.transform, false);
            tile.AddComponent<Cesium3DTileset>();
            var primitive = new GameObject("primitive");
            primitive.transform.SetParent(tile.transform, false);
            mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-2, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 0, 2) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();
            primitive.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = Object.Instantiate(Resources.Load<Material>("CesiumUnlitTilesetMaterial"));
            primitive.AddComponent<MeshRenderer>().sharedMaterial = material;
            var anchor = primitive.AddComponent<CesiumGlobeAnchor>();
            anchor.detectTransformChanges = false;
            anchor.adjustOrientationForGlobeWhenMoving = false;
            anchor.localToGlobeFixedMatrix = CesiumInstanceMath.ChartToEcef(0, 0, Radii);
            // 129 placements force at least two batches. No origin-based coverage cull.
            var placements = new double4x4[129];
            for (int i = 0; i < placements.Length; ++i)
            {
                placements[i] = double4x4.identity;
                placements[i].c3.x = -1;
            }
            GCHandle pin = GCHandle.Alloc(placements, GCHandleType.Pinned);
            try
            {
                CesiumInstancedRenderer.Configure(primitive, pin.AddrOfPinnedObject().ToInt64(),
                    placements.Length, Radii, new double4(0, -0.001, 0.001, 0.001),
                    new double4(0, -0.001, 0.001, 0.001),
                    math.mul(anchor.localToGlobeFixedMatrix, new double4x4(
                        new double4(10000, 0, 0, 0), new double4(0, 10000, 0, 0),
                        new double4(0, 0, 10000, 0), new double4(0, 0, 0, 1))), true);
            }
            finally { pin.Free(); }
            var renderer = primitive.GetComponent<CesiumInstancedRenderer>();
            var batches = (ICollection)Field(renderer, "_batches");
            Assert.AreEqual(2, batches.Count);
            Assert.IsFalse(primitive.GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(1, root.GetComponentsInChildren<CesiumGlobeAnchor>(true).Length);
            var properties = (MaterialPropertyBlock)Field(renderer, "_properties");
            int coordinate = Shader.PropertyToID("_overlayTextureCoordinateIndex_Clipping");
            int texture = Shader.PropertyToID("_overlayTexture_Clipping");
            Assert.AreEqual(-1.0f, properties.GetFloat(coordinate));
            Vector4 coverage = properties.GetVector("_CesiumInstanceCoverage");
            ancestor = new Texture2D(1, 1);
            replacement = new Texture2D(2, 2);
            var image = new double4(-10000, -10000, 10000, 10000);
            CesiumInstancedRenderer.AttachRaster(tile, "Clipping", ancestor, image, false, Radii.x);
            CesiumInstancedRenderer.AttachRaster(tile, "Clipping", replacement, image, false, Radii.x);
            CesiumInstancedRenderer.DetachRaster(tile, "Clipping", ancestor);
            Assert.AreEqual(replacement, properties.GetTexture(texture));
            Assert.LessOrEqual(properties.GetFloat(coordinate), -2);
            Assert.AreEqual(coverage, properties.GetVector("_CesiumInstanceCoverage"));
            CesiumInstancedRenderer.DetachRaster(tile, "Clipping", replacement);
            Assert.AreEqual(-1.0f, properties.GetFloat(coordinate));
            Assert.IsTrue(ancestor != null && replacement != null, "Overlay textures must not be destroyed by renderers.");
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (material != null) Object.DestroyImmediate(material);
            if (ancestor != null) Object.DestroyImmediate(ancestor);
            if (replacement != null) Object.DestroyImmediate(replacement);
        }
    }

    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }
}
