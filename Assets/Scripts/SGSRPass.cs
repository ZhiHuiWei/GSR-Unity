using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class SGSRPass : ScriptableRenderPass
{   
    private static readonly int SgsrDepthTextureId = Shader.PropertyToID("_SGSRDepthTexture");
    private static readonly int SgsrHistoryTextureId = Shader.PropertyToID("_SGSRHistoryTexture");
    private static readonly int MotionVectorTextureId = Shader.PropertyToID("_MotionVectorTexture");
    private static readonly int DebugModeId = Shader.PropertyToID("_DebugMode");
    private static readonly int MotionScaleId = Shader.PropertyToID("_MotionScale");
    private static readonly int HistoryBlendId = Shader.PropertyToID("_HistoryBlend");
    private static readonly int HistoryValidId = Shader.PropertyToID("_HistoryValid");

    public enum PresentMode
    {
        Normal,
        Upsample
    }

    [Serializable]
    public class SGSRSettings
    {
        [Range(0.1f, 1.0f)]
        public float renderScale = 1.0f;

        public Material material;

        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        public PresentMode presentMode = PresentMode.Upsample;

        public bool enableHistory = false;

        [Range(0.0f, 0.98f)]
        public float historyBlend = 0.9f;

        [Range(0, 2)]
        public int debugMode = 0;

        public float motionScale = 16.0f;
    }
    
    private readonly SGSRSettings settings;
    private RTHandle historyA;
    private RTHandle historyB;
    private bool historyValid;
    private int historyIndex;

    public SGSRPass(SGSRSettings settings)
    {
        this.settings = settings;
    }

    // This class stores the data needed by the RenderGraph pass.
    // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
    private class PassData
    {
        public TextureHandle source;
        public TextureHandle depth;
        public TextureHandle motion;
        public TextureHandle history;
        public Material material;
        public int passIndex;
        public int debugMode;
        public float motionScale;
        public float historyBlend;
        public bool historyValid;
    }

    // This static method is passed as the RenderFunc delegate to the RenderGraph render pass.
    // It is used to execute draw commands.
    static void ExecutePass(PassData data, RasterGraphContext context)
    {
        data.material.SetFloat(DebugModeId, data.debugMode);
        data.material.SetFloat(MotionScaleId, data.motionScale);
        data.material.SetFloat(HistoryBlendId, data.historyBlend);
        data.material.SetFloat(HistoryValidId, data.historyValid ? 1.0f : 0.0f);

        if (data.depth.IsValid())
            data.material.SetTexture(SgsrDepthTextureId, data.depth);
        if (data.motion.IsValid())
            data.material.SetTexture(MotionVectorTextureId, data.motion);
        if (data.history.IsValid())
            data.material.SetTexture(SgsrHistoryTextureId, data.history);
        
        Blitter.BlitTexture(
            context.cmd, 
            data.source,
            new Vector4(1, 1, 0, 0),
            data.material,
            data.passIndex
        );
    }

    // RecordRenderGraph is where the RenderGraph handle can be accessed, through which render passes can be added to the graph.
    // FrameData is a context container through which URP resources can be accessed and managed.
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {   
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        var cameraData = frameData.Get<UniversalCameraData>();
        
        if (resourceData.isActiveTargetBackBuffer)
            return;
        
        float scale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        
        RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
        desc.width = Mathf.Max(1, Mathf.RoundToInt(desc.width * scale));
        desc.height = Mathf.Max(1, Mathf.RoundToInt(desc.height * scale));
        desc.depthBufferBits = 0;
        desc.depthStencilFormat = GraphicsFormat.None;
        desc.msaaSamples = 1;

        TextureHandle lowResTexture =
            UniversalRenderer.CreateRenderGraphTexture(
                renderGraph,
                desc,
                "_SGSR_LowResTexture",
                false,
                settings.presentMode == PresentMode.Upsample ? FilterMode.Bilinear : FilterMode.Point
            );
        
        TextureHandle cameraColor = resourceData.activeColorTexture;
        RenderTextureDescriptor historyDesc = cameraData.cameraTargetDescriptor;
        historyDesc.depthBufferBits = 0;
        historyDesc.depthStencilFormat = GraphicsFormat.None;
        historyDesc.msaaSamples = 1;

        bool historyReady = settings.enableHistory && EnsureHistory(ref historyDesc);
        TextureHandle historyRead = historyReady
            ? renderGraph.ImportTexture(historyIndex == 0 ? historyA : historyB)
            : renderGraph.defaultResources.blackTexture;
        TextureHandle historyWrite = historyReady
            ? renderGraph.ImportTexture(historyIndex == 0 ? historyB : historyA)
            : TextureHandle.nullHandle;

        TextureHandle depthTexture = resourceData.activeDepthTexture.IsValid()
            ? resourceData.activeDepthTexture
            : renderGraph.defaultResources.whiteTexture;
        TextureHandle motionTexture = resourceData.motionVectorColor.IsValid()
            ? resourceData.motionVectorColor
            : renderGraph.defaultResources.blackTexture;

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                   "SGSR Debug Low Resolution Pass",
                   out var passData))
        {
            passData.source = cameraColor;
            passData.depth = depthTexture;
            passData.motion = motionTexture;
            passData.history = TextureHandle.nullHandle;
            passData.material = settings.material;
            passData.passIndex = 0;
            passData.debugMode = settings.debugMode;
            passData.motionScale = settings.motionScale;
            passData.historyBlend = 0.0f;
            passData.historyValid = false;
            
            builder.UseTexture(cameraColor);
            builder.UseTexture(depthTexture);
            builder.UseTexture(motionTexture);
            builder.SetRenderAttachment(lowResTexture, 0);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                   "SGSR Present Pass",
                   out var passData))
        {
            passData.source = lowResTexture;
            passData.depth = depthTexture;
            passData.motion = motionTexture;
            passData.history = historyRead;
            passData.material = settings.material;
            passData.passIndex = 1;
            passData.debugMode = settings.debugMode;
            passData.motionScale = settings.motionScale;
            passData.historyBlend = Mathf.Clamp01(settings.historyBlend);
            passData.historyValid = historyReady && historyValid;
            
            builder.UseTexture(lowResTexture);
            builder.UseTexture(depthTexture);
            builder.UseTexture(motionTexture);
            builder.UseTexture(historyRead);
            builder.SetRenderAttachment(cameraColor, 0);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }

        if (historyReady)
        {
            renderGraph.AddCopyPass(cameraColor, historyWrite, "SGSR Copy History Pass");
            historyValid = true;
            historyIndex = 1 - historyIndex;
        }
        else
        {
            historyValid = false;
        }
    }

    public void Dispose()
    {
        historyA?.Release();
        historyB?.Release();
        historyA = null;
        historyB = null;
        historyValid = false;
    }

    private bool EnsureHistory(ref RenderTextureDescriptor desc)
    {
        bool reallocatedA = RenderingUtils.ReAllocateHandleIfNeeded(
            ref historyA,
            desc,
            FilterMode.Bilinear,
            TextureWrapMode.Clamp,
            name: "_SGSR_HistoryA"
        );

        bool reallocatedB = RenderingUtils.ReAllocateHandleIfNeeded(
            ref historyB,
            desc,
            FilterMode.Bilinear,
            TextureWrapMode.Clamp,
            name: "_SGSR_HistoryB"
        );

        if (reallocatedA || reallocatedB)
        {
            historyValid = false;
            historyIndex = 0;
        }

        return historyA != null && historyB != null;
    }
}
