using System;
using System.Collections.Generic;
using DebugTools.Utils;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;

namespace SASExtended.Managers
{

/// <summary>
/// Draws optional in-world flight-axis visuals for the active vessel, toggled from the SAS Extended
/// window's settings panel. Reuses the game's own debug-shape components and prefabs (the same ones the
/// stock Vessel Tools debug window uses) so the visuals match the game's look for free - see
/// <c>.claude/mod_specifics.md</c> and the Flight Axes design notes.
///
/// The visuals, each an independent toggle (element name in parentheses):
/// <list type="bullet">
///   <item><b>Control axes</b> - the vessel's CURRENT orientation, split into three separately
///   toggleable arrows drawn from the control point (navball-aligned): forward=blue
///   (<c>show-control-forward</c>), nose/up=white (<c>show-control-up</c>), right=red
///   (<c>show-control-right</c>). Implemented by toggling the stock axes prefab's own child arrows, so
///   the per-axis directions are exactly the game's.</item>
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

    // The stock debug-tool prefabs. DebugArrow = a single Shapes line+cone arrow; DebugAxes = three of
    // them as an RGB gizmo. Loaded once at startup via the game's asset provider (async callback), the
    // same keys the stock VesselToolsWindowController loads.
    private const string AxesPrefabKey = "Assets/Modules/DebugTools/Assets/DebugAxes.prefab";
    private const string ArrowPrefabKey = "Assets/Modules/DebugTools/Assets/DebugArrow.prefab";

    // Velocity arrows hide below this speed (m/s): a near-zero velocity normalizes to an essentially
    // random direction, so pointing an arrow along it is just noise (most visible on a landed/at-rest
    // vessel, where gear-spring jitter dominates). Kept low so the arrow stays useful when nulling out
    // velocity for a precise landing; it's really just a noise floor. Tunable.
    private const float MinVelocityArrowSpeed = 0.1f;

    // Navball-aligned orientation offset - the same value the stock debug tools apply so the gizmo
    // reads like the navball (nose along the green/up arrow). Applied as the tracker's RotationOffset;
    // because the tracker renders transform.rotation = physicsRotation * RotationOffset, feeding a
    // Rotation whose "up" axis is direction D makes the arrow prefab's local +Z point along D. That's
    // why every direction arrow below just builds Rotation.LookRotation(hint, D) - the arrow follows
    // LookRotation's second ("up") argument.
    private static readonly Quaternion _navballRotation = Quaternion.Euler(-90f, 0f, 0f);

    private DebugShapesAxesComponent _axesPrefab;
    private DebugShapesArrowComponent _arrowPrefab;

    // --- Control axes (one shared RGB gizmo, per-child toggles) -----------------------------------
    private bool _controlForward, _controlUp, _controlRight;
    private DebugShapesAxesComponent _controlAxes;

    // --- Generic direction/attitude arrows --------------------------------------------------------
    // Static config per arrow id. Kept out of the live-instance dictionary so it survives everything.
    private sealed class ArrowSpec
    {
        public Color Color;
        public float Length;
        public bool RequiresEngaged;   // commanded/target only make sense while a mode is engaged
        public bool AnchorAtCoM;       // else anchored at the control point
        public string TrackerSuffix;
        public Func<VesselComponent, TelemetryComponent, Rotation> Rotation;
        // Optional per-frame visibility gate (e.g. hide a velocity arrow at ~0 speed where its
        // direction is just noise). Null = always visible (subject only to RequiresEngaged).
        public Func<VesselComponent, TelemetryComponent, bool> Visible;
    }

    // One live arrow instance + the exact OnUpdate delegate it was subscribed with (needed to unsub).
    private sealed class ArrowInstance
    {
        public DebugShapesArrowComponent Component;
        public Action<ITransformModel, SimulationObjectModel> Handler;
        public ArrowSpec Spec;
    }

    private static readonly Dictionary<string, ArrowSpec> _arrowSpecs = BuildArrowSpecs();

    // Desired on/off state (survives vessel changes / flight exits) and the live instances for
    // _builtVessel. Kept separate so a toggle left on rebuilds against a new vessel.
    private readonly HashSet<string> _enabledArrows = new HashSet<string>();
    private readonly Dictionary<string, ArrowInstance> _liveArrows = new Dictionary<string, ArrowInstance>();

