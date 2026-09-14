using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshRenderer))]
public sealed class SphereMaterialToggle : MonoBehaviour
{
    [SerializeField, InspectorName("Use Transparent Material")]
    private bool useTransparentMaterial = true;
    [SerializeField] private Material opaqueMaterial;
    [SerializeField] private Material transparentMaterial;

    private MeshRenderer sphereRenderer;

    private void OnEnable()
    {
        sphereRenderer = GetComponent<MeshRenderer>();
        ApplyMaterial();
    }

    private void Update()
    {
        // Inspector changes take effect on the next frame without creating materials.
        ApplyMaterial();
    }

    private void ApplyMaterial()
    {
        Material selected = useTransparentMaterial ? transparentMaterial : opaqueMaterial;
        if (selected != null && sphereRenderer.sharedMaterial != selected)
            sphereRenderer.sharedMaterial = selected;
    }

    private void OnDisable()
    {
        if (sphereRenderer != null && opaqueMaterial != null)
            sphereRenderer.sharedMaterial = opaqueMaterial;
    }
}
