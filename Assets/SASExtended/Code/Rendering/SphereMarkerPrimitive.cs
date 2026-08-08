using UnityEngine;

namespace SASExtended.Rendering
{

/// <summary>
/// A small solid ball marking a world-space point. Replaces
/// <c>DebugTools.Utils.DebugShapesSphereMarker</c> (Shapes <c>Draw.Sphere</c>), which goes away with
/// the Shapes library.
///
/// The stock marker rendered an unshaded ball, and so does this: the overlay material is unlit, so a
/// sphere mesh reads as a flat-coloured disc from every angle - which is what a position marker wants
/// anyway.
/// </summary>
internal sealed class SphereMarkerPrimitive
{
    private readonly GameObject _root;
    private readonly Material _material;

    public SphereMarkerPrimitive(string name, Transform parent, Color color, float radius)
    {
        _root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _root.name = name;
        _root.transform.SetParent(parent, worldPositionStays: false);
        _root.transform.localScale = Vector3.one * (radius * 2f); // primitive sphere is 1 unit ACROSS

        // CreatePrimitive attaches a collider. Left in place it would be a physics body sitting at the
        // vessel's centre of mass - a decorative marker must never be able to touch the simulation, so
        // it is destroyed immediately.
        var collider = _root.GetComponent<Collider>();
        if (collider != null)
            Object.Destroy(collider);

        _material = OverlayMaterial.Create(color);

        var renderer = _root.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }
    }

    public bool IsAlive => _root != null;

    public void SetActive(bool active)
    {
        if (_root != null && _root.activeSelf != active)
            _root.SetActive(active);
    }

    public void SetCenter(Vector3 center)
    {
        if (_root != null)
            _root.transform.position = center;
    }

    public void SetRadius(float radius)
    {
        if (_root != null)
            _root.transform.localScale = Vector3.one * (radius * 2f);
    }

    public void Destroy()
    {
        if (_material != null) Object.Destroy(_material);
        if (_root != null) Object.Destroy(_root);
    }
}
}
