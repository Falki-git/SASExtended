using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Rendering;
using UnityEngine;

namespace SASExtended.Managers
{

/// <summary>
/// Draws optional in-world flight-axis visuals for the active vessel, toggled from the SAS Extended
/// window's settings panel. See <c>.claude/mod_specifics.md</c> and the Flight Axes design notes.
///
/// <para><b>Rendering.</b> These visuals originally reused the game's own <c>DebugShapes*</c>
/// components and prefabs (the same ones the stock Vessel Tools debug window uses), so they matched the
/// stock look for free. KSP2 Redux is removing the Shapes library those are built on - it was a
/// significant frame-time cost - so everything here is now drawn with this mod's own
/// <see cref="SASExtended.Rendering"/> primitives, built on plain Unity <c>LineRenderer</c>s and a mesh
/// sphere. Nothing in this class depends on the game's rendering any more: no debug prefabs are loaded,
/// so no visual can be blocked by an addressable that fails to resolve.</para>
///
/// The visuals, each an independent toggle (element name in parentheses):
/// <list type="bullet">
///   <item><b>Control axes</b> - the vessel's CURRENT orientation, split into three separately
///   toggleable arrows drawn from the control point (navball-aligned): forward=blue
///   (<c>show-control-forward</c>), nose/up=white (<c>show-control-up</c>), right=red
///   (<c>show-control-right</c>). Three independent arrows sharing the control transform's rotation -
///   see <see cref="UpdateControlAxes"/> for which local axis each one follows.</item>
///   <item><b>Commanded attitude</b> (<c>show-commanded-attitude</c>) - orange arrow along the nose
///   direction SAS Extended is steering toward right now (slew-limited
///   <see cref="SASManager.CommandedRotation"/>).</item>
///   <item><b>Target attitude</b> (<c>show-target-attitude</c>) - violet arrow along the mode's raw
///   final target (<see cref="SASManager.TargetRotation"/>), before slew limiting.</item>
///   <item><b>Orbital</b> - prograde=green (<c>show-orbital-prograde</c>), normal=magenta
///   (<c>show-orbital-normal</c>), radial-in=cyan (<c>show-orbital-radial-in</c>) arrows from the
///   CoM.</item>
///   <item><b>Surface velocity</b>=gray (<c>show-surface-velocity</c>) and <b>horizontal velocity</b>
///   =brown (<c>show-horizontal-velocity</c>) arrows from the CoM.</item>
///   <item><b>CoM marker</b> (<c>show-com-marker</c>) - yellow sphere at the center of mass.</item>
/// </list>
///
/// Only the active vessel is visualized. Instances are rebuilt whenever the active vessel changes
/// (switch/undock/revert) or flight is left/re-entered - each tick's active vessel is compared against
/// <see cref="_builtVessel"/>. The on/off flags persist across those rebuilds, so a visual left on
/// before a vessel switch comes back on the new vessel automatically.
/// </summary>
public class FlightAxesVisualizer : MonoBehaviour
{
    private static readonly ReduxLib.Logging.ILogger _LOGGER =
        ReduxLib.ReduxLib.GetLogger("SASExtended|FlightAxesVisualizer");

    public static FlightAxesVisualizer Instance { get; private set; }

    // Toggle element ids (SideToggleControl names) for every visual, in a stable order.
    // MainWindowController iterates this to wire each toggle to SetVisual, so adding a visual here (plus
    // an entry in _arrowSpecs / the control-axes or CoM handling in SetVisual) is all that's needed.
    public const string ControlForwardId = "show-control-forward";
    public const string ControlUpId = "show-control-up";
    public const string ControlRightId = "show-control-right";
    public const string CommandedId = "show-commanded-attitude";
    public const string TargetId = "show-target-attitude";
    public const string OrbitalProgradeId = "show-orbital-prograde";
    public const string OrbitalNormalId = "show-orbital-normal";
    public const string OrbitalRadialInId = "show-orbital-radial-in";
    public const string SurfaceVelocityId = "show-surface-velocity";
    public const string HorizontalVelocityId = "show-horizontal-velocity";
    public const string CoMMarkerId = "show-com-marker";

