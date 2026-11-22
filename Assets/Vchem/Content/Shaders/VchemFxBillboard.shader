// Vchem M3 - effect quad with soft radial alpha (URP 14).
// 顶点走标准 TransformObjectToHClip：缩放/朝向完全由 GameObject.transform 决定，
// C# 侧 FaceCamera() 让 quad 朝向相机。本 shader 只做软边 + 火焰水滴形 alpha。
// mode 0 = white mist（柔 round puff）；mode 1 = flame（上收窄 + 亮核 + 抖动）。
Shader "Vchem/Fx/Billboard"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 0.55, 0.16, 0.85)
        _Mode ("Mode (0 mist / 1 flame)", Float) = 1
        _Softness ("Edge Softness", Range(0.05, 1)) = 0.55
        _Flicker ("Flicker", Float) = 0
        _ShowcaseT ("Showcase Time", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" }
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
                half4 _BaseColor;
                float _Mode;
                float _Softness;
                float _Flicker;
                float _ShowcaseT;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 p = IN.uv * 2.0 - 1.0;
                float r = length(p);
                float edge = 1.0 - _Softness;
                float a = 1.0 - smoothstep(edge, 1.0, r);

                half3 col = _BaseColor.rgb;
                if (_Mode > 0.5)
                {
                    // 火焰：上收窄成水滴，内核偏黄白。抖动用 Director 写入的模拟时间
                    // _ShowcaseT，而不是 _Time.y——否则同一 t 两次截图像素不同。
                    float taper = saturate(1.0 - saturate(p.y) * 0.85);
                    a *= taper;
                    float core = 1.0 - smoothstep(0.0, 0.65, r);
                    col = lerp(_BaseColor.rgb, half3(1.0, 0.95, 0.72), core * 0.85);
                    a *= 1.0 + _Flicker * 0.18 * sin(_ShowcaseT * 41.0 + p.y * 7.0);
                }
                else
                {
                    a *= 0.85;
                }
                return half4(col, saturate(a) * _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
