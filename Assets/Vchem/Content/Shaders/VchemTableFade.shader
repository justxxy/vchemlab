// Vchem M3 stage - matte table with far edge fading into the solid background.
// Unlit on purpose: no reflection, no gradient beyond the fade, no room geometry.
Shader "Vchem/Probe/TableFade"
{
    Properties
    {
        _TableColor ("Table (linear)", Color) = (0.55, 0.55, 0.54, 1)
        _BgColor ("Background (linear)", Color) = (0.342, 0.370, 0.395, 1)
        _FadeStart ("Fade Start (-Z)", Float) = 0.8
        _FadeEnd ("Fade End (-Z)", Float) = 2.8
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        ZWrite On
        Cull Back

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _TableColor;
                half4 _BgColor;
                float _FadeStart;
                float _FadeEnd;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float far = -IN.positionWS.z;
                float t = saturate((far - _FadeStart) / max(_FadeEnd - _FadeStart, 1e-4));
                t = t * t * (3.0 - 2.0 * t);
                return half4(lerp(_TableColor.rgb, _BgColor.rgb, t), 1.0);
            }
            ENDHLSL
        }
    }
}