    // --- CoM marker -------------------------------------------------------------------------------
    private bool _showCoMMarker;
    private DebugShapesSphereMarker _comMarker;

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
                Color = new Color(1f, 0.5f, 0f), Length = 3f, RequiresEngaged = true, TrackerSuffix = "SASX_Commanded",
                Rotation = (v, tel) => SASManager.Instance.CommandedRotation,
            },
            [TargetId] = new ArrowSpec
            {
                Color = new Color(0.6f, 0.2f, 0.9f), Length = 4f, RequiresEngaged = true, TrackerSuffix = "SASX_Target",
                Rotation = (v, tel) => SASManager.Instance.TargetRotation,
            },

            // Orbital frame directions (mirrors the stock debug tool's LookRotation choices - the second
            // arg is the pointing direction, the first just a non-parallel hint).
            [OrbitalProgradeId] = new ArrowSpec
            {
                Color = Color.green, Length = 3f, AnchorAtCoM = true, TrackerSuffix = "SASX_OrbPro",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementNormal, tel.OrbitMovementPrograde),
            },
            [OrbitalNormalId] = new ArrowSpec
            {
                Color = Color.magenta, Length = 3f, AnchorAtCoM = true, TrackerSuffix = "SASX_OrbNrm",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementRetrograde, tel.OrbitMovementNormal),
            },
            [OrbitalRadialInId] = new ArrowSpec
            {
                Color = Color.cyan, Length = 3f, AnchorAtCoM = true, TrackerSuffix = "SASX_OrbRad",
                Rotation = (v, tel) => Rotation.LookRotation(tel.OrbitMovementPrograde, tel.OrbitMovementRadialIn),
            },

            // Surface velocity (SurfaceMovementPrograde is the normalized surface velocity direction).
            [SurfaceVelocityId] = new ArrowSpec
            {
                Color = new Color(0.6f, 0.6f, 0.6f), Length = 2.5f, AnchorAtCoM = true, TrackerSuffix = "SASX_SrfVel",
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
                Color = new Color(0.5f, 0.28f, 0.1f), Length = 2.5f, AnchorAtCoM = true, TrackerSuffix = "SASX_HrzVel",
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
        LoadPrefabs();
    }

    private void LoadPrefabs()
    {
        // Async - the callbacks may fire after the player has already toggled a visual on, so each
        // rebuilds the current vessel's visuals once its prefab is available (Create* no-op when their
        // prefab is still null). The CoM marker needs no prefab, so it's never blocked on this.
        GameManager.Instance.Assets.Load<GameObject>(AxesPrefabKey, obj =>
        {
            _axesPrefab = obj.GetComponent<DebugShapesAxesComponent>();
            RebuildForCurrentVessel();
        }, logMissingKey: true);
        GameManager.Instance.Assets.Load<GameObject>(ArrowPrefabKey, obj =>
        {
            _arrowPrefab = obj.GetComponent<DebugShapesArrowComponent>();
            RebuildForCurrentVessel();
        }, logMissingKey: true);
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

        // The sphere marker has no tracker of its own (unlike the arrow/axes prefabs, which self-update
        // each frame), so drive its position here from the live center of mass.
        if (_comMarker != null)
        {
            var physics = PhysicsSpace;
            if (physics != null)
                _comMarker.SetCenter(physics.PositionToPhysics(_builtVessel.CenterOfMass));
        }

        // Maintain each arrow's visibility (engaged gate for commanded/target, speed gate for the
        // velocity arrows) by activating/deactivating the GameObject - without destroying it, so
        // toggling the condition back doesn't require rebuilding. Done here rather than in the arrows'
        // own handlers because a deactivated GameObject stops updating itself.
        foreach (var inst in _liveArrows.Values)
        {
            if (inst.Component == null)
                continue;
            bool visible = ComputeArrowVisible(inst.Spec, _builtVessel);
            if (inst.Component.gameObject.activeSelf != visible)
                inst.Component.gameObject.SetActive(visible);
        }
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

    // The three control arrows share one stock axes gizmo instance; each toggle just enables/disables
    // the prefab's matching child arrow (forward/up/right), which keeps their directions exactly the
    // game's. The gizmo is created when the first of the three turns on and destroyed when all are off.
    private void ApplyControlAxes()
    {
        bool anyOn = _controlForward || _controlUp || _controlRight;
        if (!anyOn)
        {
            DestroyControlAxes();
            return;
        }

        if (_controlAxes == null)
            CreateControlAxes();
        if (_controlAxes == null)
            return; // prefab not loaded yet / no vessel - RebuildForCurrentVessel will retry

        SetChildActive(_controlAxes.forward, _controlForward);
        SetChildActive(_controlAxes.up, _controlUp);
        SetChildActive(_controlAxes.right, _controlRight);
    }

    private static void SetChildActive(DebugShapesArrowComponent child, bool active)
    {
        if (child == null || child.gameObject == null)
            return;
        if (child.gameObject.activeSelf != active)
            child.gameObject.SetActive(active);
    }

    private void CreateControlAxes()
    {
        if (_controlAxes != null || _axesPrefab == null || _builtVessel == null)
            return;

        var axes = Instantiate(_axesPrefab.gameObject, transform).GetComponent<DebugShapesAxesComponent>();
        axes.arrowLineLength = 2f;
        axes.forwardColor = Color.royalBlue;
        axes.upColor = Color.white; // white nose (green is taken by orbital prograde)
        axes.rightColor = Color.red;

        var tracker = axes.GetComponent<DebugShapesObjectTracker>();
        if (tracker != null)
        {
            tracker.RotationOffset = _navballRotation;
            tracker.Setup(_builtVessel.SimulationObject, "SASX_Control", startTracking: true);
            tracker.OnUpdate += UpdateControlAxes;
        }
        _controlAxes = axes;
    }

    private static void UpdateControlAxes(ITransformModel t, SimulationObjectModel o)
    {
        var vessel = o?.Vessel;
        if (vessel == null)
            return;
        t.UpdatePosition(vessel.ControlTransform.Position);
        t.UpdateRotation(vessel.ControlTransform.Rotation);
    }

    private void DestroyControlAxes()
    {
        if (_controlAxes == null)
            return;
        var tracker = _controlAxes.GetComponent<DebugShapesObjectTracker>();
        if (tracker != null)
            tracker.OnUpdate -= UpdateControlAxes;
        if (_controlAxes.gameObject != null)
            Destroy(_controlAxes.gameObject);
        _controlAxes = null;
    }

    #endregion

    #region Generic arrows (commanded/target/orbital/velocity)

    private void CreateArrow(string id)
    {
        if (_liveArrows.ContainsKey(id) || _arrowPrefab == null || _builtVessel == null)
            return;
        if (!_arrowSpecs.TryGetValue(id, out var spec))
            return;

        var arrow = Instantiate(_arrowPrefab.gameObject, transform).GetComponent<DebugShapesArrowComponent>();
        arrow.color = spec.Color;
        arrow.lineLength = spec.Length;

        // Captures spec; stored on the instance so the exact same delegate can be unsubscribed later.
        Action<ITransformModel, SimulationObjectModel> handler = (t, o) =>
        {
            var vessel = o?.Vessel;
            if (vessel == null)
                return;
            if (spec.RequiresEngaged && !Engaged)
                return;
            var telemetry = o.Telemetry;
            if (telemetry == null)
                return;
            t.UpdatePosition(spec.AnchorAtCoM ? vessel.CenterOfMass : vessel.ControlTransform.Position);
            t.UpdateRotation(spec.Rotation(vessel, telemetry));
        };

        var tracker = arrow.GetComponent<DebugShapesObjectTracker>();
        if (tracker != null)
        {
            tracker.RotationOffset = _navballRotation;
            tracker.Setup(_builtVessel.SimulationObject, spec.TrackerSuffix, startTracking: true);
            tracker.OnUpdate += handler;
        }

        _liveArrows[id] = new ArrowInstance
        {
            Component = arrow,
            Handler = handler,
            Spec = spec,
        };

        // Start with the correct visibility so there's no one-frame flash of a stale/at-rest direction
        // the instant it's created (Update maintains it thereafter).
        arrow.gameObject.SetActive(ComputeArrowVisible(spec, _builtVessel));
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
        if (inst.Component != null)
        {
            var tracker = inst.Component.GetComponent<DebugShapesObjectTracker>();
            if (tracker != null)
                tracker.OnUpdate -= inst.Handler;
            if (inst.Component.gameObject != null)
                Destroy(inst.Component.gameObject);
        }
        _liveArrows.Remove(id);
    }

    #endregion

    #region CoM marker

    private void CreateCoMMarker()
    {
        if (_comMarker != null || _builtVessel == null)
            return;

        var go = new GameObject("SASX_CoM");
        go.transform.parent = transform;
        var marker = go.AddComponent<DebugShapesSphereMarker>();
        marker.SetEnabled(true);
        // Position is driven each frame in Update (the marker has no tracker of its own). Radius uses
        // DebugShapesSphereMarker's own default (0.5m, yellow), matching the stock CoM debug look.
        _comMarker = marker;
    }

    private void DestroyCoMMarker()
    {
        if (_comMarker == null)
            return;
        if (_comMarker.gameObject != null)
            Destroy(_comMarker.gameObject);
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
