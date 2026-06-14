Shader "Custom/S_CheckerGround"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.8, 0.8, 0.8, 1)
        _ColorB ("Color B", Color) = (0.15, 0.15, 0.15, 1)
        _Tiling ("Tiling", Float) = 20
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
                half4 _ColorA;
                half4 _ColorB;
                float _Tiling;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv * _Tiling;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {   
                float2 checkerUV = floor(IN.uv);
                float checker = fmod(checkerUV.x + checkerUV.y, 2.0);
                
                half4 color = lerp(_ColorA, _ColorB, checker);
                return color;
            }
            ENDHLSL
        }
    }
}
