using CesiumForUnity;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

#if SUPPORTS_SPLINES
using Unity.Mathematics;
using UnityEngine.Splines;
#endif

public class TestCesiumLockedPolygon
{
#if SUPPORTS_SPLINES
    [Test]
    public void SyncLockedPolygonCopiesExternalSplineToLocalPolygon()
    {
        GameObject go = new GameObject("Locked Polygon");
        go.AddComponent<CesiumGeoreference>().SetOriginLongitudeLatitudeHeight(12.0, 23.0, 456.0);
        CesiumCartographicPolygon polygon = go.AddComponent<CesiumCartographicPolygon>();
        CesiumLockedPolygon locked = go.AddComponent<CesiumLockedPolygon>();
        SplineContainer localSpline = go.GetComponent<SplineContainer>();

        GameObject sourceObject = new GameObject("Source Spline");
        sourceObject.transform.position = new Vector3(10.0f, 0.0f, 20.0f);
        SplineContainer sourceSpline = sourceObject.AddComponent<SplineContainer>();

        IReadOnlyList<Spline> sourceSplines = sourceSpline.Splines;
        for (int i = sourceSplines.Count - 1; i >= 0; --i)
        {
            sourceSpline.RemoveSpline(sourceSplines[i]);
        }

        Spline spline = new Spline();
        spline.Knots = new BezierKnot[]
        {
            new BezierKnot(new float3(-1.0f, 0.0f, -1.0f)),
            new BezierKnot(new float3(1.0f, 0.0f, -1.0f)),
            new BezierKnot(new float3(1.0f, 0.0f, 1.0f)),
            new BezierKnot(new float3(-1.0f, 0.0f, 1.0f))
        };
        spline.Closed = true;
        spline.SetTangentMode(TangentMode.Linear);
        sourceSpline.AddSpline(spline);

        locked.sourceSpline = sourceSpline;
        locked.SyncLockedPolygon();

        Assert.IsNotNull(polygon);
        Assert.AreEqual(1, localSpline.Splines.Count);

        BezierKnot[] mirrored = localSpline.Splines[0].ToArray();
        Assert.AreEqual(4, mirrored.Length);

        Vector3 worldPoint = sourceObject.transform.TransformPoint((Vector3)spline.ToArray()[0].Position);
        Vector3 mirroredWorldPoint = go.transform.TransformPoint((Vector3)mirrored[0].Position);
        Assert.That(Vector3.Distance(worldPoint, mirroredWorldPoint), Is.LessThan(0.001f));
    }
#endif
}
