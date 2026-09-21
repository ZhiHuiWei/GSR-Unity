using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public partial class SGSRPass : ScriptableRenderPass
{
    public enum Algorithm
    {
        [InspectorName("2-pass-FS")] TwoPassFragment = 0,
        [InspectorName("3-pass-CS (Transparency)")] ThreePassCompute = 1
    }
    private static readonly MaterialPropertyBlock DrawProperties = new();
    private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
    private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
    private static readonly int DepthId = Shader.PropertyToID("_SGSRDepthTexture");
    private static readonly int MotionId = Shader.PropertyToID("_MotionVectorTexture");
    private static readonly int MotionDepthClipId = Shader.PropertyToID("_SGSRMotionDepthClipTexture");
    private static readonly int HistoryId = Shader.PropertyToID("_SGSRHistoryTexture");
    private static readonly int RenderSizeId = Shader.PropertyToID("_SGSRRenderSize");
    private static readonly int OutputSizeId = Shader.PropertyToID("_SGSROutputSize");
    private static readonly int JitterId = Shader.PropertyToID("_SGSRJitter");
    private static readonly int ScaleRatioId = Shader.PropertyToID("_SGSRScaleRatio");
    private static readonly int FovId = Shader.PropertyToID("_SGSRFov");
    private static readonly int MinLerpId = Shader.PropertyToID("_SGSRMinLerpContribution");
    private static readonly int ResetId = Shader.PropertyToID("_SGSRReset");
    private static readonly int NativeDepthGatherId = Shader.PropertyToID("_SGSRNativeDepthGather");
    private static readonly int ScreenSizeId = Shader.PropertyToID("_ScreenSize");
    private static readonly int ScaledScreenParamsId = Shader.PropertyToID("_ScaledScreenParams");

    [Serializable]
    public class SGSRSettings
    {
        public Algorithm algorithm = Algorithm.TwoPassFragment;
        [Tooltip("Assign SGSR3Pass.compute for 3-pass-CS mode.")]
        public ComputeShader computeShader;
        [Tooltip("3-pass color compression parameter. Keep at 1 without a pre-exposure integration.")]
        [Min(0.0001f)] public float preExposure = 1.0f;
        public bool UseCompute => algorithm == Algorithm.ThreePassCompute;
        [Tooltip("Actual scene resolution relative to the camera output. Keep URP Render Scale at 1; SGSR reconstructs to native output size.")]
        [Range(0.1f, 1.0f)] public float renderScale = 1.0f;
        public Material material;
        public bool enableHistory = true;
        public bool enableJitter = true;
        [Range(0, 4)] public int jitterScale = 1;
        [Range(1, 32)] public int jitterPhaseCount = 8;
        [Tooltip("2-pass-FS only: history relaxation after the camera has been stationary for more than five frames.")]
        [Range(0.0f, 1.0f)] public float minLerpContribution = 0.3f;
        [Min(0.01f)] public float cameraCutDistance = 5.0f;
        [Range(1, 180)] public float cameraCutAngle = 45.0f;
    }

    private sealed class CameraHistory
    {
        public RTHandle a, b;
        public RTHandle lumaA, lumaB;
        public Algorithm algorithm;
        public ComputeShader computeShader;
        public float preExposure;
        public Vector2 jitterPixels;
        public bool valid;
        public int index, lastFrame = -1, inputWidth, inputHeight, jitterConfiguration, stillFrames;
        public Matrix4x4 viewProjection;
        public RenderTextureDescriptor outputDescriptor;
        public Matrix4x4 projection;
        public Vector3 position;
        public Quaternion rotation;
        public void Release()
        {
            a?.Release(); b?.Release();
            ReleaseLuma();
            a = b = null;
            valid = false;
        }
        public void ReleaseLuma()
        {
            lumaA?.Release(); lumaB?.Release();
            lumaA = lumaB = null;
        }
    }

    private readonly SGSRSettings settings;
    private readonly Dictionary<Camera, CameraHistory> histories = new();
    private readonly List<Camera> expiredCameras = new();

    public SGSRPass(SGSRSettings settings)
    {
        this.settings = settings;
        requiresIntermediateTexture = true;
        ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth);
    }

    public void PrepareCamera(ref CameraData cameraData)
    {
        PruneHistories();
        if (!histories.TryGetValue(cameraData.camera, out CameraHistory state))
            histories.Add(cameraData.camera, state = new CameraHistory());
        // Save the native descriptor before URP allocates scene attachments.
        state.outputDescriptor = cameraData.cameraTargetDescriptor;
        var sceneDescriptor = state.outputDescriptor;
        float scale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        sceneDescriptor.width = Mathf.Max(1, Mathf.RoundToInt(sceneDescriptor.width * scale));
        sceneDescriptor.height = Mathf.Max(1, Mathf.RoundToInt(sceneDescriptor.height * scale));
        cameraData.cameraTargetDescriptor = sceneDescriptor;
        cameraData.renderScale = scale;
    }

    private class ResolutionData
    {
        public Vector4 screenSize;
    }

    private class PassData
    {
        public TextureHandle source, depth, motion, motionDepthClip, history;
        public Material material;
        public int passIndex;
        public Vector4 renderSize, outputSize, jitter, scaleRatio;
        public float fov, minLerp, reset, nativeDepthGather;
    }

    private static void ExecutePass(PassData data, RasterGraphContext context)
    {
        // Resolve RenderGraph handles only inside execution. Every input is also
        // declared with UseTexture below so lifetimes and barriers are tracked.
        // Command buffers retain the Material reference. A later pass (or camera)
        // can overwrite its uniforms before the GPU draws the earlier pass.
        // DrawProcedural snapshots this property block for each draw instead.
        MaterialPropertyBlock material = DrawProperties;
        material.Clear();
        material.SetVector(RenderSizeId, data.renderSize);
        material.SetVector(OutputSizeId, data.outputSize);
        material.SetVector(JitterId, data.jitter);
        material.SetVector(ScaleRatioId, data.scaleRatio);
        material.SetFloat(FovId, data.fov);
        material.SetFloat(MinLerpId, data.minLerp);
        material.SetFloat(ResetId, data.reset);
        if (data.passIndex == 0)
        {
            material.SetTexture(DepthId, data.depth);
            material.SetTexture(MotionId, data.motion);
            material.SetFloat(NativeDepthGatherId, data.nativeDepthGather);
        }
        else
        {
            material.SetTexture(MotionDepthClipId, data.motionDepthClip);
            material.SetTexture(HistoryId, data.history);
        }
        material.SetTexture(BlitTextureId, data.source);
        material.SetVector(BlitScaleBiasId, new Vector4(1, 1, 0, 0));
        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, data.passIndex,
            MeshTopology.Triangles, 3, 1, material);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resources = frameData.Get<UniversalResourceData>();
        var cameraData = frameData.Get<UniversalCameraData>();
        Camera camera = cameraData.camera;
        bool compute = settings.UseCompute;
        if ((compute ? settings.computeShader == null : settings.material == null) || resources.isActiveTargetBackBuffer ||
            !resources.cameraDepthTexture.IsValid() || !resources.motionVectorColor.IsValid())
        {
            ResetHistory(camera);
            return;
        }

        if (!histories.TryGetValue(camera, out CameraHistory state))
            return;

        if (compute && !frameData.GetOrCreate<SGSROpaqueSnapshot>().color.IsValid())
        {
            // A same-texture fallback would silently disable transparency detection.
            ResetHistory(camera);
            return;
        }

        RenderTextureDescriptor inputDesc = cameraData.cameraTargetDescriptor;
        RenderTextureDescriptor outputDesc = state.outputDescriptor;
        outputDesc.depthBufferBits = 0;
        outputDesc.depthStencilFormat = GraphicsFormat.None;
        outputDesc.msaaSamples = 1;
        outputDesc.bindMS = false;
        outputDesc.useMipMap = false;
        outputDesc.autoGenerateMips = false;
        outputDesc.useDynamicScale = false;
        outputDesc.enableRandomWrite = false;
        var historyDesc = outputDesc;
        if (compute)
        {
            historyDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            historyDesc.enableRandomWrite = true;
        }
        bool reallocated = RenderingUtils.ReAllocateHandleIfNeeded(ref state.a, historyDesc, FilterMode.Bilinear,
            TextureWrapMode.Clamp, name: "_SGSR_HistoryA");
        reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref state.b, historyDesc, FilterMode.Bilinear,
            TextureWrapMode.Clamp, name: "_SGSR_HistoryB");
        if (compute) reallocated |= AllocateLuma(state, inputDesc);
        else state.ReleaseLuma();

        CameraJitter jitterComponent = camera.GetComponent<CameraJitter>();
        bool jitterEnabled = settings.enableJitter && settings.jitterScale > 0 &&
            jitterComponent != null && jitterComponent.isActiveAndEnabled;
        int jitterConfiguration = jitterEnabled ? settings.jitterScale * 100 + settings.jitterPhaseCount : 0;
        bool reset = !settings.enableHistory || !state.valid || reallocated ||
            state.algorithm != settings.algorithm ||
            (compute && (state.computeShader != settings.computeShader ||
                         !Mathf.Approximately(state.preExposure, Mathf.Max(0.0001f, settings.preExposure)))) ||
            state.lastFrame != Time.frameCount - 1 || state.inputWidth != inputDesc.width ||
            state.inputHeight != inputDesc.height || state.jitterConfiguration != jitterConfiguration ||
            !Approximately(state.projection, camera.projectionMatrix) ||
            Vector3.Distance(state.position, camera.transform.position) > settings.cameraCutDistance ||
            Quaternion.Angle(state.rotation, camera.transform.rotation) > settings.cameraCutAngle;

        TextureHandle historyRead = renderGraph.ImportTexture(state.index == 0 ? state.a : state.b);
        TextureHandle historyWrite = renderGraph.ImportTexture(state.index == 0 ? state.b : state.a);
        // Consume the genuinely low-resolution rasterized color directly.
        TextureHandle inputColor = resources.activeColorTexture;
