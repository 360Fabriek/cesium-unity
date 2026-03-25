using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class TestCesiumPrimitiveFeatures
{
    [Test]
    public void GetFeatureIdForInstanceReturnsConfiguredIds()
    {
        GameObject go = new GameObject();
        CesiumPrimitiveFeatures primitiveFeatures = go.AddComponent<CesiumPrimitiveFeatures>();
        primitiveFeatures.featureIdSets = new CesiumFeatureIdSet[]
        {
            new CesiumFeatureIdSet(3)
        };

        primitiveFeatures.SetInstanceFeatureIds(0, new long[] { 10, 20, 30 });

        Assert.That(primitiveFeatures.GetFeatureIdForInstance(0), Is.EqualTo(10));
        Assert.That(primitiveFeatures.GetFeatureIdForInstance(1), Is.EqualTo(20));
        Assert.That(primitiveFeatures.GetFeatureIdForInstance(2), Is.EqualTo(30));

        Object.DestroyImmediate(go);
    }

    [Test]
    public void GetFeatureIdForInstanceHandlesInvalidIndices()
    {
        GameObject go = new GameObject();
        CesiumPrimitiveFeatures primitiveFeatures = go.AddComponent<CesiumPrimitiveFeatures>();
        primitiveFeatures.featureIdSets = new CesiumFeatureIdSet[]
        {
            new CesiumFeatureIdSet(2)
        };
        primitiveFeatures.SetInstanceFeatureIds(0, new long[] { 4, 5 });

        Assert.That(primitiveFeatures.GetFeatureIdForInstance(-1), Is.EqualTo(-1));
        Assert.That(primitiveFeatures.GetFeatureIdForInstance(2), Is.EqualTo(-1));
        Assert.That(primitiveFeatures.GetFeatureIdForInstance(0, 1), Is.EqualTo(-1));

        Object.DestroyImmediate(go);
    }

    [Test]
    public void GetFeatureIdFromRaycastHitReturnsInstanceFeatureForPickingProxy()
    {
        GameObject go = new GameObject();
        CesiumPrimitiveFeatures primitiveFeatures = go.AddComponent<CesiumPrimitiveFeatures>();
        primitiveFeatures.featureIdSets = new CesiumFeatureIdSet[]
        {
            new CesiumFeatureIdSet(2)
        };
        primitiveFeatures.SetInstanceFeatureIds(0, new long[] { 10, 20 });

        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0.0f),
            new Vector3(0.5f, -0.5f, 0.0f),
            new Vector3(0.0f, 0.5f, 0.0f)
        };
        mesh.triangles = new int[] { 0, 1, 2 };
        mesh.RecalculateBounds();

        Material material = new Material(Shader.Find("Sprites/Default"));

        InstancedTileRenderer renderer = go.AddComponent<InstancedTileRenderer>();
        renderer.Initialize(
            mesh,
            material,
            new Matrix4x4[]
            {
                Matrix4x4.Translate(new Vector3(-2.0f, 0.0f, 0.0f)),
                Matrix4x4.Translate(new Vector3(2.0f, 0.0f, 0.0f))
            },
            new Vector4[2],
            true);

        Physics.SyncTransforms();

        Ray ray = new Ray(new Vector3(2.0f, 0.0f, -2.0f), Vector3.forward);
        Assert.That(Physics.Raycast(ray, out RaycastHit hitInfo, 10.0f), Is.True);
        Assert.That(
            primitiveFeatures.GetFeatureIdFromRaycastHit(hitInfo),
            Is.EqualTo(20));

        LogAssert.NoUnexpectedReceived();

        Object.DestroyImmediate(go);
        Object.DestroyImmediate(material);
        Object.DestroyImmediate(mesh);
    }

    [Test]
    public void InstancedRaycastFeatureIdCanBeUsedToRetrieveInstanceMetadata()
    {
        GameObject go = new GameObject();
        CesiumPrimitiveFeatures primitiveFeatures = go.AddComponent<CesiumPrimitiveFeatures>();

        TestGltfModel model = new TestGltfModel();
        CesiumPropertyTableProperty nameProperty =
            model.AddStringPropertyTableProperty(new string[] { "oak", "pine" });

        CesiumFeatureIdSet featureIdSet = new CesiumFeatureIdSet(2);
        featureIdSet.propertyTableIndex = 0;
        primitiveFeatures.featureIdSets = new CesiumFeatureIdSet[] { featureIdSet };
        primitiveFeatures.SetInstanceFeatureIds(0, new long[] { 0, 1 });

        CesiumPropertyTable propertyTable = new CesiumPropertyTable();
        propertyTable.status = CesiumPropertyTableStatus.Valid;
        propertyTable.name = "trees";
        propertyTable.count = 2;
        propertyTable.properties =
            new System.Collections.Generic.Dictionary<string, CesiumPropertyTableProperty>
            {
                { "name", nameProperty }
            };

        CesiumModelMetadata modelMetadata = go.AddComponent<CesiumModelMetadata>();
        modelMetadata.propertyTables = new CesiumPropertyTable[] { propertyTable };

        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0.0f),
            new Vector3(0.5f, -0.5f, 0.0f),
            new Vector3(0.0f, 0.5f, 0.0f)
        };
        mesh.triangles = new int[] { 0, 1, 2 };
        mesh.RecalculateBounds();

        Material material = new Material(Shader.Find("Sprites/Default"));

        InstancedTileRenderer renderer = go.AddComponent<InstancedTileRenderer>();
        renderer.Initialize(
            mesh,
            material,
            new Matrix4x4[]
            {
                Matrix4x4.Translate(new Vector3(-2.0f, 0.0f, 0.0f)),
                Matrix4x4.Translate(new Vector3(2.0f, 0.0f, 0.0f))
            },
            new Vector4[2],
            true);

        Physics.SyncTransforms();

        Ray ray = new Ray(new Vector3(2.0f, 0.0f, -2.0f), Vector3.forward);
        Assert.That(Physics.Raycast(ray, out RaycastHit hitInfo, 10.0f), Is.True);

        long featureId = primitiveFeatures.GetFeatureIdFromRaycastHit(hitInfo);
        Assert.That(featureId, Is.EqualTo(1));

        CesiumFeatureIdSet hitFeatureIdSet = primitiveFeatures.featureIdSets[0];
        var values = modelMetadata.propertyTables[hitFeatureIdSet.propertyTableIndex]
            .GetMetadataValuesForFeature(featureId);

        Assert.That(values.ContainsKey("name"), Is.True);
        Assert.That(values["name"].GetString(), Is.EqualTo("pine"));

        LogAssert.NoUnexpectedReceived();

        model.Dispose();
        Object.DestroyImmediate(go);
        Object.DestroyImmediate(material);
        Object.DestroyImmediate(mesh);
    }
}
