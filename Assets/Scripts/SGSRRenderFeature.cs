using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SGSR : ScriptableRendererFeature
{
    public static float CurrentRenderScale { get; private set; } = 1.0f;

    [SerializeField] private SGSRPass.SGSRSettings settings = new();
    private SGSRPass m_ScriptablePass;

    public override void Create()
    {
        SyncSharedSettings();

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

        SyncSharedSettings();
        m_ScriptablePass.renderPassEvent = settings.renderPassEvent;

        renderer.EnqueuePass(m_ScriptablePass);
    }

    protected override void Dispose(bool disposing)
    {
        m_ScriptablePass?.Dispose();
        m_ScriptablePass = null;
    }

    private void OnValidate()
    {
        SyncSharedSettings();
    }

    private void SyncSharedSettings()
    {
        if (settings == null)
            return;

        CurrentRenderScale = Mathf.Clamp(settings.renderScale, 0.1f, 1.0f);
    }
}