#if UNITY_EDITOR
        if (reallocated || state.inputWidth != inputDesc.width || state.inputHeight != inputDesc.height)
        {
            var actualColor = renderGraph.GetTextureDesc(inputColor);
            var actualDepth = renderGraph.GetTextureDesc(resources.cameraDepthTexture);
            var actualMotion = renderGraph.GetTextureDesc(resources.motionVectorColor);
            Debug.Log($"SGSR [{camera.name}]: color {actualColor.width}x{actualColor.height}, depth {actualDepth.width}x{actualDepth.height}, motion {actualMotion.width}x{actualMotion.height} -> output/history {outputDesc.width}x{outputDesc.height}", camera);
        }
#endif
        Vector4 renderSize = new(inputDesc.width, inputDesc.height, 1.0f / inputDesc.width, 1.0f / inputDesc.height);
        Vector4 outputSize = new(outputDesc.width, outputDesc.height, 1.0f / outputDesc.width, 1.0f / outputDesc.height);
        Vector2 jitter = jitterEnabled ? GetSampleDisplacement(cameraData, jitterComponent) : Vector2.zero;
        Vector4 jitterPixels = new(jitter.x * inputDesc.width, jitter.y * inputDesc.height, 0, 0);
        // Match SGSR2_Frag::Context::UpdateUniforms in Qualcomm's sample.
        float areaRatio = (float)outputDesc.width * outputDesc.height / ((float)inputDesc.width * inputDesc.height);
        Vector4 scaleRatio = new((float)outputDesc.width / inputDesc.width,
            Mathf.Min(20.0f, Mathf.Pow(areaRatio, 3.0f)), 0, 0);
        Matrix4x4 viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
        state.stillFrames = !reset && Approximately(state.viewProjection, viewProjection) ? state.stillFrames + 1 : 0;
        float fov = Mathf.Abs(1.0f / camera.projectionMatrix.m00); // tan(horizontal FOV / 2)

        TextureHandle outputColor;
        if (compute)
        {
            outputColor = RecordCompute(renderGraph, frameData, state, inputColor, historyRead, historyWrite,
                renderSize, outputSize, jitterPixels, fov, reset, outputDesc);
        }
        else
        {
            RenderTextureDescriptor motionDesc = outputDesc;
            motionDesc.width = inputDesc.width;
            motionDesc.height = inputDesc.height;
            motionDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            TextureHandle motionDepthClip = UniversalRenderer.CreateRenderGraphTexture(renderGraph, motionDesc,
                "_SGSR_MotionDepthClip", false, FilterMode.Point);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("SGSR Convert", out var data))
            {
                data.source = resources.activeColorTexture;
                data.depth = resources.cameraDepthTexture; // Sampleable, resolved depth (not the MSAA attachment).
                data.motion = resources.motionVectorColor;
                data.material = settings.material;
                data.passIndex = 0;
                data.renderSize = renderSize;
                data.fov = fov;
                var depthDesc = renderGraph.GetTextureDesc(data.depth);
                data.nativeDepthGather = depthDesc.width == inputDesc.width && depthDesc.height == inputDesc.height ? 1 : 0;
                builder.UseTexture(data.source);
                builder.UseTexture(data.depth);
                builder.UseTexture(data.motion);
                builder.SetRenderAttachment(motionDepthClip, 0, AccessFlags.WriteAll);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) => ExecutePass(d, ctx));
            }
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("SGSR Upscale", out var data))
            {
                data.source = inputColor;
                data.motionDepthClip = motionDepthClip;
                data.history = historyRead;
                data.material = settings.material;
                data.passIndex = 1;
                data.renderSize = renderSize;
                data.outputSize = outputSize;
                data.jitter = jitterPixels;
                data.scaleRatio = scaleRatio;
                data.minLerp = state.stillFrames > 5 ? Mathf.Clamp01(settings.minLerpContribution) : 0.0f;
                data.reset = reset ? 1 : 0;
                builder.UseTexture(inputColor);
                builder.UseTexture(motionDepthClip);
                builder.UseTexture(historyRead);
                builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) => ExecutePass(d, ctx));
            }
            // Never copy back into the low-resolution scene target. A separate native
            // color also protects history from later post effects or renderer features.
            outputColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, outputDesc,
                "_SGSR_OutputColor", false, FilterMode.Bilinear);
            renderGraph.AddBlitPass(historyWrite, outputColor, Vector2.one, Vector2.zero,
                passName: "SGSR Present");
        }
        resources.cameraColor = outputColor;
        cameraData.cameraTargetDescriptor = outputDesc;
        cameraData.renderScale = 1.0f;
        cameraData.scaledWidth = outputDesc.width;
        cameraData.scaledHeight = outputDesc.height;
        // Like URP's own post-upscale resolution update, change globals at
        // execution time, after all low-resolution scene draws have finished.
        using (var builder = renderGraph.AddRasterRenderPass<ResolutionData>("SGSR Update Output Resolution", out var data))
        {
            data.screenSize = outputSize;
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (ResolutionData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalVector(ScreenSizeId, d.screenSize);
                ctx.cmd.SetGlobalVector(ScaledScreenParamsId, new Vector4(d.screenSize.x, d.screenSize.y,
                    1.0f + d.screenSize.z, 1.0f + d.screenSize.w));
            });
        }
        state.valid = settings.enableHistory;
        state.index = 1 - state.index;
        state.lastFrame = Time.frameCount;
        state.inputWidth = inputDesc.width;
        state.inputHeight = inputDesc.height;
        state.jitterConfiguration = jitterConfiguration;
        state.projection = camera.projectionMatrix;
        state.viewProjection = viewProjection;
        state.position = camera.transform.position;
        state.rotation = camera.transform.rotation;
        state.algorithm = settings.algorithm;
        state.computeShader = settings.computeShader;
        state.preExposure = Mathf.Max(0.0001f, settings.preExposure);
        state.jitterPixels = new Vector2(jitterPixels.x, jitterPixels.y);
    }

    // Compute the actual raster UV displacement, including projection flipping.
    // Motion vectors are non-jittered: history reprojection needs no jitter delta.
    private static Vector2 GetSampleDisplacement(UniversalCameraData data, CameraJitter jitter)
    {
        Matrix4x4 projection = data.GetProjectionMatrix();
        const bool flipped = true; // Same intermediate render target as the jitter pass.
        Vector4 p = new(0, 0, -1, 1);
        Vector4 original = GL.GetGPUProjectionMatrix(projection, flipped) * p;
        Vector4 shifted = GL.GetGPUProjectionMatrix(jitter.GetJitteredProjectionMatrix(projection), flipped) * p;
        Vector2 uv = new Vector2(shifted.x / shifted.w - original.x / original.w,
            shifted.y / shifted.w - original.y / original.w) * 0.5f;
        if (SystemInfo.graphicsUVStartsAtTop) uv.y = -uv.y;
        return uv;
    }

    private static bool Approximately(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; ++i)
            if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false;
        return true;
    }

    public void ResetHistory(Camera camera = null)
    {
        if (camera != null)
        {
            if (histories.TryGetValue(camera, out var state)) state.valid = false;
        }
        else foreach (var state in histories.Values) state.valid = false;
    }

    private void PruneHistories()
    {
        expiredCameras.Clear();
        foreach (var pair in histories)
            if (pair.Key == null || Time.frameCount - pair.Value.lastFrame > 300) expiredCameras.Add(pair.Key);
        foreach (Camera camera in expiredCameras)
        {
            histories[camera].Release();
            histories.Remove(camera);
        }
    }

    public void Dispose()
    {
        foreach (var state in histories.Values) state.Release();
        histories.Clear();
    }
}
