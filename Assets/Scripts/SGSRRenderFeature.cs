using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class SGSR : ScriptableRendererFeature
{   
    [SerializeField] private SGSRPass.SGSRSettings settings = new();
    private SGSRPass m_ScriptablePass;

    public override void Create()
    {
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
        //  每帧 enqueue 前同步
        m_ScriptablePass.renderPassEvent = settings.renderPassEvent;
        
        renderer.EnqueuePass(m_ScriptablePass);
    }
}
