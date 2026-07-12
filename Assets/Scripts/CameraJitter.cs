using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraJitter : MonoBehaviour
{
    public Vector2 PreviousJitterPixels { get; private set; }
    public Vector2 CurrentJitterPixels { get; private set; }
    public Vector2 PreviousJitterUV { get; private set; }
    public Vector2 CurrentJitterUV { get; private set; }

    private Camera _camera;
    private int _frameIndex;

    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnEnable()
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();
    }

    private void OnDisable()
    {
        ResetJitter();
    }

    public bool UpdateJitter(SGSRPass.SGSRSettings settings)
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();
        
        if (settings == null || !settings.enableJitter || settings.jitterScale <= 0)
        {
            ResetJitter();
            return false;
        }

        int renderWidth = Mathf.Max(1, _camera.pixelWidth);
        int renderHeight = Mathf.Max(1, _camera.pixelHeight);

        Vector2 jitter = GetHaltonJitter(_frameIndex % Mathf.Max(1, settings.jitterPhaseCount)) * settings.jitterScale;
        PreviousJitterPixels = CurrentJitterPixels;
        PreviousJitterUV = CurrentJitterUV;
        CurrentJitterPixels = jitter;
        CurrentJitterUV = new Vector2(jitter.x / renderWidth, jitter.y / renderHeight);

        _frameIndex++;
        return true;
    }

    private void ResetJitter()
    {
        PreviousJitterPixels = Vector2.zero;
        CurrentJitterPixels = Vector2.zero;
        PreviousJitterUV = Vector2.zero;
        CurrentJitterUV = Vector2.zero;
    }

    public Matrix4x4 GetJitteredProjectionMatrix(Matrix4x4 nonJitteredProjection)
    {
        Matrix4x4 projection = nonJitteredProjection;
        projection.m02 += CurrentJitterUV.x * 2.0f;
        projection.m12 += CurrentJitterUV.y * 2.0f;

        return projection;
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
