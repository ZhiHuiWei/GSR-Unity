using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class SGSRPass : ScriptableRenderPass
{   
    private static readonly int SgsrDepthTextureId = Shader.PropertyToID("_SGSRDepthTexture");
    private static readonly int MotionVectorTextureId = Shader.PropertyToID("_MotionVectorTexture");
    private static readonly int DebugModeId = Shader.PropertyToID("_DebugMode");
    private static readonly int MotionScaleId = Shader.PropertyToID("_MotionScale");

    [Serializable]
    public class SGSRSettings
    {
        [Range(0.1f, 1.0f)]
        public float renderScale = 1.0f;

        public Material material;

        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        [Range(0, 2)]
        public int debugMode = 0;

        public float motionScale = 16.0f;
    }
    
    private readonly SGSRSettings settings;

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
        public Material material;
        public int passIndex;
        public int debugMode;
        public float motionScale;
    }

    // This static method is passed as the RenderFunc delegate to the RenderGraph render pass.
    // It is used to execute draw commands.
    static void ExecutePass(PassData data, RasterGraphContext context)
    {
        data.material.SetFloat(DebugModeId, data.debugMode);
        data.material.SetFloat(MotionScaleId, data.motionScale);

        if (data.depth.IsValid())
            data.material.SetTexture(SgsrDepthTextureId, data.depth);
        if (data.motion.IsValid())
            data.material.SetTexture(MotionVectorTextureId, data.motion);
        
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
        desc.msaaSamples = 1;

        TextureHandle lowResTexture =
            UniversalRenderer.CreateRenderGraphTexture(
                renderGraph,
                desc,
                "_SGSR_LowResTexture",
                false
            );
        
        TextureHandle cameraColor = resourceData.activeColorTexture;
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
            passData.material = settings.material;
            passData.passIndex = 0;
            passData.debugMode = settings.debugMode;
            passData.motionScale = settings.motionScale;
            
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
            passData.depth = TextureHandle.nullHandle;
            passData.motion = TextureHandle.nullHandle;
            passData.material = settings.material;
            passData.passIndex = 1;
            passData.debugMode = settings.debugMode;
            passData.motionScale = settings.motionScale;
            
            builder.UseTexture(lowResTexture);
            builder.SetRenderAttachment(cameraColor, 0);
            builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
        }
    }
}
