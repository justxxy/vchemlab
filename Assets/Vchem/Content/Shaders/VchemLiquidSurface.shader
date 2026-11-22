// Vchem M3 - liquid surface (horizontal disk, URP 14).
// NOBOOK measurement: flat surface, one horizontal bright band, no white ring.
// The band is placed near the camera-side edge in local +Z so it reads as a
// horizontal line across the ellipse; the old meniscus rim is intentionally gone.
Shader "Vchem/Probe/LiquidSurface"
{
    Properties
    {
        _SurfaceColor ("Surface Color", Color) = (0.80, 0.84, 0.86, 0.18)
        _Radius ("Disk Local Radius", Float) = 0.5
        _BandCenter ("Band Center (local z / radius)", Float) = 0.82
        _BandWidth ("Band Width", Float) = 0.10
        _BandColor ("Band Color", Color) = (0.94, 0.97, 0.98, 1)
        _BandStrength ("Band Strength", Float) = 0.08
        _FresnelPower ("Fresnel Power", Float) = 5.0
        _FresnelStrength ("Fresnel Strength", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        // 液面用极扁 Cylinder（高 4mm），Cull Off 会同时画顶/底/侧三层，
        // 叠加后在瓶底透出一圈浅色圆盘，看着像洒了一滩水。只渲顶面即可。
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
                float2 uv         : TEXCOORD0;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _SurfaceColor;
                float _Radius;
                float _BandCenter;
                float _BandWidth;
                half4 _BandColor;
                float _BandStrength;
                float _FresnelPower;
                float _FresnelStrength;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionOS = IN.positionOS.xyz;
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

                float z = IN.positionOS.z / max(_Radius, 1e-3);
                float t = (z - _BandCenter) / max(_BandWidth, 1e-3);
                float band = exp(-t * t);
                // Fade the band near the left/right ends so it is a line, not a ring.
                float endFade = 1.0 - smoothstep(0.75, 1.0, abs(IN.positionOS.x) / max(_Radius, 1e-3));
                band *= endFade;

                half3 col = _SurfaceColor.rgb + fresnel * _FresnelStrength * half3(0.9, 0.97, 1.0);
                col += band * _BandColor.rgb * _BandStrength;
                // §P2 液面白圈：band 的 alpha 贡献 0.18 会让 p99/本体到 1.536，
                // 超过 <1.5 阈值；降到 0.10 保留水平亮带但去掉亮环感。
                half a = _SurfaceColor.a + fresnel * 0.08 + band * 0.10;
                return half4(col, saturate(a));
            }
            ENDHLSL
        }
    }
}
