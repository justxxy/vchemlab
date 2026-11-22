// Vchem M3 stage - cheap radial contact shadow decal.
// One shared material; each apparatus gets one instance scaled to its footprint.
Shader "Vchem/Probe/ContactShadow"
{
    Properties
    {
        _ShadowTex ("Radial Alpha", 2D) = "white" {}
        _Color ("Shadow Color", Color) = (0.02, 0.025, 0.03, 0.55)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-20" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ShadowTex);
            SAMPLER(sampler_ShadowTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _ShadowTex_ST;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _ShadowTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_ShadowTex, sampler_ShadowTex, IN.uv).a;
                return half4(_Color.rgb, a * _Color.a);
            }
            ENDHLSL
        }
    }
}
