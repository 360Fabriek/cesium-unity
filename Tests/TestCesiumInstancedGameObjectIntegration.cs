using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

public class TestCesiumInstancedGameObjectIntegration
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly double3 Radii = new double3(6378137.0, 6378137.0, 6356752.3142451793);

    private sealed class Fixture : IDisposable
    {
        internal GameObject root, tile, primitive;
        internal CesiumGeoreference georeference;
        internal CesiumGlobeAnchor anchor;
        internal Mesh mesh;
        internal Material material;
        internal Texture2D ancestor, replacement;
        internal CesiumInstancedRenderer renderer;
        internal double4x4 baseEcef;
        internal double4x4[] placements;

        internal Fixture()
        {
            root = new GameObject("Instanced integration test");
            root.SetActive(false); // Do not start network tile loading in these tests.
            try
            {
                georeference = root.AddComponent<CesiumGeoreference>();
                georeference.Initialize();
                tile = new GameObject("tile");
                tile.transform.SetParent(root.transform, false);
                tile.AddComponent<Cesium3DTileset>();
                primitive = new GameObject("prototype");
                primitive.transform.SetParent(tile.transform, false);
                mesh = new Mesh();
                mesh.vertices = new[] { new Vector3(-2, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 0, 2) };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.RecalculateBounds();
                primitive.AddComponent<MeshFilter>().sharedMesh = mesh;
                material = Object.Instantiate(Resources.Load<Material>("CesiumUnlitTilesetMaterial"));
                var source = primitive.AddComponent<MeshRenderer>();
                source.sharedMaterial = material;
                var properties = new MaterialPropertyBlock();
                properties.SetFloat("_IntegrationUserProperty", 0.375f);
                source.SetPropertyBlock(properties);
                anchor = primitive.AddComponent<CesiumGlobeAnchor>();
                anchor.detectTransformChanges = false;
                anchor.adjustOrientationForGlobeWhenMoving = false;
                baseEcef = CesiumInstanceMath.ChartToEcef(0, 0, Radii);
                anchor.localToGlobeFixedMatrix = baseEcef;
                placements = new[] { double4x4.identity, double4x4.identity };
                placements[0].c3.x = 1;  // Crosses into the x=2..4 coverage box.
                placements[1].c3.x = -5; // Fully separated from that box.
                Configure();
                renderer = primitive.GetComponent<CesiumInstancedRenderer>();
                ancestor = new Texture2D(1, 1);
                replacement = new Texture2D(2, 2);
            }
            catch { Dispose(); throw; }
        }

        internal void Configure()
        {
            GCHandle pin = GCHandle.Alloc(placements, GCHandleType.Pinned);
            try
            {
                var box = double4x4.identity;
                box.c3.x = 3; // x=2..4, y/z=-1..1 in the shared ENU chart.
                CesiumInstancedRenderer.Configure(primitive, pin.AddrOfPinnedObject().ToInt64(),
                    placements.Length, Radii, new double4(2 / Radii.x, -0.001, 4 / Radii.x, 0.001),
                    new double4(-0.001, -0.001, 0.001, 0.001), math.mul(baseEcef, box), true);
            }
            finally { pin.Free(); }
        }

        internal MaterialPropertyBlock Properties => (MaterialPropertyBlock)Field(renderer, "_properties");
        internal void Attach(Texture texture) => renderer.SetRaster("Clipping", texture,
            new double4(-10000, -10000, 10000, 10000), false, Radii.x);
        internal bool UpdateTransforms() => (bool)Invoke(renderer, "UpdateTransforms");
        internal int PlacementCount
        {
            get
            {
                int count = 0;
                foreach (object batch in (IEnumerable)Field(renderer, "_batches"))
                    count += ((int[])batch.GetType().GetField("indices", PrivateInstance | BindingFlags.Public).GetValue(batch)).Length;
                return count;
            }
        }

        public void Dispose()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (material != null) Object.DestroyImmediate(material);
            if (ancestor != null) Object.DestroyImmediate(ancestor);
            if (replacement != null) Object.DestroyImmediate(replacement);
        }
    }

    [Test]
    public void IntegrationKeepsPrototypeHierarchyAndUserPropertyBlock()
    {
        using (var f = new Fixture())
        {
            Assert.AreEqual(3, f.root.GetComponentsInChildren<Transform>(true).Length);
            Assert.AreEqual(1, f.root.GetComponentsInChildren<CesiumGlobeAnchor>(true).Length);
            Assert.AreSame(f.mesh, f.primitive.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(f.material, f.primitive.GetComponent<MeshRenderer>().sharedMaterial);
            Assert.IsFalse(f.primitive.GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(0.375f, f.Properties.GetFloat("_IntegrationUserProperty"));
            Assert.AreEqual(1, f.PlacementCount, "The straddler stays; only the wholly separated instance is omitted.");
        }
    }

    [Test]
    public void DuplicateConfigurationDoesNotAddASecondRenderer()
    {
        using (var f = new Fixture())
        {
            Assert.Throws<InvalidOperationException>(() => f.Configure());
            Assert.AreEqual(1, f.primitive.GetComponents<CesiumInstancedRenderer>().Length);
        }
    }

    [Test]
    public void ReleaseStopsSubmissionAndIsIdempotentWithoutDestroyingBorrowedResources()
    {
        using (var f = new Fixture())
        {
            f.Attach(f.ancestor);
            CesiumInstancedRenderer.Release(f.tile);
            CesiumInstancedRenderer.Release(f.tile);
            Assert.IsTrue((bool)Field(f.renderer, "_released"));
            Assert.IsFalse((bool)Field(f.renderer, "_initialized"));
            Assert.IsFalse(f.tile.activeSelf);
            Assert.AreEqual(0, f.PlacementCount);
            Assert.AreEqual(0, ((Array)Field(f.renderer, "_instances")).Length);
            Assert.IsFalse(f.UpdateTransforms());
            f.Attach(f.replacement); // Late callbacks must not resurrect a freed tile.
            Assert.IsTrue(f.Properties.isEmpty);
            Assert.IsTrue(f.mesh != null && f.material != null && f.ancestor != null && f.replacement != null);
        }
    }

    [Test]
    public void DisableEnableCallbacksRetainRasterAndCoverageData()
    {
        using (var f = new Fixture())
        {
            f.Attach(f.ancestor);
            Vector4 coverage = f.Properties.GetVector("_CesiumInstanceCoverage");
            Invoke(f.renderer, "OnDisable");
            Invoke(f.renderer, "OnEnable");
            Assert.AreEqual(f.ancestor, f.Properties.GetTexture("_overlayTexture_Clipping"));
            Assert.AreEqual(coverage, f.Properties.GetVector("_CesiumInstanceCoverage"));
            Assert.IsFalse((bool)Field(f.renderer, "_released"));
            Assert.AreEqual(1, f.PlacementCount);
        }
    }

    [Test]
    public void InvalidRasterReplacementDoesNotLoseThePreviousBinding()
    {
        using (var f = new Fixture())
        {
            f.Attach(f.ancestor);
            Assert.Throws<ArgumentException>(() => f.renderer.SetRaster("Clipping", f.replacement,
                new double4(0, 0, double.PositiveInfinity, 1), false, Radii.x));
            f.renderer.RemoveRaster("Clipping", f.replacement);
            Assert.AreEqual(f.ancestor, f.Properties.GetTexture("_overlayTexture_Clipping"));
            f.renderer.RemoveRaster("Clipping", f.ancestor);
            Assert.AreEqual(-1.0f, f.Properties.GetFloat("_overlayTextureCoordinateIndex_Clipping"));
        }
    }

    [Test]
    public void MovingThePrototypeReconsidersPreviouslyExcludedPlacements()
    {
        using (var f = new Fixture())
        {
            Assert.IsTrue(f.UpdateTransforms());
            Vector4 coverage = f.Properties.GetVector("_CesiumInstanceCoverage");
            for (int iteration = 0; iteration < 3; ++iteration)
            {
                double4x4 moved = f.baseEcef;
                moved.c3 += moved.c0 * 5.0;
                f.anchor.localToGlobeFixedMatrix = moved;
                Assert.IsTrue(f.UpdateTransforms());
                Assert.AreEqual(2, f.PlacementCount, "A stale original-height box cannot cull moved geometry.");
                f.anchor.localToGlobeFixedMatrix = f.baseEcef;
                Assert.IsTrue(f.UpdateTransforms());
                Assert.AreEqual(1, f.PlacementCount, "Rebuilding must replace batches, not append them.");
            }
            Assert.AreEqual(coverage, f.Properties.GetVector("_CesiumInstanceCoverage"));
        }
    }

    [Test]
    public void ChangingPrototypeBoundsReconsidersPreviouslyExcludedPlacements()
    {
        using (var f = new Fixture())
        {
            Assert.IsTrue(f.UpdateTransforms());
            f.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100);
            Assert.IsTrue(f.UpdateTransforms());
            Assert.AreEqual(2, f.PlacementCount);
        }
    }

    [Test]
    public void OriginShiftReusesBatchMembershipAndMatrixArrays()
    {
        using (var f = new Fixture())
        {
            Assert.IsTrue(f.UpdateTransforms());
            var batches = (IList)Field(f.renderer, "_batches");
            object firstBatch = batches[0];
            object matrices = firstBatch.GetType().GetField("matrices", PrivateInstance | BindingFlags.Public).GetValue(firstBatch);
            f.georeference.SetOriginEarthCenteredEarthFixed(Radii.x, 100, 0);
            Assert.IsTrue(f.UpdateTransforms());
            Assert.AreSame(firstBatch, batches[0]);
            Assert.AreSame(matrices, firstBatch.GetType().GetField("matrices", PrivateInstance | BindingFlags.Public).GetValue(firstBatch));
            Assert.AreEqual(1, f.PlacementCount);
        }
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void NonFiniteRasterBoundsAreRejected(double bad)
    {
        Assert.Throws<ArgumentException>(() => CesiumInstanceMath.RasterMapping(0, 0,
            new double4(0, 0, bad, 1), false, Radii.x));
    }

    [Test]
    public void ShaderMappingOverflowIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CesiumInstanceMath.RasterMapping(0, 0,
            new double4(0, 0, 1e-300, 1), false, Radii.x));
    }

    [Test]
    public void TransformValidationAllowsReflectionAndRoundoffButRejectsInvalidData()
    {
        var m = double4x4.identity;
        m.c0.x = -2;
        m.c3.w = 1.0 + 2e-16;
        Assert.IsTrue(CesiumInstanceMath.IsUsableTransform(m));
        m.c0.x = 0;
        Assert.IsFalse(CesiumInstanceMath.IsUsableTransform(m));
        m.c0.x = double.NaN;
        Assert.IsFalse(CesiumInstanceMath.IsUsableTransform(m));
        m = double4x4.identity;
        m.c0.w = 0.25;
        Assert.IsFalse(CesiumInstanceMath.IsUsableTransform(m));
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, PrivateInstance).GetValue(target);

    private static object Invoke(object target, string name) =>
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, null);
}
