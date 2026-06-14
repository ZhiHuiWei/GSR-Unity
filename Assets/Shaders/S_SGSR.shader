Shader "Custom/S_SGSR"
{
    Properties
    {
        _DebugMode ("Debug Mode", Float) = 0
        _MotionScale ("Motion Scale", Float) = 16
        _HistoryBlend ("History Blend", Range(0, 0.98)) = 0.9
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
                
                UNITY_BRANCH
                if (_DebugMode < 0.5) return half4(saturate(1.0 - linearDepth).xxx, 1);
                UNITY_BRANCH
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

            TEXTURE2D_X(_SGSRHistoryTexture);
            TEXTURE2D_X(_MotionVectorTexture);
            SAMPLER(sampler_BlitTexture);

            float _HistoryBlend;
            float _HistoryValid;
            
            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord.xy;
                half4 current = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv);

                UNITY_BRANCH
                if (_HistoryValid < 0.5)
                    return current;

                float2 motion = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_PointClamp, uv).xy;
                float2 historyUV = uv - motion;

                UNITY_BRANCH
                if (any(historyUV < 0.0) || any(historyUV > 1.0))
                    return current;

                half4 history = SAMPLE_TEXTURE2D_X(_SGSRHistoryTexture, sampler_LinearClamp, historyUV);
                return lerp(current, history, saturate(_HistoryBlend));
            }
            ENDHLSL
        }
    }
}
