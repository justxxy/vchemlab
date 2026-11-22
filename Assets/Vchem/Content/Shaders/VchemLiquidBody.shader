// Vchem M3 - liquid body (URP 14, hand-written HLSL).
// Default: almost colourless, brightness ≈ empty glass (NOBOOK measured ratio 0.98).
// Turbidity is a parameter, not a colour swap: at t>0 the shader samples a higher
// mip of _CameraOpaqueTexture so the background behind the liquid actually blurs.
// Depth shading uses liquid-top world Y, not scene depth; no fluid solver involved.
Shader "Vchem/Probe/LiquidBody"
{
    Properties
    {
        _WaterColor ("Clear Water", Color) = (0.30, 0.32, 0.33, 0.10)
        _DeepColor ("Deep Water", Color) = (0.28, 0.30, 0.31, 0.16)
        _TurbidColor ("Turbid Tint", Color) = (0.93, 0.94, 0.92, 0.85)
        _Turbidity ("Turbidity", Range(0, 1)) = 0
        _Tint ("Event Tint", Color) = (1, 1, 1, 1)
        _LiquidTopY ("Liquid Top (World Y)", Float) = 0.2
        _DepthSpan ("Depth Span", Float) = 1.2
        _FresnelPower ("Fresnel Power", Float) = 3.0
        _FresnelStrength ("Fresnel Strength", Float) = 0.12
        _Opacity ("Opacity", Float) = 1.0
        _StripeTex ("Debug Stripe Target", 2D) = "white" {}
        _DebugStripes ("Debug Stripes", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D(_StripeTex);
            SAMPLER(sampler_StripeTex);

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
                half4 _WaterColor;
                half4 _DeepColor;
                half4 _TurbidColor;
                float _Turbidity;
                half4 _Tint;
                float _LiquidTopY;
                float _DepthSpan;
                float _FresnelPower;
                float _FresnelStrength;
                float _Opacity;
                float _DebugStripes;
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

                float depth = saturate((_LiquidTopY - IN.positionWS.y) / max(_DepthSpan, 0.0001));
                half3 clearCol = lerp(_WaterColor.rgb, _DeepColor.rgb, depth) * _Tint.rgb;

                // Turbidity: blur what is behind the liquid, then mix toward milk.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float lod = saturate(_Turbidity) * 7.5;
                half3 behind = SAMPLE_TEXTURE2D_LOD(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, screenUV, lod).rgb;
                if (_DebugStripes > 0.5h)
                    behind = SAMPLE_TEXTURE2D_LOD(_StripeTex, sampler_StripeTex, screenUV * float2(4.0, 1.0), lod).rgb;
                // 浑浊 = 乳白本体 + 模糊背景三者混合。旧版系数写死 1.00，使 milky
                // 完全等于模糊背景；在灰色台面前"变浑"与背景同色、肉眼不可见。
                // 0.35 保留背景模糊（G1 对比度判据），0.65 乳白给出可见的浑浊。
                half3 milky = lerp(_TurbidColor.rgb, behind * 1.05, 0.35);
                half3 col = lerp(clearCol, milky, saturate(_Turbidity)) + fresnel * _FresnelStrength;

                half alpha = lerp(lerp(_WaterColor.a, _DeepColor.a, depth), _TurbidColor.a, saturate(_Turbidity));
                alpha += fresnel * 0.05;
                return half4(col, saturate(alpha) * _Opacity);
            }
            ENDHLSL
        }
    }
}