    public static readonly string[] ToggleIds =
    {
        ControlForwardId, ControlUpId, ControlRightId,
        CommandedId, TargetId,
        OrbitalProgradeId, OrbitalNormalId, OrbitalRadialInId,
        SurfaceVelocityId, HorizontalVelocityId,
        CoMMarkerId,
    };

    // Velocity arrows hide below this speed (m/s): a near-zero velocity normalizes to an essentially
    // random direction, so pointing an arrow along it is just noise (most visible on a landed/at-rest
    // vessel, where gear-spring jitter dominates). Kept low so the arrow stays useful when nulling out
    // velocity for a precise landing; it's really just a noise floor. Tunable.
    private const float MinVelocityArrowSpeed = 0.1f;

    // The navball convention every ArrowSpec below is written against: an arrow points along the fed
    // Rotation's "up" (+Y) axis, so a full vessel-attitude Rotation points along the nose and any
    // direction D is drawn via Rotation.LookRotation(hint, D) - i.e. LookRotation's SECOND argument is
    // what gets drawn. The stock path achieved this with a Euler(-90,0,0) RotationOffset on the
    // tracker; UpdateArrow now applies it directly. See the load-bearing comment there.

    // --- Control axes (three independent arrows sharing the control transform) ---------------------
    private enum ControlAxis { Forward = 0, Up = 1, Right = 2 }

    private const float ControlAxisLength = 2f;

    // Indexed by ControlAxis. White nose, because green is taken by the orbital prograde arrow.
    private static readonly Color[] _controlAxisColors = { Color.royalBlue, Color.white, Color.red };
    private static readonly string[] _controlAxisNames = { "SASX_ControlFwd", "SASX_ControlUp", "SASX_ControlRight" };

    private bool _controlForward, _controlUp, _controlRight;
    private readonly ArrowPrimitive[] _controlAxes = new ArrowPrimitive[3];

    // --- Generic direction/attitude arrows --------------------------------------------------------
    // Static config per arrow id. Kept out of the live-instance dictionary so it survives everything.
    private sealed class ArrowSpec
    {
        public Color Color;
        public float Length;
        public bool RequiresEngaged;   // commanded/target only make sense while a mode is engaged
        public bool AnchorAtCoM;       // else anchored at the control point
        public string ObjectName;   // GameObject name for the primitive (also handy in the hierarchy)
        public Func<VesselComponent, TelemetryComponent, Rotation> Rotation;
        // Optional per-frame visibility gate (e.g. hide a velocity arrow at ~0 speed where its
        // direction is just noise). Null = always visible (subject only to RequiresEngaged).
        public Func<VesselComponent, TelemetryComponent, bool> Visible;
    }

    // One live arrow instance. Since ArrowPrimitive is driven directly from this class's Update loop
    // (rather than by a DebugShapesObjectTracker firing a per-instance callback), there is no
    // subscription to keep track of for unsubscribing - just the primitive and its spec.
    private sealed class ArrowInstance
    {
        public ArrowPrimitive Primitive;
        public ArrowSpec Spec;
    }

    private static readonly Dictionary<string, ArrowSpec> _arrowSpecs = BuildArrowSpecs();

    // Desired on/off state (survives vessel changes / flight exits) and the live instances for
    // _builtVessel. Kept separate so a toggle left on rebuilds against a new vessel.
    private readonly HashSet<string> _enabledArrows = new HashSet<string>();
    private readonly Dictionary<string, ArrowInstance> _liveArrows = new Dictionary<string, ArrowInstance>();

    // --- CoM marker -------------------------------------------------------------------------------
    private const float CoMMarkerRadius = 0.5f; // DebugShapesSphereMarker's own default

    private bool _showCoMMarker;
    private SphereMarkerPrimitive _comMarker;

    // The vessel the current instances were built for (null = none). Compared each tick against the
    // live active vessel so a switch/undock/revert/flight-exit rebuilds against the new one.
    private VesselComponent _builtVessel;

