// Vchem M3 - glass shell (double-layer, URP 14 / hand-written HLSL).
// Four layers, in render order:
//   1. wall double-line (shell id separates the two silhouettes)
//   2. studio reflection (Reflection Probe / custom HDRI cubemap)
//   3. thickness absorption (view-path proxy, sigma in 1/mm)
//   4. screen-space refraction (optional, shader_feature_local; recorded only)
// Target: PC / RTX 4060 Laptop, URP High. M2 is untouched.
Shader "Vchem/Probe/GlassShell"
{
    Properties
    {
        _GlassColor ("Glass Tint", Color) = (0.40, 0.45, 0.47, 0.10)
        _FresnelPower ("Fresnel Power", Float) = 3.5
        _FresnelStrength ("Fresnel Strength", Float) = 0.85
        _RimColor ("Rim Color", Color) = (0.95, 0.99, 1.0, 1.0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1

        _ReflectionStrength ("Studio Reflection Strength", Float) = 3.0
        _ReflectionRoughness ("Studio Reflection Roughness", Range(0, 1)) = 0.02

        _ShellId ("Shell Id (0=inner, 1=outer)", Float) = 0
        _WallThicknessMM ("Wall Thickness (mm)", Float) = 1.2
        _MaxPathMM ("Max View Path (mm)", Float) = 82
        _Sigma ("Absorption Sigma (1/mm)", Vector) = (0.030, 0.018, 0.012, 0)

        _LinePower ("Edge Line Power", Float) = 14
        _LineStrength ("Edge Line Strength", Float) = 1.5

        _RefractionStrength ("Refraction Strength", Float) = 1.5
        _RefractionMix ("Refraction Mix", Range(0, 1)) = 0.25

        // 细管专用模式：用视空间法线横向分量画连续双线，绕开 dot(N,V) 幂次
        // 在亚像素细管上产生的高频碎光。0=器皿原路径，1=细管路径。
        _TubeMode  ("Thin Tube Mode", Float) = 0
        _TubeSplitY ("Tube Split Y (object space)", Float) = 2.0   // 仅 y<此值的片段走细管路径
        _TubeColor  ("Tube Tint", Color) = (0.62, 0.63, 0.63, 0.38) // 中性玻璃色，独立于器皿
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+30" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull [_Cull]

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // These three are URP-owned renderer keywords, not local options.
            #pragma multi_compile _ _ENVIRONMENTREFLECTIONS_OFF
            #pragma multi_compile _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile _ _REFLECTION_PROBE_BOX_PROJECTION
            // Optional refraction: material-controlled, local variant only.
            #pragma shader_feature_local _REFRACTION_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #ifdef _REFRACTION_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #endif

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
                float  positionOSY: TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _GlassColor;
                float _FresnelPower;
                float _FresnelStrength;
                half4 _RimColor;
                float _ReflectionStrength;
                float _ReflectionRoughness;
                float _ShellId;
                float _WallThicknessMM;
                float _MaxPathMM;
                half4 _Sigma;
                float _LinePower;
                float _LineStrength;
                float _RefractionStrength;
                float _RefractionMix;
                float _TubeMode;
                float _TubeSplitY;
                half4 _TubeColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS  = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS  = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionOSY = IN.positionOS.y;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetCameraPositionWS() - IN.positionWS);
                float ndv = saturate(abs(dot(N, V)));
                float grazing = 1.0 - ndv;
                float fresnel = pow(grazing, _FresnelPower);

                // Layer 3: absorption. Path length grows from two wall thicknesses
                // at normal incidence to the vessel diameter at grazing incidence.
                float pathMM = lerp(_WallThicknessMM * 2.0, _MaxPathMM, pow(grazing, 1.6));
                half3 absorb = exp(-_Sigma.rgb * pathMM);

                // Layer 1 body + rim tint.
                half3 body = lerp(_GlassColor.rgb, _RimColor.rgb, fresnel) * absorb;

                // Layer 2: studio reflection from the custom reflection probe.
                float3 R = reflect(-V, N);
                half3 studio = GlossyEnvironmentReflection(R, IN.positionWS, _ReflectionRoughness, 1.0h);
                // §P2 A2：本体端反射权重 0.20→0.10，边缘端（fresnel≈1）几乎不变，
                // 从而拉开本体/高光亮度比；不碰 edgeLine，避免水平环面锯齿。
                half3 col = body + studio * _ReflectionStrength * saturate(0.10 + fresnel);

                // Layer 1b: thin silhouette line. Outer shell is brighter than inner,
                // so the two shells read as a wall with a dark gap instead of one blob.
                float edgeLine = pow(grazing, _LinePower) * _LineStrength * (0.65 + 0.35 * _ShellId);
                col += edgeLine * half3(0.92, 0.98, 1.0);
                half a = _GlassColor.a + fresnel * _FresnelStrength + edgeLine * 0.35;

                // 细管模式：视空间法线的横向分量 abs(nVS.x) 在直管段沿管长恒定，
                // 用它做"中间暗-两侧亮"的玻璃双线，不会像 dot(N,V)^k 那样在几像素
                // 宽的管子上被光栅化成断续点串。_TubeColor 为中性玻璃色（不随器皿偏蓝）。
                // _TubeSplitY：只让物体空间 y 低于阈值的部分（如漏斗细颈）走细管路径，
                // 球碗/瓶体等高处仍用上面的器皿原路径；0.03 软过渡避免硬边。
                if (_TubeMode > 0.5)
                {
                    float tubeW = 1.0 - smoothstep(_TubeSplitY - 0.03, _TubeSplitY + 0.03, IN.positionOSY);
                    if (tubeW > 0.001)
                    {
                        float3 nVS = normalize(mul((float3x3)UNITY_MATRIX_V, N));
                        float rim = smoothstep(0.20, 0.92, abs(nVS.x));
                        half3 tubeCol = lerp(_TubeColor.rgb, half3(0.96, 0.96, 0.96), rim * 0.75);
                        tubeCol *= (0.92 + 0.08 * (1.0 - abs(nVS.y)));
                        half tubeA = _TubeColor.a + rim * _TubeColor.a * 0.7;
                        return half4(lerp(col, tubeCol, tubeW), saturate(lerp(a, tubeA, tubeW)));
                    }
                }

                #ifdef _REFRACTION_ON
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float2 offset = N.xy * _RefractionStrength * saturate(pathMM / max(_MaxPathMM, 1e-3)) * 0.001;
                half3 refracted = SAMPLE_TEXTURE2D_LOD(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, screenUV + offset, 0).rgb;
                col = lerp(col, refracted * 1.15, _RefractionMix);
                #endif

                return half4(col, saturate(a));
            }
            ENDHLSL
        }
    }
}
