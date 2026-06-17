Shader "Cesium/Instanced"
{
    Properties
    {
        _baseColorFactor("Base Color Factor", Color) = (1, 1, 1, 1)
        _baseColorTexture("Base Color Texture", 2D) = "white" {}
        _baseColorTextureCoordinateIndex("Base Color Texture Coordinate Index", Float) = 0
        _overlayTexture_0("Overlay Texture 0", 2D) = "white" {}
        _overlayTexture_1("Overlay Texture 1", 2D) = "white" {}
        _overlayTexture_2("Overlay Texture 2", 2D) = "white" {}
        _overlayTextureCoordinateIndex_0("Overlay Texture Coordinate Index 0", Float) = 0
        _overlayTextureCoordinateIndex_1("Overlay Texture Coordinate Index 1", Float) = 0
        _overlayTextureCoordinateIndex_2("Overlay Texture Coordinate Index 2", Float) = 0
        _overlayTranslationAndScale_0("Overlay Translation And Scale 0", Vector) = (0, 0, 1, 1)
        _overlayTranslationAndScale_1("Overlay Translation And Scale 1", Vector) = (0, 0, 1, 1)
        _overlayTranslationAndScale_2("Overlay Translation And Scale 2", Vector) = (0, 0, 1, 1)
        _overlayEnabled_0("Overlay Enabled 0", Float) = 0
        _overlayEnabled_1("Overlay Enabled 1", Float) = 0
        _overlayEnabled_2("Overlay Enabled 2", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
        }

        Pass
        {
            Name "Instanced Unlit"

            Cull Back
            ZWrite On

            HLSLPROGRAM

            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
                float2 uv3 : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv0 : TEXCOORD1;
                float2 uv1 : TEXCOORD2;
                float2 uv2 : TEXCOORD3;
                float2 uv3 : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            sampler2D _baseColorTexture;
            float4 _baseColorTexture_ST;
            float4 _baseColorFactor;
            float _baseColorTextureCoordinateIndex;

            sampler2D _overlayTexture_0;
            sampler2D _overlayTexture_1;
            sampler2D _overlayTexture_2;
            float _overlayTextureCoordinateIndex_0;
            float _overlayTextureCoordinateIndex_1;
            float _overlayTextureCoordinateIndex_2;
            float4 _overlayTranslationAndScale_0;
            float4 _overlayTranslationAndScale_1;
            float4 _overlayTranslationAndScale_2;
            float _overlayEnabled_0;
            float _overlayEnabled_1;
            float _overlayEnabled_2;

            float2 SelectUV(
                float textureCoordinateIndex,
                float2 uv0,
                float2 uv1,
                float2 uv2,
                float2 uv3)
            {
                int index = (int)round(textureCoordinateIndex);
                if (index == 1)
                {
                    return uv1;
                }
                if (index == 2)
                {
                    return uv2;
                }
                if (index == 3)
                {
                    return uv3;
                }

                return uv0;
            }

            float2 GetOverlayUV(
                float textureCoordinateIndex,
                float4 translationAndScale,
                float2 uv0,
                float2 uv1,
                float2 uv2,
                float2 uv3)
            {
                float2 overlayUv = SelectUV(
                    textureCoordinateIndex,
                    uv0,
                    uv1,
                    uv2,
                    uv3);
                overlayUv = overlayUv * translationAndScale.zw +
                    translationAndScale.xy;
                return float2(overlayUv.x, 1.0 - overlayUv.y);
            }

            float4 BlendOverlayColor(
                float4 color,
                float4 overlayColor,
                float enabled)
            {
                if (enabled < 0.5)
                {
                    return color;
                }

                color.rgb = lerp(color.rgb, overlayColor.rgb, overlayColor.a);
                return color;
            }

            Varyings Vertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_INITIALIZE_OUTPUT(Varyings, output);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.normalWS = UnityObjectToWorldNormal(input.normalOS);
                output.uv0 = input.uv0;
                output.uv1 = input.uv1;
                output.uv2 = input.uv2;
                output.uv3 = input.uv3;
                return output;
            }

            float4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 baseColorUv = SelectUV(
                    _baseColorTextureCoordinateIndex,
                    input.uv0,
                    input.uv1,
                    input.uv2,
                    input.uv3);
                baseColorUv = TRANSFORM_TEX(baseColorUv, _baseColorTexture);

                float4 color =
                    tex2D(_baseColorTexture, baseColorUv) * _baseColorFactor;

                float2 overlayUv0 = GetOverlayUV(
                    _overlayTextureCoordinateIndex_0,
                    _overlayTranslationAndScale_0,
                    input.uv0,
                    input.uv1,
                    input.uv2,
                    input.uv3);
                color = BlendOverlayColor(
                    color,
                    tex2D(_overlayTexture_0, overlayUv0),
                    _overlayEnabled_0);

                float2 overlayUv1 = GetOverlayUV(
                    _overlayTextureCoordinateIndex_1,
                    _overlayTranslationAndScale_1,
                    input.uv0,
                    input.uv1,
                    input.uv2,
                    input.uv3);
                color = BlendOverlayColor(
                    color,
                    tex2D(_overlayTexture_1, overlayUv1),
                    _overlayEnabled_1);

                float2 overlayUv2 = GetOverlayUV(
                    _overlayTextureCoordinateIndex_2,
                    _overlayTranslationAndScale_2,
                    input.uv0,
                    input.uv1,
                    input.uv2,
                    input.uv3);
                color = BlendOverlayColor(
                    color,
                    tex2D(_overlayTexture_2, overlayUv2),
                    _overlayEnabled_2);

                float3 normal = normalize(input.normalWS);
                float light =
                    saturate(dot(normal, normalize(float3(0.35, 0.8, 0.25)))) *
                    0.55 + 0.45;
                color.rgb *= light;
                return color;
            }

            ENDHLSL
        }
    }
}
