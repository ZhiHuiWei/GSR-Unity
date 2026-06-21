using UnityEngine;

public class FrameRateLimiter : MonoBehaviour
{
    [SerializeField] private int targetFrameRate = 60;
    [SerializeField] private bool useVSync;

    private void Awake()
    {
        ApplyFrameRate();
    }

    private void OnValidate()
    {
        targetFrameRate = Mathf.Max(1, targetFrameRate);

        if (Application.isPlaying)
            ApplyFrameRate();
    }

    private void ApplyFrameRate()
    {
        QualitySettings.vSyncCount = useVSync ? 1 : 0;
        Application.targetFrameRate = useVSync ? -1 : targetFrameRate;
    }
}
