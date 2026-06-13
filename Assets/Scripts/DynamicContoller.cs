using UnityEngine;

public class DynamicContoller : MonoBehaviour
{
    public Transform rotatingCube;
    public Transform movingSphere;
    public Transform mainCamera;
    
    public float cubeRotateSpeed = 45.0f;
    public float sphereMoveAmplitude = 1.5f;
    public float sphereMoveSpeed = 1.0f;
    public float cameraMoveAmplitude = 0.3f;
    public float cameraMoveSpeed = 0.5f;
    
    private Vector3 _sphereBasePos;
    private Vector3 _cameraBasePos;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (movingSphere != null)
            _sphereBasePos = movingSphere.position;
        
        if (mainCamera != null)
            _cameraBasePos = mainCamera.position;
    }

    // Update is called once per frame
    void Update()
    {
        float t = Time.time;

        if (rotatingCube != null)
        {
            rotatingCube.Rotate(Vector3.up, cubeRotateSpeed * Time.deltaTime, Space.World);
            rotatingCube.Rotate(Vector3.right, cubeRotateSpeed * .5f * Time.deltaTime, Space.World);
        }

        if (movingSphere != null)
        {
            var pos = _sphereBasePos;
            pos.x += Mathf.Sin(t * sphereMoveSpeed) * sphereMoveAmplitude;
            movingSphere.position = pos;
        }

        if (mainCamera != null)
        {
            var pos = _cameraBasePos;
            pos.x += Mathf.Sin(t * cameraMoveSpeed) * cameraMoveAmplitude;
            mainCamera.position = pos;
        }
    }
}
