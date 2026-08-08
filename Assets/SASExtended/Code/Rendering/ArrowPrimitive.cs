using UnityEngine;

namespace SASExtended.Rendering
{

/// <summary>
/// A single world-space arrow drawn with two <see cref="LineRenderer"/>s - a constant-width shaft and a
/// head that tapers to a point. Replaces the game's <c>DebugShapesArrowComponent</c> (Shapes
/// <c>Line</c> + <c>Cone</c>), which is going away with the Shapes library.
///
/// Why two LineRenderers rather than one with a stepped width curve: a LineRenderer's width curve is
/// sampled along the *normalized* length of the line, so a shaft/head step needs two keys at the same
/// normalized position, which is fragile and re-tunes itself every time the arrow's length changes.
/// Two renderers keep the shaft and the head independently and predictably shaped, and the extra draw
/// call is irrelevant at the ~11 arrows this mod can show at once. (It is still far cheaper than what
/// it replaces: Shapes re-issued immediate-mode draws per camera per frame; these are persistent
/// meshes that only move.)
///
/// LineRenderers are view-aligned billboards by default, so an arrow stays readable from any angle
/// instead of vanishing when seen edge-on - the same behaviour the stock debug arrows had.
///
/// Not a MonoBehaviour: the owner (<c>FlightAxesVisualizer</c>) already runs a per-frame loop and
/// drives every visual from it, which keeps all the sim-space -> world-space conversion in one place.
/// </summary>
internal sealed class ArrowPrimitive
{
    // Proportions, as fractions of the arrow's total length, so an arrow reads the same at any size.
    private const float ShaftWidthFraction = 0.030f;
    private const float HeadLengthFraction = 0.180f;
    private const float HeadWidthFraction = 0.090f;

    private readonly GameObject _root;
    private readonly LineRenderer _shaft;
    private readonly LineRenderer _head;
    private readonly Material _shaftMaterial;
    private readonly Material _headMaterial;

    private readonly Vector3[] _shaftPoints = new Vector3[2];
    private readonly Vector3[] _headPoints = new Vector3[2];

    public ArrowPrimitive(string name, Transform parent, Color color)
    {
        _root = new GameObject(name);
        _root.transform.SetParent(parent, worldPositionStays: false);

        _shaftMaterial = OverlayMaterial.Create(color);
        _headMaterial = OverlayMaterial.Create(color);

        _shaft = CreateLine("shaft", _root.transform, _shaftMaterial);
        _head = CreateLine("head", _root.transform, _headMaterial);
    }

    public bool IsAlive => _root != null;

    public bool ActiveSelf => _root != null && _root.activeSelf;

    public void SetActive(bool active)
    {
        if (_root != null && _root.activeSelf != active)
            _root.SetActive(active);
    }

    public void SetColor(Color color)
    {
        if (_shaftMaterial != null) _shaftMaterial.color = color;
        if (_headMaterial != null) _headMaterial.color = color;
    }

    /// <summary>
    /// Positions the arrow in world space. <paramref name="direction"/> need not be normalized; a
    /// zero-length direction leaves the previous geometry untouched (normalizing it would point the
    /// arrow in an arbitrary direction).
    /// </summary>
    public void SetFromTo(Vector3 origin, Vector3 direction, float length)
    {
        if (_root == null)
            return;

        float magnitude = direction.magnitude;
        if (magnitude < 1e-6f || length <= 0f)
            return;

        var unit = direction / magnitude;
        float headLength = length * HeadLengthFraction;
        var shaftEnd = origin + unit * (length - headLength);
        var tip = origin + unit * length;

        _shaftPoints[0] = origin;
        _shaftPoints[1] = shaftEnd;
        _shaft.widthMultiplier = length * ShaftWidthFraction;
        _shaft.SetPositions(_shaftPoints);

        _headPoints[0] = shaftEnd;
        _headPoints[1] = tip;
        // The head's width curve tapers full width -> 0, which renders the billboarded triangle that
        // reads as a cone. widthMultiplier scales the curve, so the curve itself stays 1 -> 0.
        _head.widthMultiplier = length * HeadWidthFraction;
        _head.SetPositions(_headPoints);
    }

    public void Destroy()
    {
        // Runtime-created materials are not collected with their renderer, so they are destroyed
        // explicitly rather than leaked once per arrow per vessel rebuild.
        if (_shaftMaterial != null) Object.Destroy(_shaftMaterial);
        if (_headMaterial != null) Object.Destroy(_headMaterial);
        if (_root != null) Object.Destroy(_root);
    }

    private static LineRenderer CreateLine(string name, Transform parent, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);

        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.numCapVertices = 0;
        line.numCornerVertices = 0;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        line.material = material;

        // Both renderers taper the same way; the shaft simply overrides this with a flat curve so it
        // keeps a constant width along its length.
        line.widthCurve = name == "head"
            ? AnimationCurve.Linear(0f, 1f, 1f, 0f)
            : AnimationCurve.Constant(0f, 1f, 1f);

        return line;
    }
}
}
