// Adapted from sgsr2_convert.fs and sgsr2_upscale.fs (2-pass-fs).
// Copyright (c) 2024, Qualcomm Innovation Center, Inc. All rights reserved.
// SPDX-License-Identifier: BSD-3-Clause
Shader "Custom/S_SGSR"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        TEXTURE2D_X_FLOAT(_SGSRDepthTexture);
        TEXTURE2D_X(_MotionVectorTexture);
        TEXTURE2D_X(_SGSRMotionDepthClipTexture);
        TEXTURE2D_X(_SGSRHistoryTexture);
        TEXTURE2D_X_FLOAT(_SGSRPreviousMetadata);
        float4 _SGSRPreviousJitter;
        float4x4 _SGSRInverseViewProjection;
        float4x4 _SGSRPreviousView;
        float _SGSRHistoryDepthThreshold;
        float4 _SGSRRenderSize; // xy: size, zw: reciprocal size
        float4 _SGSROutputSize;
        float4 _SGSRJitter; // xy: current sample displacement in input pixels
        float4 _SGSRScaleRatio; // x: kernel bias, y: variance box scale
        float _SGSRFov;
        float _SGSRMinLerpContribution;
        float _SGSRReset;
        float _SGSRNativeDepthGather;

        int2 ClampInputPosition(int2 p)
        {
            return clamp(p, int2(0, 0), int2(_SGSRRenderSize.xy) - 1);
        }
        float LoadInputDepth(int2 p)
        {
            float2 uv = (float2(ClampInputPosition(p)) + 0.5) * _SGSRRenderSize.zw;
            float depth = SAMPLE_TEXTURE2D_X_LOD(_SGSRDepthTexture, sampler_PointClamp, uv, 0).r;
            #if UNITY_REVERSED_Z
                depth = 1.0 - depth;
            #endif
            return depth; // Official convert math uses near = 0, far = 1.
        }
        // GLSL gather component order, with point fallback for a depth texture
        // whose descriptor differs from the input color resolution.
        float4 GatherInputDepth(int2 p)
        {
            float4 depths = 0.0;
            UNITY_BRANCH
            if (_SGSRNativeDepthGather > 0.5)
            {
                float2 uv = (float2(p) + 1.0) * _SGSRRenderSize.zw;
                depths = GATHER_RED_TEXTURE2D_X(_SGSRDepthTexture, sampler_PointClamp, uv);
                #if UNITY_REVERSED_Z
                    depths = 1.0 - depths;
                #endif
            }
            else
            {
                depths = float4(LoadInputDepth(p + int2(0, 1)), LoadInputDepth(p + 1),
                                LoadInputDepth(p + int2(1, 0)), LoadInputDepth(p));
            }
            return depths;
        }
        float Min4(float4 v) { return min(min(v.x, v.y), min(v.z, v.w)); }
        float FastLanczos(float base)
        {
            float y = base - 1.0;
            float y2 = y * y;
            return (0.75 * y + y2) * y2;
        }
        float3 ToYCoCg(float3 rgb)
        {
            return float3(dot(rgb, float3(0.25, 0.5, 0.25)),
                          0.5 * (rgb.r - rgb.b),
                          dot(rgb, float3(-0.25, 0.5, -0.25)));
        }
        float3 FromYCoCg(float3 color)
        {
            return float3(color.x + color.y - color.z, color.x + color.z,
                          color.x - color.y - color.z);
        }
        float InputEyeDepth(float nearZeroDepth)
        {
            if (unity_OrthoParams.w > 0.5)
                return lerp(_ProjectionParams.y, _ProjectionParams.z, nearZeroDepth);
            #if UNITY_REVERSED_Z
                return LinearEyeDepth(1.0 - nearZeroDepth, _ZBufferParams);
            #else
                return LinearEyeDepth(nearZeroDepth, _ZBufferParams);
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "SGSR Convert"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Convert
            float4 Convert(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                int2 p = ClampInputPosition(int2(uv * _SGSRRenderSize.xy));
                float4 btmLeft = GatherInputDepth(p - 1);
                float4 btmRight = GatherInputDepth(p + int2(1, -1));
                float4 topLeft = GatherInputDepth(p + int2(-1, 1));
                float4 topRight = GatherInputDepth(p + 1);
                float maxC = min(min(btmLeft.z, btmRight.w), min(topLeft.y, topRight.x));
                float depthclip = 0.0;
                if (maxC < 1.0 - 1.0e-5)
                {
                    float depthsep = 1.37e-5 * _SGSRFov * length(_SGSRRenderSize.xy) * (1.0 - maxC);
                    float4 nearest = float4(Min4(btmLeft), Min4(btmRight), Min4(topLeft), Min4(topRight));
                    float4 weights = saturate(depthsep / (abs(maxC - nearest) + 1.19e-7));
                    depthclip = saturate(1.0 - dot(weights, float4(0.25, 0.25, 0.25, 0.25)));
                }
                // URP supplies signed current-minus-previous UV motion, including
                // camera motion. Zero and negative values are valid; no decoding.
                float2 motion = SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture, sampler_PointClamp, uv, 0).xy;
                return float4(motion, depthclip, InputEyeDepth(LoadInputDepth(p)));
            }
            ENDHLSL
        }
        Pass
        {
            Name "SGSR Upscale"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Upscale
            float4 Upscale(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 hruv = input.texcoord;
                float2 jitteruv = saturate(hruv + _SGSRJitter.xy * _SGSRRenderSize.zw);
                int2 inputPos = ClampInputPosition(int2(jitteruv * _SGSRRenderSize.xy));
                float3 mda = SAMPLE_TEXTURE2D_X_LOD(_SGSRMotionDepthClipTexture, sampler_PointClamp, jitteruv, 0).xyz;
                float2 prevUV = hruv - mda.xy;
                bool reset = _SGSRReset > 0.5 || any(prevUV < 0.0) || any(prevUV > 1.0);
                // Color history is unjittered, but previous Convert metadata is
                // on the previous jittered raster grid. Reject newly exposed surfaces.
                UNITY_BRANCH
                if (!reset)
                {
                    float2 previousRasterUV = prevUV + _SGSRPreviousJitter.xy * _SGSRRenderSize.zw;
                    if (any(previousRasterUV < 0.0) || any(previousRasterUV > 1.0))
                        reset = true;
                    else
                    {
                        float previousEyeDepth = SAMPLE_TEXTURE2D_X_LOD(_SGSRPreviousMetadata,
                            sampler_PointClamp, previousRasterUV, 0).w;
                        float rawDepth = LoadInputDepth(inputPos);
                        #if UNITY_REVERSED_Z
                            rawDepth = 1.0 - rawDepth;
                        #else
                            rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                        #endif
                        float2 sampleUV = (float2(inputPos) + 0.5) * _SGSRRenderSize.zw;
                        float3 positionWS = ComputeWorldSpacePosition(sampleUV, rawDepth, _SGSRInverseViewProjection);
                        float expectedPreviousDepth = -mul(_SGSRPreviousView, float4(positionWS, 1.0)).z;
                        float tolerance = max(0.01, abs(expectedPreviousDepth) * _SGSRHistoryDepthThreshold);
                        // UV motion has no object Z displacement. Significant motion
                        // in depth conservatively rejects history instead of ghosting.
                        reset = expectedPreviousDepth <= 0.0 ||
                            abs(previousEyeDepth - expectedPreviousDepth) > tolerance;
                    }
                }
                float3 historyColor = 0.0;
                UNITY_BRANCH
                if (!reset)
                    historyColor = ToYCoCg(SAMPLE_TEXTURE2D_X_LOD(_SGSRHistoryTexture, sampler_LinearClamp, prevUV, 0).rgb);
                float depthfactor = mda.z;
                float biasmax = _SGSRScaleRatio.x;
                float biasmin = max(1.0, 0.3 + 0.3 * biasmax);
                float kernelbias = 0.5 * lerp(biasmax, biasmin, 0.25 * depthfactor);
                float kernelbias2 = kernelbias * kernelbias;
                // Official tuning uses NDC motion (twice Unity's UV motion).
                float motionLength = length(2.0 * mda.xy * _SGSROutputSize.xy);
                float curvebias = lerp(-2.0, -3.0, saturate(motionLength * 0.02));
                float2 srcOffset = float2(inputPos) + 0.5 - _SGSRJitter.xy - hruv * _SGSRRenderSize.xy;
                float4 upsampledcw = 0.0;
                float3 boxcenter = 0.0;
                float3 boxvar = 0.0;
                float boxweight = 0.0;
                float3 boxmin = 1.0e20;
                float3 boxmax = -1.0e20;
                // Official fast path: top, right, left, center, bottom (five taps).
                const int2 offsets[5] = { int2(0, 1), int2(1, 0), int2(-1, 0), int2(0, 0), int2(0, -1) };
                [unroll]
                for (int i = 0; i < 5; ++i)
                {
                    // Separate chroma from luma: a black/white neighborhood must
                    // not accept the old pink sphere merely because RGB is in range.
                    float3 color = ToYCoCg(LOAD_TEXTURE2D_X(_BlitTexture, ClampInputPosition(inputPos + offsets[i])).rgb);
                    float2 offset = srcOffset + float2(offsets[i]);
                    float distance2 = dot(offset, offset);
                    float weight = FastLanczos(saturate(distance2 * kernelbias2));
                    upsampledcw += float4(color * weight, weight);
                    float bw = exp(distance2 * curvebias);
                    boxmin = min(boxmin, color);
                    boxmax = max(boxmax, color);
                    boxcenter += color * bw;
                    boxvar += color * color * bw;
                    boxweight += bw;
                }
                boxcenter /= max(boxweight, 1.192e-7);
                boxvar = sqrt(abs(boxvar / max(boxweight, 1.192e-7) - boxcenter * boxcenter));
                // Negative Lanczos lobes are intentional in the official filter.
                // Only an effectively zero sum needs a safe spatial fallback.
                if (abs(upsampledcw.w) <= 1.192e-7)
                    upsampledcw = float4(ToYCoCg(SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, jitteruv, 0).rgb), 1.0);
                upsampledcw.rgb = clamp(upsampledcw.rgb / upsampledcw.w, boxmin - 0.075, boxmax + 0.075);
                upsampledcw.w *= 1.0 / 3.0;
                float baseupdate = 1.0 - depthfactor;
                baseupdate = min(baseupdate, lerp(baseupdate, upsampledcw.w * 10.0, saturate(10.0 * motionLength)));
                baseupdate = min(baseupdate, lerp(baseupdate, upsampledcw.w, saturate(motionLength * 0.05)));
                float boxscale = max(depthfactor, saturate(motionLength * 0.05));
                float boxsize = lerp(_SGSRScaleRatio.y, 1.0, boxscale);
                boxmin = max(boxmin, boxcenter - boxvar * boxsize);
                boxmax = min(boxmax, boxcenter + boxvar * boxsize);
                float3 clampedColor = clamp(historyColor, boxmin, boxmax);
                float startLerp = _SGSRMinLerpContribution;
                if (2.0 * (abs(mda.x) + abs(mda.y)) > 0.000001)
                    startLerp = 0.0;
                float contribution = (any(boxmin > historyColor) || any(historyColor > boxmax)) ? startLerp : 1.0;
                // Static-camera relaxation must not preserve chroma from an object
                // that moved away, even when Min Lerp Contribution is set to 1.
                if (any(historyColor.yz < boxmin.yz - 1.0e-4) || any(historyColor.yz > boxmax.yz + 1.0e-4))
                    contribution = 0.0;
                historyColor = lerp(clampedColor, historyColor, saturate(contribution));
                float basealpha = lerp(min(baseupdate, 0.1), baseupdate, saturate(contribution));
                float alpha = reset ? 1.0 : saturate(upsampledcw.w / max(1.192e-7, basealpha + upsampledcw.w));
                float3 result = lerp(historyColor, upsampledcw.rgb, alpha);
                // Preserve alpha for URP targets that request alpha output.
                float sourceAlpha = LOAD_TEXTURE2D_X(_BlitTexture, inputPos).a;
                return float4(FromYCoCg(result), sourceAlpha);
            }
            ENDHLSL
        }
    }
}
