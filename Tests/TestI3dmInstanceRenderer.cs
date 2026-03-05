using CesiumForUnity;
using NUnit.Framework;
using System.Reflection;
using UnityEngine;

public class TestI3dmInstanceRenderer
{
    [Test]
    public void TryCreateLongitudeIntervalHandlesDatelinePoints()
    {
        bool created = I3dmInstanceRenderer.TryCreateLongitudeInterval(
            new[] { 179.0, -179.0, -178.0 },
            out I3dmInstanceRenderer.LongitudeInterval interval);

        Assert.IsTrue(created);
        Assert.IsTrue(interval.Contains(179.0));
        Assert.IsTrue(interval.Contains(-179.0));
        Assert.IsTrue(interval.Contains(-178.0));
        Assert.IsFalse(interval.Contains(0.0));
    }

    [Test]
    public void TryCreateLongitudeIntervalCreatesWrappedIntervalWhenNeeded()
    {
        bool created = I3dmInstanceRenderer.TryCreateLongitudeInterval(
            new[] { 350.0, 355.0, 5.0, 10.0 },
            out I3dmInstanceRenderer.LongitudeInterval interval);

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
        bool created = I3dmInstanceRenderer.TryCreateLongitudeInterval(
            new[] { 350.0, 10.0 },
            out I3dmInstanceRenderer.LongitudeInterval interval);
        Assert.IsTrue(created);

        Assert.IsTrue(I3dmInstanceRenderer.IsPointWithinBounds(0.0, 5.0, interval, 0.0, 10.0));
        Assert.IsFalse(I3dmInstanceRenderer.IsPointWithinBounds(180.0, 5.0, interval, 0.0, 10.0));
        Assert.IsFalse(I3dmInstanceRenderer.IsPointWithinBounds(0.0, -5.0, interval, 0.0, 10.0));
    }

    [Test]
    public void GeoreferenceChangedMarksClipCacheDirty()
    {
        GameObject gameObject = new GameObject("i3dm-test");
        try
        {
            I3dmInstanceRenderer renderer = gameObject.AddComponent<I3dmInstanceRenderer>();

            FieldInfo clipCacheDirtyField =
                typeof(I3dmInstanceRenderer).GetField("_clipCacheDirty", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo georeferenceChangedField =
                typeof(I3dmInstanceRenderer).GetField("_georeferenceChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo handleGeoreferenceChangedMethod =
                typeof(I3dmInstanceRenderer).GetMethod("HandleGeoreferenceChanged", BindingFlags.NonPublic | BindingFlags.Instance);

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
}
