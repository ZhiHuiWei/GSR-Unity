using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class CameraJitterRenderPass : ScriptableRenderPass
{
    private readonly bool _isApplyPass;
    private readonly SGSRPass.SGSRSettings _settings;

    private class PassData
    {
        public Matrix4x4 viewMatrix;
        public Matrix4x4 projectionMatrix;
        public Matrix4x4 gpuProjectionMatrix;
    }

    public CameraJitterRenderPass(RenderPassEvent evt, bool isApplyPass, SGSRPass.SGSRSettings settings)
    {
        renderPassEvent = evt;
        this._isApplyPass = isApplyPass;
        this._settings = settings;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!TryGetMatrices(
                renderingData.cameraData.camera,
                renderingData.cameraData.IsCameraProjectionMatrixFlipped(),
                out Matrix4x4 viewMatrix,
                out Matrix4x4 projectionMatrix,
                out Matrix4x4 gpuProjectionMatrix))
            return;

        CommandBuffer cmd = CommandBufferPool.Get(_isApplyPass ? "Apply Camera Jitter" : "Restore Camera Jitter");
        
        // 管线上和 shader 内用的 matrix 不来自同一个地方，所以需要 set 两次
        cmd.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
        RenderingUtils.SetViewAndProjectionMatrices(cmd, viewMatrix, gpuProjectionMatrix, true);
        
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

        if (!TryGetMatrices(
                cameraData.camera,
                cameraData.IsCameraProjectionMatrixFlipped(),
                out Matrix4x4 viewMatrix,
                out Matrix4x4 projectionMatrix,
                out Matrix4x4 gpuProjectionMatrix))
            return;

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                   _isApplyPass ? "Apply Camera Jitter" : "Restore Camera Jitter",
                   out PassData passData))
        {
            passData.viewMatrix = viewMatrix;
            passData.projectionMatrix = projectionMatrix;
            passData.gpuProjectionMatrix = gpuProjectionMatrix;

            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);    // 防止 RendereGraph 认为 Pass 无用删除
            builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
            {
                context.cmd.SetViewProjectionMatrices(data.viewMatrix, data.projectionMatrix);
                RenderingUtils.SetViewAndProjectionMatrices(context.cmd, data.viewMatrix, data.gpuProjectionMatrix, true);
            });
        }
    }

    private bool TryGetMatrices(
        Camera camera,
        bool projectionMatrixFlipped,
        out Matrix4x4 viewMatrix,
        out Matrix4x4 projectionMatrix,
        out Matrix4x4 gpuProjectionMatrix)
    {
        viewMatrix = Matrix4x4.identity;
        projectionMatrix = Matrix4x4.identity;
        gpuProjectionMatrix = Matrix4x4.identity;

        if (camera == null)
            return false;

        Matrix4x4 nonJitteredProjection = camera.projectionMatrix;
        projectionMatrix = nonJitteredProjection;

        if (_isApplyPass)
        {
            CameraJitter jitter = camera.GetComponent<CameraJitter>();
            if (jitter == null || !jitter.enabled || !jitter.UpdateJitter(_settings))
                return false;

            projectionMatrix = jitter.GetJitteredProjectionMatrix(nonJitteredProjection);
        }

        viewMatrix = camera.worldToCameraMatrix;
        gpuProjectionMatrix = GL.GetGPUProjectionMatrix(projectionMatrix, projectionMatrixFlipped);
        return true;
    }
}