    private static VesselComponent ActiveVessel =>
        GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

    private static IPhysicsSpaceProvider PhysicsSpace =>
        GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;

    private static bool Engaged => SASManager.Instance != null && SASManager.Instance.IsEngaged;

    private static Dictionary<string, ArrowSpec> BuildArrowSpecs()
    {
        return new Dictionary<string, ArrowSpec>
        {
            // Commanded/target: feed the full vessel-attitude Rotation straight in (the navball offset
            // makes the arrow follow its nose). Gated on an engaged mode - their source holds a stale
            // value otherwise.
            [CommandedId] = new ArrowSpec
            {
                Color = new Color(1f, 0.5f, 0f), Length = 3f, RequiresEngaged = true, ObjectName = "SASX_Commanded",
                Rotation = (v, tel) => SASManager.Instance.CommandedRotation,
            },
            [TargetId] = new ArrowSpec
            {
                Color = new Color(0.6f, 0.2f, 0.9f), Length = 4f, RequiresEngaged = true, ObjectName = "SASX_Target",
                Rotation = (v, tel) => SASManager.Instance.TargetRotation,
            },

            // Orbital frame directions (mirrors the stock debug tool's LookRotation choices - the second
            // arg is the pointing direction, the first just a non-parallel hint).
            [OrbitalProgradeId] = new ArrowSpec
            {
                Color = Color.green, Length = 3f, AnchorAtCoM = true, ObjectName = "SASX_OrbPro",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementNormal, tel.OrbitMovementPrograde),
            },
            [OrbitalNormalId] = new ArrowSpec
            {
                Color = Color.magenta, Length = 3f, AnchorAtCoM = true, ObjectName = "SASX_OrbNrm",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementRetrograde, tel.OrbitMovementNormal),
            },
            [OrbitalRadialInId] = new ArrowSpec
            {
                Color = Color.cyan, Length = 3f, AnchorAtCoM = true, ObjectName = "SASX_OrbRad",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementPrograde, tel.OrbitMovementRadialIn),
            },

