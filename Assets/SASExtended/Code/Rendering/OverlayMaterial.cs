using UnityEngine;
using UnityEngine.Rendering;

namespace SASExtended.Rendering
{

/// <summary>
/// The one material recipe every in-world visual in this mod uses: unlit, coloured, and drawn on top of
/// scene geometry rather than occluded by it.
///
/// Deliberately built from a stock Unity shader rather than anything from the game: KSP2 Redux is
/// removing the Shapes library (it was a significant source of frame-time cost), which takes the
/// <c>DebugShapes*</c> components and the immediate-mode <c>Shapes.Draw</c> API with it. Everything we
/// draw therefore has to stand on plain Unity rendering.
///
/// <para><b>Why "Hidden/Internal-Colored" and not "Sprites/Default".</b> These are debug-style markers
/// that are useless when buried inside the vessel or terrain they describe, so they have to defeat
/// depth testing. That needs a shader which actually exposes <c>_ZTest</c> as a material property -
/// <c>Sprites/Default</c> does not, so setting <c>_ZTest</c> on it is silently a no-op, depth testing
/// stays <c>LEqual</c>, and the visual is occluded no matter what render queue it is in. That is
/// exactly how the CoM marker ended up invisible inside the vessel. <c>Hidden/Internal-Colored</c> is
/// Unity's built-in shader for immediate-mode/debug drawing and declares <c>_ZTest</c>, <c>_ZWrite</c>,
/// <c>_Cull</c> and the blend factors, so the settings below actually take effect.</para>
/// </summary>
internal static class OverlayMaterial
{
    /// <summary>
    /// Creates an always-on-top unlit material of the given color. The caller owns the returned
    /// material and must <c>Destroy</c> it (materials created at runtime are not garbage collected
    /// with their renderer).
    /// </summary>
    public static Material Create(Color color)
    {
        // Ordered by capability, not preference: the fallbacks still draw, they just can't switch off
        // depth testing, so a visual would be occluded rather than missing entirely.
        var shader = Shader.Find("Hidden/Internal-Colored")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color");

        var material = new Material(shader)
        {
            color = color,
            // Drawn after everything opaque and transparent. On its own this only fixes ordering, not
            // occlusion - _ZTest below is what actually lets it show through geometry.
            renderQueue = (int)RenderQueue.Overlay,
        };

        // Set defensively: on a fallback shader these properties don't exist, and assigning a missing
        // property is a silent no-op that would leave a confusing "it should be on top" impression.
        SetIntIfPresent(material, "_ZTest", (int)CompareFunction.Always); // ignore the depth buffer
        SetIntIfPresent(material, "_ZWrite", 0);                          // ...and don't write to it
        SetIntIfPresent(material, "_Cull", (int)CullMode.Off);            // visible from inside too
        SetIntIfPresent(material, "_SrcBlend", (int)BlendMode.SrcAlpha);
        SetIntIfPresent(material, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);

        return material;
    }

    private static void SetIntIfPresent(Material material, string property, int value)
    {
        if (material.HasProperty(property))
            material.SetInt(property, value);
    }
}
}
