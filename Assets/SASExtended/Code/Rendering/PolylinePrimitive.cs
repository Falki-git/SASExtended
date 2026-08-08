using UnityEngine;

namespace SASExtended.Rendering
{

/// <summary>
/// A world-space polyline of constant width, optionally closed into a loop. Replaces the Shapes
/// immediate-mode <c>Draw.Line</c> / <c>Draw.Ring</c> calls, which go away with the Shapes library.
///
/// Unlike the Shapes originals this is a persistent mesh rather than a per-camera, per-frame draw
/// call: it only costs anything when its points actually change. That matters here, because the
/// per-frame immediate-mode cost is exactly why Redux is dropping Shapes.
/// </summary>
internal sealed class PolylinePrimitive
{
    private readonly GameObject _root;
    private readonly LineRenderer _line;
    private readonly Material _material;

    public PolylinePrimitive(string name, Transform parent, Color color, float width, bool loop)
    {
        _root = new GameObject(name);
        _root.transform.SetParent(parent, worldPositionStays: false);

        _material = OverlayMaterial.Create(color);

        _line = _root.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.loop = loop;
        _line.positionCount = 0;
        _line.numCapVertices = 0;
        _line.numCornerVertices = 0;
        _line.alignment = LineAlignment.View;
        _line.textureMode = LineTextureMode.Stretch;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        _line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        _line.widthMultiplier = width;
        _line.material = _material;
    }

    public bool IsAlive => _root != null;

    public void SetActive(bool active)
    {
        if (_root != null && _root.activeSelf != active)
            _root.SetActive(active);
    }

    public void SetColor(Color color)
    {
        if (_material != null)
            _material.color = color;
    }

    public void SetWidth(float width)
    {
        if (_line != null)
            _line.widthMultiplier = width;
    }

    /// <summary>
    /// Replaces the polyline's points. <paramref name="count"/> lets the caller reuse a larger scratch
    /// buffer than the number of points actually being drawn.
    /// </summary>
    public void SetPoints(Vector3[] points, int count)
    {
        if (_line == null || points == null || count < 2)
            return;

        if (_line.positionCount != count)
            _line.positionCount = count;
        _line.SetPositions(points);
    }

    /// <summary>
    /// Lays out a circle of <paramref name="segmentCount"/> points around <paramref name="center"/>, in
    /// the plane perpendicular to <paramref name="normal"/>, into <paramref name="buffer"/>. Returns the
    /// number of points written, or 0 if the inputs are degenerate.
    ///
    /// Used with <c>loop: true</c>, so the closing segment is drawn by the LineRenderer itself and the
    /// first point must not be repeated at the end.
    /// </summary>
    public static int BuildCircle(Vector3[] buffer, int segmentCount, Vector3 center, Vector3 normal, float radius)
    {
        if (buffer == null || segmentCount < 3 || buffer.Length < segmentCount || radius <= 0f)
            return 0;

        var axis = normal.normalized;
        if (axis.sqrMagnitude < 0.5f)
            return 0;

        // Any vector not parallel to the normal works as a seed for the in-plane basis; pick the world
        // axis the normal is least aligned with so the cross product never degenerates.
        var seed = Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right;
        var tangent = Vector3.Cross(axis, seed).normalized;
        var bitangent = Vector3.Cross(axis, tangent);

        for (int i = 0; i < segmentCount; i++)
        {
            float angle = (float)i / segmentCount * Mathf.PI * 2f;
            buffer[i] = center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius;
        }
        return segmentCount;
    }

    public void Destroy()
    {
        // Runtime-created materials aren't collected with their renderer.
        if (_material != null) Object.Destroy(_material);
        if (_root != null) Object.Destroy(_root);
    }
}
}