            // Surface velocity (SurfaceMovementPrograde is the normalized surface velocity direction).
            [SurfaceVelocityId] = new ArrowSpec
            {
                Color = new Color(0.6f, 0.6f, 0.6f), Length = 2.5f, AnchorAtCoM = true, ObjectName = "SASX_SrfVel",
                // The arrow follows LookRotation's "up" arg only when the "forward" arg is PERPENDICULAR
                // to it (LookRotation sets +Z=forward exactly, +Y=up-projected-perpendicular). The
                // orbital normal is not perpendicular to the surface-velocity direction in general, so
                // using it as the hint skewed the arrow well off true (worst when moving horizontally
                // with ~0 vertical speed). Build a forward that IS perpendicular to the velocity via a
                // cross with HorizonUp, falling back to HorizonNorth when velocity is ~vertical (and so
                // parallel to up, making that first cross degenerate).
                Rotation = (v, tel) =>
                {
                    var vel = Vector.normalize(tel.SurfaceMovementVelocity);
                    var hint = Vector.cross(vel, tel.HorizonUp);
                    if (hint.magnitude < 0.01)
                        hint = Vector.cross(vel, tel.HorizonNorth);
                    return Rotation.LookRotation(hint, vel);
                },
                Visible = (v, tel) => tel.SurfaceMovementVelocity.magnitude >= MinVelocityArrowSpeed,
            },
            // Horizontal component of surface velocity (surface velocity with its vertical/radial part
            // projected out). Vector.dot/minus/scale reframe their arguments into a common frame.
            [HorizontalVelocityId] = new ArrowSpec
            {
                Color = new Color(0.5f, 0.28f, 0.1f), Length = 2.5f, AnchorAtCoM = true, ObjectName = "SASX_HrzVel",
                Rotation = (v, tel) =>
                {
                    var up = tel.HorizonUp;
                    var vel = tel.SurfaceMovementVelocity;
                    var horizontal = Vector.normalize(Vector.minus(vel, Vector.scale(up, Vector.dot(vel, up))));
                    return Rotation.LookRotation(up, horizontal);
                },
                // Gate on the HORIZONTAL speed specifically - a vessel descending straight down has real
                // surface speed but ~no horizontal component, so its direction here is still noise.
                Visible = (v, tel) =>
                {
                    var up = tel.HorizonUp;
                    var vel = tel.SurfaceMovementVelocity;
                    return Vector.minus(vel, Vector.scale(up, Vector.dot(vel, up))).magnitude >= MinVelocityArrowSpeed;
                },
            },
        };
    }

    private void Awake()
    {
        Instance = this;
        // No asset loading any more: every visual is drawn by this mod's own Rendering primitives, so
        // nothing here waits on the game's debug prefabs (which are going away with Shapes) and
        // nothing can be blocked by an addressable that fails to resolve.
    }

    /// <summary>
    /// Turns one visual on or off, dispatched by toggle element id (see <see cref="ToggleIds"/>).
    /// Called by MainWindowController from each settings-panel toggle.
    /// </summary>
    public void SetVisual(string id, bool on)
    {
        switch (id)
        {
            case ControlForwardId: _controlForward = on; ApplyControlAxes(); break;
            case ControlUpId: _controlUp = on; ApplyControlAxes(); break;
            case ControlRightId: _controlRight = on; ApplyControlAxes(); break;

            case CoMMarkerId:
                _showCoMMarker = on;
                if (on) CreateCoMMarker();
                else DestroyCoMMarker();
                break;

            default:
                if (!_arrowSpecs.ContainsKey(id))
                {
                    _LOGGER.LogWarning($"SetVisual: unknown visual id '{id}'.");
                    return;
                }
                if (on)
                {
                    _enabledArrows.Add(id);
                    CreateArrow(id);
                }
                else
                {
                    _enabledArrows.Remove(id);
                    DestroyArrow(id);
                }
                break;
        }
    }

    private void Update()
    {
        var vessel = ActiveVessel;

        // Active vessel changed (switch/undock/revert, or flight exit -> null): the old instances are
        // parented here and tracking a vessel that may be torn down, so drop them and rebuild whichever
        // visuals are still enabled against the new vessel.
        if (vessel != _builtVessel)
        {
            DestroyAllInstances();
            _builtVessel = vessel;
            RebuildForCurrentVessel();
        }

        if (_builtVessel == null)
            return;

        // Every visual is driven from here now - nothing self-updates any more. All of them re-derive
        // their world position from the vessel's LIVE position each frame rather than caching a
        // converted one, because the game continuously re-anchors its floating origin.
        if (_comMarker != null)
        {
            var physics = PhysicsSpace;
            if (physics != null)
                _comMarker.SetCenter((Vector3)physics.PositionToPhysics(_builtVessel.CenterOfMass));
        }

        UpdateControlAxes();

        // Position/orient every arrow and maintain its visibility (engaged gate for commanded/target,
        // speed gate for the velocity arrows). Hidden arrows are deactivated rather than destroyed, so
        // the condition reversing brings them straight back.
        UpdateArrows();
    }

    private void RebuildForCurrentVessel()
    {
        if (_builtVessel == null)
            return;
        ApplyControlAxes();
        foreach (var id in _enabledArrows)
            CreateArrow(id);
        if (_showCoMMarker)
            CreateCoMMarker();
    }

    #region Control axes (current orientation - split into per-child toggles)

    // The three control arrows are now three independent ArrowPrimitives sharing one source rotation
    // (the vessel's control transform), rather than one stock axes gizmo whose child arrows were
    // toggled. Each is created when its own toggle turns on and destroyed when it turns off.
    private void ApplyControlAxes()
    {
        ApplyControlAxis(ControlAxis.Forward, _controlForward);
        ApplyControlAxis(ControlAxis.Up, _controlUp);
        ApplyControlAxis(ControlAxis.Right, _controlRight);
    }

    private void ApplyControlAxis(ControlAxis axis, bool on)
    {
        int i = (int)axis;
        if (!on)
        {
            _controlAxes[i]?.Destroy();
            _controlAxes[i] = null;
            return;
        }

        if (_controlAxes[i] != null || _builtVessel?.SimulationObject == null)
            return;

        _controlAxes[i] = new ArrowPrimitive(_controlAxisNames[i], transform, _controlAxisColors[i]);
        UpdateControlAxes();
    }

    // Positions the control arrows along the vessel's current control-transform axes.
    //
    // The three local axes each arrow follows are the ONE thing here that could not be derived from the
    // game assembly: DebugShapesAxesComponent only sets its children's colour and length, and their
    // orientations lived in the DebugAxes prefab's transform hierarchy. What is certain is the "up"
    // arrow, which the whole navball convention is built around - feeding a full vessel-attitude
    // Rotation to an arrow makes it point along the nose, i.e. along the rotation's +Y (see the
    // load-bearing comment in UpdateArrow). Forward and right are the orthonormal completion of that.
    private void UpdateControlAxes()
    {
        if (_controlAxes[0] == null && _controlAxes[1] == null && _controlAxes[2] == null)
            return;

        var physics = PhysicsSpace;
        var control = _builtVessel?.ControlTransform;
        if (physics == null || control == null)
            return;

        var origin = (Vector3)physics.PositionToPhysics(control.Position);
        Quaternion rotation = physics.RotationToPhysics(control.Rotation);

        // Forward and right are NEGATED; up is not. Confirmed in flight - with the un-negated basis the
        // white/up arrow was already correct while the other two pointed exactly backwards, which is
        // the signature of a 180-degree rotation about the up axis (it maps forward -> -forward and
        // right -> -right and leaves up alone). In other words the gizmo's basis is the control
        // rotation turned 180 degrees about its own nose axis. This is the piece that could not be
        // derived from the assembly - DebugShapesAxesComponent only sets its children's colour and
        // length, and their orientations lived in the DebugAxes prefab's transform hierarchy.
        SetControlAxis(ControlAxis.Forward, origin, rotation * Vector3.back);
        SetControlAxis(ControlAxis.Up, origin, rotation * Vector3.up);
        SetControlAxis(ControlAxis.Right, origin, rotation * Vector3.left);
    }

    private void SetControlAxis(ControlAxis axis, Vector3 origin, Vector3 direction)
    {
        var arrow = _controlAxes[(int)axis];
        if (arrow == null || !arrow.IsAlive)
            return;
        arrow.SetFromTo(origin, direction, ControlAxisLength);
    }

    private void DestroyControlAxes()
    {
        for (int i = 0; i < _controlAxes.Length; i++)
        {
            _controlAxes[i]?.Destroy();
            _controlAxes[i] = null;
        }
    }

    #endregion

    #region Generic arrows (commanded/target/orbital/velocity)

    private void CreateArrow(string id)
    {
        // SimulationObject, not just the vessel - see CreateControlAxes for why.
        if (_liveArrows.ContainsKey(id) || _builtVessel?.SimulationObject == null)
            return;
        if (!_arrowSpecs.TryGetValue(id, out var spec))
            return;

        var instance = new ArrowInstance
        {
            Primitive = new ArrowPrimitive(spec.ObjectName, transform, spec.Color),
            Spec = spec,
        };
        _liveArrows[id] = instance;

        // Drive it once immediately rather than waiting for the next Update: a freshly created
        // primitive has no geometry yet, so it would otherwise show as a one-frame artifact at the
        // origin the instant a toggle is switched on.
        UpdateArrow(instance, PhysicsSpace, _builtVessel.SimulationObject.Telemetry);
    }

    // Drives every live arrow's world position, direction and visibility. This is the work
    // DebugShapesObjectTracker used to do for us: ask the spec for a sim-space Rotation, convert
    // sim -> Unity world, and write it into the primitive.
    //
    // Recomputed from the vessel's LIVE position every frame and never cached across frames, because
    // the game continuously re-anchors its floating origin - caching a converted absolute position is
    // exactly the bug the landing-prediction visuals spent several rounds on (see
    // landing_prediction_fixes.md rounds 9/10).
    private void UpdateArrows()
    {
        if (_liveArrows.Count == 0)
            return;

        var physics = PhysicsSpace;
        var telemetry = _builtVessel?.SimulationObject?.Telemetry;
        foreach (var instance in _liveArrows.Values)
            UpdateArrow(instance, physics, telemetry);
    }

    private void UpdateArrow(ArrowInstance instance, IPhysicsSpaceProvider physics, TelemetryComponent telemetry)
    {
        if (instance?.Primitive == null || !instance.Primitive.IsAlive)
            return;

        // Visibility is evaluated here, from the always-running Update loop, rather than inside the
        // arrow's own update - a hidden arrow that gated itself could never decide to re-show.
        bool visible = ComputeArrowVisible(instance.Spec, _builtVessel);
        instance.Primitive.SetActive(visible);
        if (!visible || physics == null || telemetry == null)
            return;

        var spec = instance.Spec;
        var anchor = spec.AnchorAtCoM ? _builtVessel.CenterOfMass : _builtVessel.ControlTransform.Position;
        var origin = (Vector3)physics.PositionToPhysics(anchor);

        // THE LOAD-BEARING LINE: an arrow points along its Rotation's UP (+Y) axis, not its forward.
        // That was the stock debug arrows' convention too - the tracker applied a Euler(-90,0,0)
        // RotationOffset, which maps the arrow prefab's local +Z onto the fed rotation's +Y - and every
        // ArrowSpec above is written for it, which is why they all build
        // Rotation.LookRotation(hint, direction) and expect the SECOND argument to be what gets drawn.
        // Swapping this to `* Vector3.forward` silently points all seven arrows somewhere else.
        Quaternion worldRotation = physics.RotationToPhysics(spec.Rotation(_builtVessel, telemetry));
        instance.Primitive.SetFromTo(origin, worldRotation * Vector3.up, spec.Length);
    }

    // Whether an arrow should currently be shown: engaged-gated arrows (commanded/target) need a mode
    // engaged; velocity arrows need enough speed for their direction to be meaningful (see each spec's
    // Visible predicate). Evaluated from the always-running Update loop, never from an arrow's own
    // handler - a hidden GameObject stops updating itself and could otherwise never re-show.
    private static bool ComputeArrowVisible(ArrowSpec spec, VesselComponent vessel)
    {
        if (spec.RequiresEngaged && !Engaged)
            return false;
        if (spec.Visible == null)
            return true;
        var telemetry = vessel?.SimulationObject?.Telemetry;
        if (telemetry == null)
            return true; // telemetry not ready yet - don't hide on a transient
        return spec.Visible(vessel, telemetry);
    }

    private void DestroyArrow(string id)
    {
        if (!_liveArrows.TryGetValue(id, out var inst))
            return;
        inst.Primitive?.Destroy();
        _liveArrows.Remove(id);
    }

    #endregion

    #region CoM marker

    private void CreateCoMMarker()
    {
        if (_comMarker != null || _builtVessel == null)
            return;

        // Radius/colour match DebugShapesSphereMarker's own defaults, so the marker keeps the size and
        // colour it had on the stock path.
        _comMarker = new SphereMarkerPrimitive("SASX_CoM", transform, Color.yellow, CoMMarkerRadius);
        // Position is driven each frame from Update.
    }

    private void DestroyCoMMarker()
    {
        _comMarker?.Destroy();
        _comMarker = null;
    }

    #endregion

    private void DestroyAllInstances()
    {
        DestroyControlAxes();
        foreach (var id in new List<string>(_liveArrows.Keys))
            DestroyArrow(id);
        DestroyCoMMarker();
    }

    private void OnDestroy()
    {
        DestroyAllInstances();
        if (Instance == this)
            Instance = null;
    }
}
}
