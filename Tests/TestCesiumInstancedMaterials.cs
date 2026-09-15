using System;
using System.Linq;
using System.Reflection;
using System.Text;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class TestCesiumInstancedMaterials
{
    private GameObject _prototype;
    private Material _material;
    private Texture2D _texture;
    private MeshRenderer _source;

    [SetUp]
    public void SetUp()
    {
        _prototype = new GameObject("Material parity test");
        _prototype.SetActive(false);
        var template = Resources.Load<Material>("CesiumDefaultTilesetMaterial");
        Assert.IsNotNull(template, "The package's default material must be imported.");
        _material = Object.Instantiate(template);
        _texture = new Texture2D(1, 1);
        _source = _prototype.AddComponent<MeshRenderer>();
        _source.sharedMaterial = _material;
    }

    [TearDown]
    public void TearDown()
    {
        if (_prototype != null) Object.DestroyImmediate(_prototype);
        if (_material != null) Object.DestroyImmediate(_material);
        if (_texture != null) Object.DestroyImmediate(_texture);
    }

    [Test]
    public void PreparingMaterialChangesOnlyInstancingNotSurfaceState()
    {
        _material.enableInstancing = false;
        _material.renderQueue = 2467;
        var factor = new Vector4(0.13f, 0.27f, 0.49f, 0.75f);
        var pbr = new Vector4(0.72f, 0.81f, 0, 0);
        _material.SetVector("_baseColorFactor", factor);
        _material.SetVector("_metallicRoughnessFactor", pbr);
        _material.SetTexture("_baseColorTexture", _texture);
        _material.SetTextureOffset("_baseColorTexture", new Vector2(0.2f, 0.3f));
        _material.SetTextureScale("_baseColorTexture", new Vector2(2, 3));
        string[] surfaceProperties = new[] {
            "_AlphaClip", "_BUILTIN_AlphaClip", "_AlphaCutoffEnable",
            "_Cull", "_CullMode", "_BUILTIN_CullMode"
        }.Where(_material.HasProperty).ToArray();
        Assert.IsNotEmpty(surfaceProperties);
        foreach (string property in surfaceProperties) _material.SetFloat(property, 0.0f);
        string[] keywords = _material.shaderKeywords;
        Shader shader = _material.shader;

        CesiumInstancedRenderer.PrepareMaterialForInstancing(_material);

        Assert.IsTrue(_material.enableInstancing);
        Assert.AreSame(shader, _material.shader);
        Assert.AreEqual(2467, _material.renderQueue);
        CollectionAssert.AreEquivalent(keywords, _material.shaderKeywords);
        foreach (string property in surfaceProperties) Assert.AreEqual(0.0f, _material.GetFloat(property), property);
        Assert.AreEqual(factor, _material.GetVector("_baseColorFactor"));
        Assert.AreEqual(pbr, _material.GetVector("_metallicRoughnessFactor"));
        Assert.AreEqual(_texture, _material.GetTexture("_baseColorTexture"));
        Assert.AreEqual(new Vector2(0.2f, 0.3f), _material.GetTextureOffset("_baseColorTexture"));
        Assert.AreEqual(new Vector2(2, 3), _material.GetTextureScale("_baseColorTexture"));
    }

    [Test]
    public void RendererPropertyBlockIsUsedWhenNoMaterialSlotBlockExists()
    {
        var input = new MaterialPropertyBlock();
        input.SetVector("_baseColorFactor", new Vector4(0.2f, 0.4f, 0.6f, 1));
        input.SetTexture("_baseColorTexture", _texture);
        _source.SetPropertyBlock(input);
        var output = new MaterialPropertyBlock();
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        Assert.AreEqual(input.GetVector("_baseColorFactor"), output.GetVector("_baseColorFactor"));
        Assert.AreEqual(_texture, output.GetTexture("_baseColorTexture"));
    }

    [Test]
    public void MaterialSlotBlockTakesPrecedenceWithoutMergingRendererBlock()
    {
        var shared = new MaterialPropertyBlock();
        shared.SetFloat("_RendererOnlyValue", 17.0f);
        shared.SetVector("_baseColorFactor", Vector4.one);
        _source.SetPropertyBlock(shared);
        var perMaterial = new MaterialPropertyBlock();
        perMaterial.SetVector("_baseColorFactor", new Vector4(0.1f, 0.2f, 0.3f, 1));
        perMaterial.SetTexture("_baseColorTexture", _texture);
        _source.SetPropertyBlock(perMaterial, 0);

        var output = new MaterialPropertyBlock();
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        Assert.AreEqual(perMaterial.GetVector("_baseColorFactor"), output.GetVector("_baseColorFactor"));
        Assert.AreEqual(_texture, output.GetTexture("_baseColorTexture"));
        Assert.IsFalse(output.HasProperty("_RendererOnlyValue"), "Unity does not merge the two block levels.");
    }

    [Test]
    public void ClearingSlotOverrideRestoresRendererBlockOnNextCopy()
    {
        var block = new MaterialPropertyBlock();
        block.SetFloat("_TestValue", 1.0f);
        _source.SetPropertyBlock(block);
        block.SetFloat("_TestValue", 2.0f);
        _source.SetPropertyBlock(block, 0);
        var output = new MaterialPropertyBlock();
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        Assert.AreEqual(2.0f, output.GetFloat("_TestValue"));
        _source.SetPropertyBlock(null, 0);
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        Assert.AreEqual(1.0f, output.GetFloat("_TestValue"));
    }

    [Test]
    public void UnsetSourceClearsStaleDestinationProperties()
    {
        var output = new MaterialPropertyBlock();
        output.SetTexture("_baseColorTexture", _texture);
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        Assert.IsTrue(output.isEmpty);
    }

    [Test]
    public void CopyDoesNotMutateSourceOverrides()
    {
        var input = new MaterialPropertyBlock();
        input.SetFloat("_overlayTextureCoordinateIndex_Clipping", 3.0f);
        _source.SetPropertyBlock(input, 0);
        var output = new MaterialPropertyBlock();
        CesiumInstancedRenderer.CopySourceMaterialProperties(_source, output);
        output.SetFloat("_overlayTextureCoordinateIndex_Clipping", -2.0f);
        _source.GetPropertyBlock(input, 0);
        Assert.AreEqual(3.0f, input.GetFloat("_overlayTextureCoordinateIndex_Clipping"));
    }

    [Test]
    public void IndividualDrawSwitchPreservesMaterialRasterAndBatchObjects()
    {
        var renderer = _prototype.AddComponent<CesiumInstancedRenderer>();
        // The inactive root prevents networking and draw submission in this unit test.
        var settings = _prototype.AddComponent<CesiumInstancedRendering>();
        settings.forceIndividualDraws = false;
        SetField(renderer, "_materialDiagnosticSettings", settings);
        SetField(renderer, "_canInstance", true);
        SetField(renderer, "_material", _material);
        var properties = new MaterialPropertyBlock();
        properties.SetTexture("_overlayTexture_Clipping", _texture);
        properties.SetFloat("_overlayTextureCoordinateIndex_Clipping", -2.0f);
        properties.SetFloat("_CesiumInstanceClipCoverage", 1.0f);
        properties.SetVector("_CesiumInstanceCoverage", new Vector4(-1, -2, 3, 4));
        SetField(renderer, "_properties", properties);
        object batches = GetField(renderer, "_batches");
        _source.enabled = false;

        Assert.IsTrue(renderer.UseGpuInstancingForDraw);
        settings.forceIndividualDraws = true;
        Assert.IsFalse(renderer.UseGpuInstancingForDraw);
        settings.forceIndividualDraws = false;
        Assert.IsTrue(renderer.UseGpuInstancingForDraw);
        Assert.AreSame(_material, GetField(renderer, "_material"));
        Assert.AreSame(properties, GetField(renderer, "_properties"));
        Assert.AreSame(batches, GetField(renderer, "_batches"));
        Assert.AreEqual(_texture, properties.GetTexture("_overlayTexture_Clipping"));
        Assert.AreEqual(-2.0f, properties.GetFloat("_overlayTextureCoordinateIndex_Clipping"));
        Assert.AreEqual(1.0f, properties.GetFloat("_CesiumInstanceClipCoverage"));
        Assert.AreEqual(new Vector4(-1, -2, 3, 4), properties.GetVector("_CesiumInstanceCoverage"));
        Assert.IsFalse(_source.enabled);
    }

    [Test]
    public void DiagnosticSwitchCannotEnableUnsupportedInstancing()
    {
        var renderer = _prototype.AddComponent<CesiumInstancedRenderer>();
        SetField(renderer, "_canInstance", false);
        Assert.IsFalse(renderer.UseGpuInstancingForDraw);
        var settings = _prototype.AddComponent<CesiumInstancedRendering>();
        SetField(renderer, "_materialDiagnosticSettings", settings);
        settings.forceIndividualDraws = false;
        Assert.IsFalse(renderer.UseGpuInstancingForDraw);
        settings.forceIndividualDraws = true;
        Assert.IsFalse(renderer.UseGpuInstancingForDraw);
    }

    [Test]
    public void DiagnosticModeDefaultsOff()
    {
        var settings = _prototype.AddComponent<CesiumInstancedRendering>();
        Assert.IsFalse(settings.forceIndividualDraws);
        Assert.IsTrue(settings.enableInstancedRendering);
    }

    [Test]
    public void DiagnosticsAcceptAnUninitializedPrototype()
    {
        var renderer = _prototype.AddComponent<CesiumInstancedRenderer>();
        var text = new StringBuilder();
        Assert.DoesNotThrow(() => renderer.AppendMaterialDiagnostics(text));
        StringAssert.Contains("Not initialized", text.ToString());
    }

    [Test]
    public void MaterialHelpersRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CesiumInstancedRenderer.PrepareMaterialForInstancing(null));
        Assert.Throws<ArgumentNullException>(() => CesiumInstancedRenderer.CopySourceMaterialProperties(null, new MaterialPropertyBlock()));
        Assert.Throws<ArgumentNullException>(() => CesiumInstancedRenderer.CopySourceMaterialProperties(_source, null));
    }

    private static object GetField(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

    private static void SetField(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
}
