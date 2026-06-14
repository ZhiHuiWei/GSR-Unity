Shader "Custom/S_SGSR"
{
    Properties
    {
        _DebugMode ("Debug Mode", Float) = 0
        _MotionScale ("Motion Scale", Float) = 16
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        
        ZTest Always
        ZWrite Off
        Cull Off
        
        Pass
        {
            Name "SGSR Debug"
            
            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_SGSRDepthTexture);
            TEXTURE2D_X(_MotionVectorTexture);
            
            float _DebugMode;
            float _MotionScale;

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.texcoord.xy;
                
                float rawDepth = SAMPLE_TEXTURE2D_X(_SGSRDepthTexture, sampler_PointClamp, uv).r;
                float linearDepth = Linear01Depth(rawDepth, _ZBufferParams);
                
                float2 motion = SAMPLE_TEXTURE2D_X(
                    _MotionVectorTexture,
                    sampler_PointClamp,
                    uv).xy;
                
                float3 motionColor = float3(
                    abs(motion.x) * _MotionScale,
                    abs(motion.y) * _MotionScale,
                    0);
                
                if (_DebugMode < 0.5) return half4(saturate(1.0 - linearDepth).xxx, 1);
                if (_DebugMode < 1.5) return half4(motionColor, 1);
                
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            }
            ENDHLSL
        }
        Pass
        {
            Name "SGSR Present"
            
            HLSLPROGRAM
            
            #pragma vertex Vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            
            half4 frag(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_PointClamp,
                    input.texcoord.xy
                );
            }
            ENDHLSL
        }
    }
}
