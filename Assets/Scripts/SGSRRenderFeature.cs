using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SGSR : ScriptableRendererFeature
{
    public static float CurrentRenderScale { get; private set; } = 1.0f;

    [SerializeField] private SGSRPass.SGSRSettings settings = new();
    private SGSRPass m_ScriptablePass;
    private CameraJitterRenderPass m_ApplyJitterPass;
    private CameraJitterRenderPass m_RestoreJitterPass;
    private bool warnedUnsupportedConfiguration;

    public override void Create()
    {
        m_ScriptablePass?.Dispose();
        if (settings == null)
            return;
        
        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        m_ApplyJitterPass = new CameraJitterRenderPass(RenderPassEvent.BeforeRenderingPrePasses, true, settings);
        m_RestoreJitterPass = new CameraJitterRenderPass(RenderPassEvent.AfterRendering, false, settings);

        m_ScriptablePass = new SGSRPass(settings)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
        };

    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings == null || settings.material == null || m_ScriptablePass == null)
            return;

        // Preview/reflection cameras and overlay cameras do not own a temporal scene history.
        if (renderingData.cameraData.cameraType != CameraType.Game ||
            renderingData.cameraData.renderType != CameraRenderType.Base ||
            renderingData.cameraData.xr.enabled || !renderingData.cameraData.resolveFinalTarget)
            return;

        // URP has already chosen its built-in scaling/TAA path at this point.
        // Keep that path at native resolution; SGSR owns the scene scale below.
        if (!Mathf.Approximately(UniversalRenderPipeline.asset.renderScale, 1.0f) ||
            (UniversalRenderPipeline.asset.upscalingFilter != UpscalingFilterSelection.Auto &&
             UniversalRenderPipeline.asset.upscalingFilter != UpscalingFilterSelection.Linear &&
             UniversalRenderPipeline.asset.upscalingFilter != UpscalingFilterSelection.Point) ||
            renderingData.cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing ||
            renderingData.cameraData.cameraTargetDescriptor.useDynamicScale)
        {
            if (!warnedUnsupportedConfiguration)
                Debug.LogWarning("SGSR requires URP Render Scale = 1, Upscaling Filter = Automatic/Bilinear/Nearest-Neighbor, TAA off, and hardware dynamic resolution off. Use SGSR Render Scale to set scene resolution.", this);
            warnedUnsupportedConfiguration = true;
            m_ScriptablePass.ResetHistory(renderingData.cameraData.camera);
            return;
        }
        warnedUnsupportedConfiguration = false;

        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        // AddRenderPasses runs before CreateRenderGraphCameraRenderTargets.
        // This changes rasterization itself, including depth and motion targets.
        m_ScriptablePass.PrepareCamera(ref renderingData.cameraData);
        renderer.EnqueuePass(m_ApplyJitterPass);
        // Depth and object/camera motion must have been generated; also include transparents.
        m_ScriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        renderer.EnqueuePass(m_ScriptablePass);
        renderer.EnqueuePass(m_RestoreJitterPass);
    }

    public void ResetHistory(Camera camera = null) => m_ScriptablePass?.ResetHistory(camera);

    protected override void Dispose(bool disposing)
    {
        m_ScriptablePass?.Dispose();
        m_ScriptablePass = null;
        m_ApplyJitterPass = null;
        m_RestoreJitterPass = null;
    }

    private void OnValidate()
    {   
        if (settings == null)
            return;
        
        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
    }
}
