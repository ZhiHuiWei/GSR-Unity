using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class CameraJitterRenderPass : ScriptableRenderPass
{
    private static readonly int InverseViewId = Shader.PropertyToID("unity_MatrixInvV");
    private static readonly int InverseProjectionId = Shader.PropertyToID("unity_MatrixInvP");
    private static readonly int InverseViewProjectionId = Shader.PropertyToID("unity_MatrixInvVP");
    private readonly bool _isApplyPass;
    private readonly SGSRPass.SGSRSettings _settings;

    private class PassData
    {
        public Matrix4x4 viewMatrix;
        public Matrix4x4 projectionMatrix;
        public Matrix4x4 inverseViewMatrix;
        public Matrix4x4 inverseProjectionMatrix;
        public Matrix4x4 inverseViewProjectionMatrix;
    }

    public CameraJitterRenderPass(RenderPassEvent evt, bool isApplyPass, SGSRPass.SGSRSettings settings)
    {
        renderPassEvent = evt;
        this._isApplyPass = isApplyPass;
        this._settings = settings;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        if (_isApplyPass)
        {
            cameraData.scaledWidth = cameraData.cameraTargetDescriptor.width;
            cameraData.scaledHeight = cameraData.cameraTargetDescriptor.height;
        }

        if (!TryGetMatrices(
                cameraData,
                cameraData.cameraTargetDescriptor.width,
                cameraData.cameraTargetDescriptor.height,
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
            passData.inverseViewMatrix = viewMatrix.inverse;
            passData.inverseProjectionMatrix = gpuProjectionMatrix.inverse;
            passData.inverseViewProjectionMatrix = passData.inverseViewMatrix * passData.inverseProjectionMatrix;

            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);    // 防止 RendereGraph 认为 Pass 无用删除
            builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
            {
                context.cmd.SetViewProjectionMatrices(data.viewMatrix, data.projectionMatrix);
                // Match ScriptableRenderer.SetCameraMatrices: Unity converts the
                // forward projection for the active render target and winding.
                // Overwriting unity_MatrixP/VP with a pre-flipped GPU matrix here
                // breaks that contract and can cull front-facing geometry.
                context.cmd.SetGlobalMatrix(InverseViewId, data.inverseViewMatrix);
                context.cmd.SetGlobalMatrix(InverseProjectionId, data.inverseProjectionMatrix);
                context.cmd.SetGlobalMatrix(InverseViewProjectionId, data.inverseViewProjectionMatrix);
            });
        }
    }

    private bool TryGetMatrices(
        UniversalCameraData cameraData,
        int targetWidth,
        int targetHeight,
        out Matrix4x4 viewMatrix,
        out Matrix4x4 projectionMatrix,
        out Matrix4x4 gpuProjectionMatrix)
    {
        viewMatrix = Matrix4x4.identity;
        projectionMatrix = Matrix4x4.identity;
        gpuProjectionMatrix = Matrix4x4.identity;

        Camera camera = cameraData.camera;
        if (camera == null)
            return false;

        // Use the same cached view/projection as URP (including its aspect setup).
        Matrix4x4 nonJitteredProjection = cameraData.GetProjectionMatrix();
        projectionMatrix = nonJitteredProjection;

        if (_isApplyPass)
        {
            CameraJitter jitter = camera.GetComponent<CameraJitter>();
            if (jitter == null || !jitter.enabled || !jitter.UpdateJitter(_settings, targetWidth, targetHeight))
                return false;

            projectionMatrix = jitter.GetJitteredProjectionMatrix(nonJitteredProjection);
        }

        viewMatrix = cameraData.GetViewMatrix();
        gpuProjectionMatrix = GL.GetGPUProjectionMatrix(projectionMatrix, true);
        return true;
    }
}
