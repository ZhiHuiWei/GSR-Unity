using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public sealed class SGSROpaqueSnapshot : ContextItem
{
    public TextureHandle color;
    public override void Reset() => color = TextureHandle.nullHandle;
}

public sealed class SGSROpaqueColorPass : ScriptableRenderPass
{
    public SGSROpaqueColorPass() => renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

    public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
    {
        var snapshot = frameData.GetOrCreate<SGSROpaqueSnapshot>();
        snapshot.Reset();
        var resources = frameData.Get<UniversalResourceData>();
        if (resources.isActiveTargetBackBuffer) return;
        var desc = frameData.Get<UniversalCameraData>().cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        desc.depthStencilFormat = GraphicsFormat.None;
        desc.msaaSamples = 1;
        desc.bindMS = false;
        desc.enableRandomWrite = false;
        desc.useMipMap = desc.autoGenerateMips = desc.useDynamicScale = false;
        // A real copy, including skybox, at scene resolution. URP's optional
        // cameraOpaqueTexture can be downsampled and is not interchangeable.
        snapshot.color = UniversalRenderer.CreateRenderGraphTexture(graph, desc,
            "_SGSR_OpaqueColor", false, FilterMode.Point);
        graph.AddBlitPass(resources.activeColorTexture, snapshot.color, Vector2.one,
            Vector2.zero, passName: "SGSR Opaque Snapshot");
    }
}
