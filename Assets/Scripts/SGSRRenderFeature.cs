using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SGSR : ScriptableRendererFeature
{
    public static float CurrentRenderScale { get; private set; } = 1.0f;

    [SerializeField] private SGSRPass.SGSRSettings settings = new();
    private SGSRPass m_ScriptablePass;
    private CameraJitterRenderPass m_ApplyJitterPass;
    private CameraJitterRenderPass m_RestoreJitterPass;
    private SGSROpaqueColorPass m_OpaqueColorPass;
    private bool warnedUnsupportedConfiguration;
    private string lastAlgorithmError;

    public override void Create()
    {
        m_ScriptablePass?.Dispose();
        if (settings == null)
            return;
        
        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        m_ApplyJitterPass = new CameraJitterRenderPass(RenderPassEvent.BeforeRenderingPrePasses, true, settings);
        m_RestoreJitterPass = new CameraJitterRenderPass(RenderPassEvent.AfterRendering, false, settings);
        m_OpaqueColorPass = new SGSROpaqueColorPass();

        m_ScriptablePass = new SGSRPass(settings)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
        };

    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings == null || m_ScriptablePass == null)
            return;

        // Preview/reflection cameras and overlay cameras do not own a temporal scene history.
        if (renderingData.cameraData.cameraType != CameraType.Game ||
            renderingData.cameraData.renderType != CameraRenderType.Base ||
            renderingData.cameraData.xr.enabled || !renderingData.cameraData.resolveFinalTarget)
            return;

        string algorithmError = settings.UseCompute
            ? GetComputeSupportError(settings.computeShader)
            : settings.material == null ? "2-pass-FS material is not assigned." : null;
        if (algorithmError != null)
        {
            if (lastAlgorithmError != algorithmError)
                Debug.LogWarning($"SGSR skipped: {algorithmError} Scene resolution and jitter have not been changed.", this);
            lastAlgorithmError = algorithmError;
            CurrentRenderScale = 1.0f;
            m_ScriptablePass.ResetHistory(renderingData.cameraData.camera);
            return;
        }
        lastAlgorithmError = null;

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
        if (settings.UseCompute) renderer.EnqueuePass(m_OpaqueColorPass);
        // Depth and object/camera motion must have been generated; also include transparents.
        m_ScriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        renderer.EnqueuePass(m_ScriptablePass);
        renderer.EnqueuePass(m_RestoreJitterPass);
    }

    public void ResetHistory(Camera camera = null) => m_ScriptablePass?.ResetHistory(camera);

    private static string GetComputeSupportError(ComputeShader shader)
    {
        if (shader == null) return "3-pass-CS Compute Shader is not assigned; assign SGSR3Pass.compute.";
        if (!SystemInfo.supportsComputeShaders) return $"Compute shaders are unsupported on {SystemInfo.graphicsDeviceType}.";
        if (!shader.HasKernel("Convert")) return "Compute Shader is missing the Convert kernel; check shader import errors.";
        if (!shader.HasKernel("Activate")) return "Compute Shader is missing the Activate kernel; check shader import errors.";
        if (!shader.HasKernel("Upscale")) return "Compute Shader is missing the Upscale kernel; check shader import errors.";
        // Packed uint textures use Texture2D<uint>.Load and UAV writes, never
        // Sample/SampleLevel. Requiring sampler support rejects valid integer
        // formats on backends that distinguish shader loads from sampling.
        if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, GraphicsFormatUsage.LoadStore))
            return "R32_UInt LoadStore is unsupported (packed color/luma UAVs).";
        if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Sample))
            return "RGBA16F Sample is unsupported (color history).";
        if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Linear))
            return "RGBA16F Linear is unsupported (bilinear color history).";
        if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.LoadStore))
            return "RGBA16F LoadStore is unsupported (metadata/history/scene UAVs).";
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        m_ScriptablePass?.Dispose();
        m_ScriptablePass = null;
        m_ApplyJitterPass = null;
        m_RestoreJitterPass = null;
        m_OpaqueColorPass = null;
    }

    private void OnValidate()
    {   
        if (settings == null)
            return;
        
        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
    }
}
