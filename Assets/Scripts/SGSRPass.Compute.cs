using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public partial class SGSRPass
{
    private static bool AllocateLuma(CameraHistory state, RenderTextureDescriptor desc)
    {
        desc.depthBufferBits = 0;
        desc.depthStencilFormat = GraphicsFormat.None;
        desc.graphicsFormat = GraphicsFormat.R32_UInt;
        desc.msaaSamples = 1;
        desc.bindMS = false;
        desc.enableRandomWrite = true;
        desc.useMipMap = desc.autoGenerateMips = desc.useDynamicScale = false;
        bool changed = RenderingUtils.ReAllocateHandleIfNeeded(ref state.lumaA, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_SGSR_LumaA");
        changed |= RenderingUtils.ReAllocateHandleIfNeeded(ref state.lumaB, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_SGSR_LumaB");
        return changed;
    }

    private sealed class ComputeData
    {
        public ComputeShader shader;
        public int kernel, stage, reset, reversedZ, groupsX, groupsY;
        public Vector4 renderSize, outputSize, jitter;
        public float exposure, fov;
        public TextureHandle input, opaque, depth, velocity, color, metadata;
        public TextureHandle previous, colorOut, metadataOut, historyOut, sceneOut;
    }

    private static void ExecuteCompute(ComputeData d, ComputeGraphContext ctx)
    {
        var cmd = ctx.cmd;
        // Record per-dispatch constants; never mutate shared ComputeShader state.
        cmd.SetComputeVectorParam(d.shader, "_RenderSize", d.renderSize);
        cmd.SetComputeVectorParam(d.shader, "_OutputSize", d.outputSize);
        cmd.SetComputeVectorParam(d.shader, "_Jitter", d.jitter);
        cmd.SetComputeFloatParam(d.shader, "_PreExposure", d.exposure);
        cmd.SetComputeFloatParam(d.shader, "_CameraFov", d.fov);
        cmd.SetComputeIntParam(d.shader, "_Reset", d.reset);
        cmd.SetComputeIntParam(d.shader, "_ReversedZ", d.reversedZ);
        if (d.stage == 0)
        {
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_InputColor", d.input);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_InputOpaqueColor", d.opaque);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_InputDepth", d.depth);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_InputVelocity", d.velocity);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_ColorLumaOut", d.colorOut);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_MotionDepthAlphaOut", d.metadataOut);
        }
        else if (d.stage == 1)
        {
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_ColorLuma", d.color);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_MotionDepthAlpha", d.metadata);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_PreviousLuma", d.previous);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_LumaOut", d.historyOut);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_MotionDepthClipAlphaOut", d.metadataOut);
        }
        else
        {
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_InputColor", d.input);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_ColorLuma", d.color);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_MotionDepthClipAlpha", d.metadata);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_PreviousHistory", d.previous);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_HistoryOut", d.historyOut);
            cmd.SetComputeTextureParam(d.shader, d.kernel, "_SceneOut", d.sceneOut);
        }
        cmd.DispatchCompute(d.shader, d.kernel, d.groupsX, d.groupsY, 1);
    }

    private TextureHandle RecordCompute(RenderGraph graph, ContextContainer frameData, CameraHistory state,
        TextureHandle input, TextureHandle historyRead, TextureHandle historyWrite,
        Vector4 renderSize, Vector4 outputSize, Vector4 jitter, float fov, bool reset,
        RenderTextureDescriptor outputDesc)
    {
        var resources = frameData.Get<UniversalResourceData>();
        var opaque = frameData.Get<SGSROpaqueSnapshot>().color;
        var lowDesc = outputDesc;
        lowDesc.width = (int)renderSize.x;
        lowDesc.height = (int)renderSize.y;
        lowDesc.enableRandomWrite = true;
        lowDesc.graphicsFormat = GraphicsFormat.R32_UInt;
        var color = UniversalRenderer.CreateRenderGraphTexture(graph, lowDesc, "_SGSR_YCoCg", false, FilterMode.Point);
        lowDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        var mda = UniversalRenderer.CreateRenderGraphTexture(graph, lowDesc, "_SGSR_MotionDepthAlpha", false, FilterMode.Point);
        var mdca = UniversalRenderer.CreateRenderGraphTexture(graph, lowDesc, "_SGSR_MotionDepthClipAlpha", false, FilterMode.Point);
        var lumaRead = graph.ImportTexture(state.index == 0 ? state.lumaA : state.lumaB);
        var lumaWrite = graph.ImportTexture(state.index == 0 ? state.lumaB : state.lumaA);
        var sceneDesc = outputDesc;
        sceneDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
        sceneDesc.enableRandomWrite = true;
        var scene = UniversalRenderer.CreateRenderGraphTexture(graph, sceneDesc, "_SGSR_CSScene", false, FilterMode.Bilinear);
        string[] kernels = { "Convert", "Activate", "Upscale" };
        for (int stage = 0; stage < 3; ++stage)
        {
            using var builder = graph.AddComputePass<ComputeData>("SGSR CS " + kernels[stage], out var d);
            d.shader = settings.computeShader;
            d.kernel = d.shader.FindKernel(kernels[stage]);
            d.stage = stage;
            d.renderSize = renderSize;
            d.outputSize = outputSize;
            d.jitter = new Vector4(jitter.x, jitter.y, state.jitterPixels.x, state.jitterPixels.y);
            d.exposure = Mathf.Max(0.0001f, settings.preExposure);
            d.fov = fov;
            d.reset = reset ? 1 : 0;
            d.reversedZ = SystemInfo.usesReversedZBuffer ? 1 : 0;
            var size = stage == 2 ? outputSize : renderSize;
            d.groupsX = ((int)size.x + 7) / 8;
            d.groupsY = ((int)size.y + 7) / 8;
            if (stage == 0)
            {
                d.input = input; d.opaque = opaque;
                d.depth = resources.cameraDepthTexture; d.velocity = resources.motionVectorColor;
                d.colorOut = color; d.metadataOut = mda;
                builder.UseTexture(input); builder.UseTexture(opaque);
                builder.UseTexture(d.depth); builder.UseTexture(d.velocity);
                builder.UseTexture(color, AccessFlags.Write); builder.UseTexture(mda, AccessFlags.Write);
            }
            else if (stage == 1)
            {
                d.color = color; d.metadata = mda; d.previous = lumaRead;
                d.historyOut = lumaWrite; d.metadataOut = mdca;
                builder.UseTexture(color); builder.UseTexture(mda); builder.UseTexture(lumaRead);
                builder.UseTexture(lumaWrite, AccessFlags.Write); builder.UseTexture(mdca, AccessFlags.Write);
            }
            else
            {
                d.input = input; d.color = color; d.metadata = mdca; d.previous = historyRead;
                d.historyOut = historyWrite; d.sceneOut = scene;
                builder.UseTexture(input); builder.UseTexture(color);
                builder.UseTexture(mdca); builder.UseTexture(historyRead);
                builder.UseTexture(historyWrite, AccessFlags.Write); builder.UseTexture(scene, AccessFlags.Write);
            }
            builder.SetRenderFunc(static (ComputeData data, ComputeGraphContext ctx) => ExecuteCompute(data, ctx));
        }
        // Display RGB and temporal YCoCg/state are distinct outputs. Match the
        // original camera format before handing color to URP post processing.
        var output = UniversalRenderer.CreateRenderGraphTexture(graph, outputDesc,
            "_SGSR_OutputColor", false, FilterMode.Bilinear);
        graph.AddBlitPass(scene, output, Vector2.one, Vector2.zero, passName: "SGSR CS Present");
        return output;
    }
}
