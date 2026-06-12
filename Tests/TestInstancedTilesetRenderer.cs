using CesiumForUnity;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using Unity.Mathematics;
using UnityEngine;

public class TestInstancedTilesetRenderer
{
    [Test]
    public void TryCreateLongitudeIntervalHandlesDatelinePoints()
    {
        bool created = InstancedTilesetRenderer.TryCreateLongitudeInterval(
            new[] { 179.0, -179.0, -178.0 },
            out InstancedTilesetRenderer.LongitudeInterval interval);

        Assert.IsTrue(created);
        Assert.IsTrue(interval.Contains(179.0));
        Assert.IsTrue(interval.Contains(-179.0));
        Assert.IsTrue(interval.Contains(-178.0));
        Assert.IsFalse(interval.Contains(0.0));
    }

    [Test]
    public void TryCreateLongitudeIntervalCreatesWrappedIntervalWhenNeeded()
    {
        bool created = InstancedTilesetRenderer.TryCreateLongitudeInterval(
            new[] { 350.0, 355.0, 5.0, 10.0 },
            out InstancedTilesetRenderer.LongitudeInterval interval);

        Assert.IsTrue(created);
        Assert.IsTrue(interval.wraps);
        Assert.IsTrue(interval.Contains(0.0));
        Assert.IsTrue(interval.Contains(358.0));
        Assert.IsTrue(interval.Contains(7.0));
        Assert.IsFalse(interval.Contains(180.0));
    }

    [Test]
    public void IsPointWithinBoundsPerformsConservativeBoundsFiltering()
    {
        bool created = InstancedTilesetRenderer.TryCreateLongitudeInterval(
            new[] { 350.0, 10.0 },
            out InstancedTilesetRenderer.LongitudeInterval interval);
        Assert.IsTrue(created);

        Assert.IsTrue(InstancedTilesetRenderer.IsPointWithinBounds(0.0, 5.0, interval, 0.0, 10.0));
        Assert.IsFalse(InstancedTilesetRenderer.IsPointWithinBounds(180.0, 5.0, interval, 0.0, 10.0));
        Assert.IsFalse(InstancedTilesetRenderer.IsPointWithinBounds(0.0, -5.0, interval, 0.0, 10.0));
    }

    [Test]
    public void GeoreferenceChangedMarksClipCacheDirty()
    {
        GameObject gameObject = new GameObject("i3dm-test");
        try
        {
            InstancedTilesetRenderer renderer = gameObject.AddComponent<InstancedTilesetRenderer>();

            FieldInfo clipCacheDirtyField =
                typeof(InstancedTilesetRenderer).GetField("_clipCacheDirty", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo georeferenceChangedField =
                typeof(InstancedTilesetRenderer).GetField("_georeferenceChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo handleGeoreferenceChangedMethod =
                typeof(InstancedTilesetRenderer).GetMethod("HandleGeoreferenceChanged", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(clipCacheDirtyField);
            Assert.IsNotNull(georeferenceChangedField);
            Assert.IsNotNull(handleGeoreferenceChangedMethod);

            clipCacheDirtyField.SetValue(renderer, false);
            georeferenceChangedField.SetValue(renderer, false);

            handleGeoreferenceChangedMethod.Invoke(renderer, null);

            Assert.IsTrue((bool)clipCacheDirtyField.GetValue(renderer));
            Assert.IsTrue((bool)georeferenceChangedField.GetValue(renderer));
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void BoxRaycastCollidersExposeInstanceIdentity()
    {
        GameObject gameObject = new GameObject("i3dm-raycast-test");
        try
        {
            InstancedTilesetRenderer renderer = gameObject.AddComponent<InstancedTilesetRenderer>();
            renderer.colliderMode = InstancedTilesetRenderer.RaycastColliderMode.BoxPerInstance;

            Mesh mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f)
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7
            };
            mesh.RecalculateBounds();

            renderer.AddInstanceGroup(
                "group",
                mesh,
                null,
                new List<double4x4> { double4x4.identity },
                3);

            InstancedTilesetRaycastHit hit =
                gameObject.GetComponentInChildren<InstancedTilesetRaycastHit>(true);

            Assert.IsNotNull(hit);
            Assert.AreSame(renderer, hit.instancedRenderer);
            Assert.AreEqual("group", hit.groupId);
            Assert.AreEqual(3, hit.primitiveIndex);
            Assert.AreEqual(0, hit.instanceIndex);
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ReplacingInstanceGroupDestroysOldRaycastColliders()
    {
        GameObject gameObject = new GameObject("i3dm-replace-test");
        try
        {
            InstancedTilesetRenderer renderer = gameObject.AddComponent<InstancedTilesetRenderer>();
            renderer.colliderMode = InstancedTilesetRenderer.RaycastColliderMode.BoxPerInstance;

            Mesh mesh = new Mesh();
            mesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.right,
                Vector3.up
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();

            renderer.AddInstanceGroup(
                "group",
                mesh,
                null,
                new List<double4x4> { double4x4.identity, double4x4.identity });
            renderer.AddInstanceGroup(
                "group",
                mesh,
                null,
                new List<double4x4> { double4x4.identity });

            InstancedTilesetRaycastHit[] hits =
                gameObject.GetComponentsInChildren<InstancedTilesetRaycastHit>(true);

            Assert.AreEqual(1, hits.Length);
            Assert.AreEqual(0, hits[0].instanceIndex);
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

}
