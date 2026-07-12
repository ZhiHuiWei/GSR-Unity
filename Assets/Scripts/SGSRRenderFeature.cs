using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SGSR : ScriptableRendererFeature
{
    public static float CurrentRenderScale { get; private set; } = 1.0f;

    [SerializeField] private SGSRPass.SGSRSettings settings = new();
    private SGSRPass m_ScriptablePass;
    private CameraJitterRenderPass m_ApplyJitterPass;
    private CameraJitterRenderPass m_RestoreJitterPass;

    public override void Create()
    {
        if (settings == null)
            return;
        
        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        m_ApplyJitterPass = new CameraJitterRenderPass(RenderPassEvent.BeforeRenderingPrePasses, true, settings);
        m_RestoreJitterPass = new CameraJitterRenderPass(RenderPassEvent.AfterRendering, false, settings);

        m_ScriptablePass = new SGSRPass(settings)
        {
            renderPassEvent = settings.renderPassEvent
        };

        m_ScriptablePass.ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings == null || settings.material == null)
            return;

        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
        renderer.EnqueuePass(m_ApplyJitterPass);
        m_ScriptablePass.renderPassEvent = settings.renderPassEvent;

        renderer.EnqueuePass(m_ScriptablePass);
        renderer.EnqueuePass(m_RestoreJitterPass);
    }

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
