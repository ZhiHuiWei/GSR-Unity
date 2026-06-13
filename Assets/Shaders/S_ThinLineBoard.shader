Shader "Custom/S_ThinLineBoard"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.02, 0.02, 0.02, 1)
        _LineColor ("Line Color", Color) = (1, 1, 1, 1)
        _LineCount ("Line Count", Float) = 80
        _LineWidth ("Line Width", Range(0.001, 0.5)) = 0.08
        _Angle ("Angle", Float) = 45
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
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
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _LineColor;
                float _LineCount;
                float _LineWidth;
                float _Angle;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float rad = radians(_Angle);
                float2 dir = float2(cos(rad), sin(rad));
                
                float coord = dot(IN.uv, dir) * _LineCount;
                float stripe = frac(coord);
                float thinLine = step(stripe, _LineWidth);
                
                half4 color = lerp(_BaseColor, _LineColor, thinLine);
                return color;
            }
            ENDHLSL
        }
    }
}
