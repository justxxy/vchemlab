// Vchem M3 - gas bubble (real sphere mesh, GPU instancing enabled on the material).
// Billboards fail the "turn the camera" check, so bubbles use Sphere.fbx geometry.
Shader "Vchem/Probe/Bubble"
{
    Properties
    {
        _Color ("Tint", Color) = (0.92, 0.97, 1.0, 1)
        _FresnelPower ("Fresnel Power", Float) = 2.5
        _FresnelStrength ("Fresnel Strength", Float) = 0.9
        _CoreAlpha ("Core Alpha", Range(0, 1)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
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
                float3 normalOS   : NORMAL;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _FresnelPower;
                float _FresnelStrength;
                float _CoreAlpha;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetCameraPositionWS() - IN.positionWS);
                float fresnel = pow(1.0 - saturate(abs(dot(N, V))), _FresnelPower);
                half3 col = _Color.rgb * (0.72 + fresnel * _FresnelStrength);
                return half4(col, saturate(_CoreAlpha + fresnel * 0.7));
            }
            ENDHLSL
        }
    }
}
