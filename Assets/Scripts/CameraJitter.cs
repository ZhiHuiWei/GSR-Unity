using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class CameraJitter : MonoBehaviour
{
    public bool enableJitter = true;

    [Range(0, 16)]
    public int jitterScale = 1;

    [Range(1, 64)]
    public int jitterPhaseCount = 8;

    public Vector2 PreviousJitterPixels { get; private set; }
    public Vector2 CurrentJitterPixels { get; private set; }
    public Vector2 PreviousJitterUV { get; private set; }
    public Vector2 CurrentJitterUV { get; private set; }

    private Camera _camera;
    private Matrix4x4 _nonJitteredProjection;
    private bool _hasJitteredProjection;
    private int _frameIndex;

    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnEnable()
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        RestoreProjection();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (renderingCamera != _camera)
            return;

        if (!enableJitter || jitterScale <= 0.0f)
        {
            RestoreProjection();
            PreviousJitterPixels = Vector2.zero;
            CurrentJitterPixels = Vector2.zero;
            PreviousJitterUV = Vector2.zero;
            CurrentJitterUV = Vector2.zero;
            return;
        }

        float renderScale = Mathf.Clamp(SGSR.CurrentRenderScale, 0.1f, 1.0f);
        int renderWidth = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelWidth * renderScale));
        int renderHeight = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelHeight * renderScale));

        _nonJitteredProjection = _camera.projectionMatrix;

        Vector2 jitter = GetHaltonJitter(_frameIndex % Mathf.Max(1, jitterPhaseCount)) * jitterScale;
        PreviousJitterPixels = CurrentJitterPixels;
        PreviousJitterUV = CurrentJitterUV;
        CurrentJitterPixels = jitter;
        CurrentJitterUV = new Vector2(jitter.x / renderWidth, jitter.y / renderHeight);

        Matrix4x4 projection = _nonJitteredProjection;
        projection.m02 += CurrentJitterUV.x * 2.0f;
        projection.m12 += CurrentJitterUV.y * 2.0f;

        _camera.nonJitteredProjectionMatrix = _nonJitteredProjection;
        _camera.projectionMatrix = projection;
        _hasJitteredProjection = true;

        _frameIndex++;
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (renderingCamera != _camera)
            return;

        RestoreProjection();
    }

    private void RestoreProjection()
    {
        if (!_hasJitteredProjection || _camera == null)
            return;

        _camera.projectionMatrix = _nonJitteredProjection;
        _camera.nonJitteredProjectionMatrix = _nonJitteredProjection;
        _hasJitteredProjection = false;
    }

    private Vector2 GetHaltonJitter(int index)
    {
        float x = Halton(index + 1, 2) - 0.5f;
        float y = Halton(index + 1, 3) - 0.5f;
        return new Vector2(x, y);
    }

    private static float Halton(int index, int radix)
    {
        float result = 0.0f;
        float fraction = 1.0f / radix;

        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }
}