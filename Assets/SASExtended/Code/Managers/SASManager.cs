using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Models;
using UnityEngine;

namespace SASExtended.Managers
{

public class SASManager : MonoBehaviour
{
    private SASManager() { }

    public static SASManager Instance { get; set; }

    //public double X = 90, Y = 90, Z = 90;
    public double X = 0, Y = 0, Z = 0;
    public bool XEnabled = true, YEnabled = true, ZEnabled = true;
    public AttitudeMode AttitudeMode = AttitudeMode.None;

    public double RefreshInterval = 0;
    public double RefreshInterval_short = 0.02;
    public double RefreshInterval_mid = 0.05;
    public double RefreshInterval_long = 0.08;
    public double AngleToRotation_small = 10;
    public double AngleToRotation_large = 30;

    // --- Hover control ---------------------------------------------------------------------------
    // In Hover mode the manager both points the vessel (thrust axis up, tilted against horizontal
    // velocity) AND drives the throttle. The throttle value is read back by the Harmony patch on
    // FlightInputHandler (see Patches/FlightInputHandlerThrottlePatch) each FixedUpdate.
    public float HoverThrottle;                 // 0..1, commanded throttle while hovering
    public double HoverAltitudeGain = 0.5;      // target vertical speed (m/s) per metre of altitude error
    public double HoverMaxClimbRate = 20;       // m/s cap on commanded climb
    public double HoverMaxDescentRate = 20;     // m/s cap on commanded descent
    public double HoverThrottleKp = 0.05;       // immediate throttle per m/s of vertical-speed error
    public double HoverThrottleKi = 0.10;       // throttle trim per m/s of vertical-speed error per second
    public double HoverHorizontalGain = 0.5;    // tilt tangent per m/s of horizontal speed
    public double HoverMaxTilt = 30;            // deg cap on the tilt used to kill horizontal velocity

    private double _hoverTargetAltitude;
    private double _throttleIntegral;
    private double _hoverCosTilt = 1.0;
    private double _lastHoverLogTime;

    private static readonly ReduxLib.Logging.ILogger _LOGGER = ReduxLib.ReduxLib.GetLogger("SASExtended|SASManager");
    private static double _UT => GameManager.Instance.Game?.UniverseModel?.UniverseTime ?? 0;
    private double _lastRefreshTime = 0;

    private VesselComponent _vessel => GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
    // The Redux Assembly-CSharp isn't publicized, so we reach the telemetry through the public
    // SimulationObject.Telemetry accessor instead of the private VesselComponent._telemetryComponent field.
    private TelemetryComponent _telemetry => _vessel.SimulationObject.Telemetry;
    private Rotation _rotation;
    // Attitude held for AttitudeMode.KillRot - captured once at engage time (see SetSASKillrot) and
    // then just held, mirroring the game's own vanilla StabilityAssist ("SAS off, kill rotation, hold
    // whatever attitude you're currently at" - not pointed at any particular direction like north).
    private Rotation _killRotTarget;

    private void Start()
    {
        Instance = this;
    }

    private void Update()
    {
        if (AttitudeMode == AttitudeMode.None || _vessel == null)
            return;

        if (_UT - _lastRefreshTime > RefreshInterval /*DebugUI.Instance.RefreshInterval*/)
        {
            SetRotation();
            _vessel.Autopilot.SAS.LockRotation(_rotation);
            _lastRefreshTime = _UT;

            SetRefreshInterval();
        }

        // Hover drives the throttle too, and needs a fresh command every frame (independent of the
        // adaptive rotation-refresh interval) so vertical-speed control stays smooth.
        if (AttitudeMode == AttitudeMode.Hover)
            UpdateHoverThrottle();
    }

    public void SetRotation()
    {
        //double x, y, z;
        //if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
        //    return;

        // RefreshAutopilotTelemetry() is the game's own "autopilot consumers call this before
        // reading telemetry" entry point (as opposed to the display-only per-frame OnUpdate cycle).
        // Without it we could read a frame-stale horizon/orbit-movement/target/maneuver snapshot.
        _telemetry.RefreshAutopilotTelemetry();

        // All the telemetry direction vectors below come from different KSP2 ITransformFrame /
        // coordinateSystem instances (e.g. OrbitMovementNormal/RadialIn/RadialOut are literally the
        // constant local axes (0,1,0)/(-1,0,0)/(1,0,0) of orbitMovementTransformInternal — meaningless
        // until expressed in a common frame). Mixing their raw ".vector" components directly (as the
        // heading/pitch math below does via Vector3d.Dot/SignedAngle/ProjectOnPlane) is only valid once
        // every vector is reframed into the SAME coordinateSystem. Rotation.LookRotation(forward, up)
        // does this reframing internally for its own two arguments, which is why the base transform and
        // Prograde/Retrograde (whose source frame happens to sit close to the horizon frame) look mostly
        // right — but Normal/AntiNormal/RadialIn/RadialOut come from a frame that's drastically rotated
        // relative to the horizon frame, so the un-reframed math was comparing directions expressed in
        // unrelated bases. Fix: reframe every vector into HorizonNorth's coordinateSystem up front.
        var north = _telemetry.HorizonNorth;
        var referenceFrame = north.coordinateSystem;
        var west = _telemetry.HorizonWest;
        var east = _telemetry.HorizonEast;
        var south = _telemetry.HorizonSouth;

        var orbitPrograde = Vector.Reframed(Vector.normalize(_telemetry.OrbitalMovementVelocity), referenceFrame);
        var orbitRetrograde = Vector.negate(orbitPrograde);
        var orbitNormal = Vector.Reframed(_telemetry.OrbitMovementNormal, referenceFrame);
        var orbitAntiNormal = Vector.negate(orbitNormal);
        var orbitRadialIn = Vector.Reframed(_telemetry.OrbitMovementRadialIn, referenceFrame);
        var orbitRadialOut = Vector.Reframed(_telemetry.OrbitMovementRadialOut, referenceFrame);

        var surfacePrograde = Vector.Reframed(Vector.normalize(_telemetry.SurfaceMovementPrograde), referenceFrame);
        var surfaceRetrograde = Vector.negate(surfacePrograde);
        var target = Vector.Reframed(Vector.normalize(_telemetry.TargetDirection), referenceFrame);
        var antiTarget = Vector.negate(target);

        // Vessel's velocity relative to the target (MechJeb's RELATIVE_VELOCITY) - confirmed via
        // decompile that TelemetryComponent computes TargetPrograde as exactly
        // normalize(OrbitalMovementVelocity - targetOrbitalVelocity), i.e. relative velocity, not
        // orbital prograde around the target.
        var targetRelativePrograde = Vector.Reframed(_telemetry.TargetPrograde, referenceFrame);
        var targetRelativeRetrograde = Vector.negate(targetRelativePrograde);

        var maneuver = Vector.Reframed(Vector.normalize(_telemetry.ManeuverDirection), referenceFrame);

        var sunBody = GetParentStar(_vessel);
        var sun = Vector.Reframed(Vector.normalize(Position.Delta(sunBody.Position, _telemetry.RootPosition)), referenceFrame);
        var antiSun = Vector.negate(sun);

        var upwards = Vector.Reframed(Vector.normalize(Position.Delta(_telemetry.RootPosition, _telemetry.SOIPosition)), referenceFrame);

        // Horizontal component of surface velocity (vertical component projected out via the same
        // dot/minus pattern Hover already uses for its tilt calc - Vector.dot/minus reframe their
        // second argument into the first's coordinateSystem automatically, so mixing the unreframed
        // telemetry vector with the already-reframed "upwards" here is safe).
        var horizontalVelocity = Vector.Reframed(
            Vector.normalize(Vector.minus(_telemetry.SurfaceMovementVelocity, Vector.scale(upwards, Vector.dot(_telemetry.SurfaceMovementVelocity, upwards)))),
            referenceFrame);
        var antiHorizontalVelocity = Vector.negate(horizontalVelocity);

        // Values actually commanded this tick - populated per-branch below (a disabled H/P/R control
        // doesn't just force its value to 0; see BuildPointingRotation/GetCurrentOffsetAngles).
        double effX, effY, effZ;

        switch (AttitudeMode)
        {
            // The previous heading/pitch derivation (SignedAngle vs. north + Asin vs. upwards, then
            // reconstructing via a chain of local AngleAxis rotations) was only exact when the target
            // was near the horizon frame's "upwards" axis (its geometry degenerates into a gimbal lock
            // there, which incidentally hid the bug for Radial+/-) - off by up to ~90 degrees in
            // general (confirmed numerically). BuildPointingRotation uses LookRotation instead, which
            // directly aligns local "up" (the vessel's nose axis - see Euler(90,0,0)) with the target
            // with zero error for any target/upwards pair.
            case AttitudeMode.OrbitPrograde:
                _rotation = BuildPointingRotation(orbitPrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.OrbitRetrograde:
                _rotation = BuildPointingRotation(orbitRetrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.OrbitNormal:
                _rotation = BuildPointingRotation(orbitNormal, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.OrbitAntiNormal:
                _rotation = BuildPointingRotation(orbitAntiNormal, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.OrbitRadialIn:
                _rotation = BuildPointingRotation(orbitRadialIn, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.OrbitRadialOut:
                _rotation = BuildPointingRotation(orbitRadialOut, upwards, out effX, out effY, out effZ);
                break;


            case AttitudeMode.SurfaceSurf:
                _rotation = BuildPointingRotation(north, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SurfaceSvelPlus:
                _rotation = BuildPointingRotation(surfacePrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SurfaceSvelMinus:
                _rotation = BuildPointingRotation(surfaceRetrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SurfaceHvelPlus:
                _rotation = BuildPointingRotation(horizontalVelocity, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SurfaceHvelMinus:
                _rotation = BuildPointingRotation(antiHorizontalVelocity, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetPlus:
                _rotation = BuildPointingRotation(target, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetMinus:
                _rotation = BuildPointingRotation(antiTarget, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetRvelPlus:
                _rotation = BuildPointingRotation(targetRelativePrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetRvelMinus:
                _rotation = BuildPointingRotation(targetRelativeRetrograde, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetParPlus:
                _rotation = BuildTargetOrientationRotation(reversed: false, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.TargetParMinus:
                _rotation = BuildTargetOrientationRotation(reversed: true, upwards, out effX, out effY, out effZ);
                break;
            // Star pointing now runs through the same LookRotation-based BuildPointingRotation as
            // every other mode (the earlier "doesn't work" note predates that rewrite - see the
            // Offset math section of mod_specifics.md); wired to the UI but not yet re-verified
            // in-game.
            case AttitudeMode.SpecialStarPlus:
                _rotation = BuildPointingRotation(sun, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SpecialStarMinus:
                _rotation = BuildPointingRotation(antiSun, upwards, out effX, out effY, out effZ);
                break;
            case AttitudeMode.SurfaceUp:
                // Target is "upwards" itself, so it can't also be used as LookRotation's up-hint
                // (degenerate/parallel) - use "north" as the hint instead, same remap pattern as
                // every other case.
                _rotation = BuildPointingRotation(upwards, north, out effX, out effY, out effZ);
                break;

            case AttitudeMode.Hover:
            {
                // Desired thrust direction: local "up" (away from the planet), tilted against the
                // horizontal surface-velocity component so that thrust bleeds off horizontal motion.
                var up = upwards;
                var surfaceVelocity = _telemetry.SurfaceMovementVelocity;
                var horizontal = Vector.minus(surfaceVelocity, Vector.scale(up, Vector.dot(surfaceVelocity, up)));
                var horizontalSpeed = horizontal.magnitude;

                var desired = up;
                double rawTiltTangent = HoverHorizontalGain * horizontalSpeed;
                double tiltTangentCap = Math.Tan(HoverMaxTilt * Math.PI / 180.0);
                double tiltTangent = 0.0;
                if (horizontalSpeed > 0.1)
                {
                    tiltTangent = Math.Min(rawTiltTangent, tiltTangentCap);
                    desired = Vector.normalize(Vector.minus(up, Vector.scale(Vector.normalize(horizontal), tiltTangent)));
                }
                _hoverCosTilt = 1.0 / Math.Sqrt(1.0 + tiltTangent * tiltTangent);

                // Point the vessel's nose along the desired thrust vector directly via LookRotation
                // (see the comment on AttitudeMode.OrbitPrograde). Heading/pitch aren't user-configurable
                // in Hover, only roll - which gets the same free-when-disabled treatment as elsewhere.
                var look = Rotation.LookRotation(desired, upwards);
                effX = 0;
                effY = 0;
                effZ = ZEnabled ? Z : GetCurrentOffsetAngles(look).roll;
                _rotation = look;
                _rotation.localRotation = look.localRotation * QuaternionD.Euler(0, 0, effZ) * QuaternionD.Euler(90, 0, 0);

                // Horizontal-cancellation is a pure-proportional controller (tilt angle scales
                // directly with current horizontal speed, no integral/derivative term) - if it's
                // struggling to null horizontal speed, the likely culprits are (a) tilt saturating at
                // HoverMaxTilt (not enough authority to correct faster) or (b) the attitude only being
                // recomputed on the adaptive RefreshInterval, so it's chasing a horizontal-velocity
                // vector that's already changed direction by the time SAS catches up. This line exposes
                // both: whether the raw (uncapped) tilt demand exceeds the cap, and (via the trailing
                // angleToTarget in the log line below) how far actual attitude lags the commanded one.
                _LOGGER.LogDebug(
                    $"[Hover/attitude] horizontalSpeed={horizontalSpeed:F2}m/s horizontal={FormatVector(horizontal)} " +
                    $"rawTiltTangent={rawTiltTangent:F3} cappedTiltTangent={tiltTangent:F3}[{(rawTiltTangent > tiltTangentCap ? "SATURATED" : "ok")}] " +
                    $"tiltDeg={Math.Atan(tiltTangent) * 180.0 / Math.PI:F1} desired={FormatVector(desired)} hoverCosTilt={_hoverCosTilt:F3} " +
                    $"refreshInterval={RefreshInterval:F3}s");

                break;
            }

            case AttitudeMode.KillRot:
                // Not pointed at anything - just hold the attitude captured when KillRot was engaged
                // (see SetSASKillrot), so the autopilot damps existing angular velocity down to a stop
                // instead of steering the vessel anywhere (equivalent to vanilla StabilityAssist).
                _rotation = _killRotTarget;
                effX = 0;
                effY = 0;
                effZ = 0;
                break;

            case AttitudeMode.Maneuver:
                if (_telemetry.HasManeuver)
                {
                    _rotation = BuildPointingRotation(maneuver, upwards, out effX, out effY, out effZ);
                }
                else
                {
                    // No maneuver node planned - ManeuverDirection is a degenerate zero vector in that
                    // case, so there's nothing sensible to point at. Hold current attitude instead
                    // (same fallback as KillRot) rather than steering toward a meaningless direction.
                    _rotation = _vessel.ControlTransform.Rotation;
                    effX = 0;
                    effY = 0;
                    effZ = 0;
                }
                break;

            // TEMP - debugging
            case AttitudeMode.Horizon:
                _rotation = BuildPointingRotation(north, upwards, out effX, out effY, out effZ); // maybe we can simplify this
                break;

            default: // horizon
                _rotation = BuildPointingRotation(north, upwards, out effX, out effY, out effZ);
                break;
        }

        var angleToTarget = GetAngleToRotation();
        _LOGGER.LogDebug(
            $"[SetRotation] mode={AttitudeMode} offsets(H={effX:F1}[{XEnabled}],P={effY:F1}[{YEnabled}],R={effZ:F1}[{ZEnabled}]) angleToTarget={angleToTarget:F2}deg | " +
            $"prograde={FormatVector(orbitPrograde)} retrograde={FormatVector(orbitRetrograde)} " +
            $"normal={FormatVector(orbitNormal)} antiNormal={FormatVector(orbitAntiNormal)} " +
            $"radialIn={FormatVector(orbitRadialIn)} radialOut={FormatVector(orbitRadialOut)} " +
            $"upwards={FormatVector(upwards)} north={FormatVector(north)} " +
            $"horizontalVelocity={FormatVector(horizontalVelocity)} targetRelativePrograde={FormatVector(targetRelativePrograde)}");
    }

    private static string FormatVector(Vector v) => $"({v.vector.x:F3},{v.vector.y:F3},{v.vector.z:F3})";

    // Builds the commanded attitude for a "point the nose at `target`" mode: LookRotation aligns local
    // up (the nose - see the trailing Euler(90,0,0)) with `target` exactly, then the H/P/R offsets are
    // applied on top. A disabled H/P/R control isn't just forced to 0 - see GetCurrentOffsetAngles.
    private Rotation BuildPointingRotation(Vector target, Vector upHint, out double appliedX, out double appliedY, out double appliedZ)
    {
        var look = Rotation.LookRotation(target, upHint);

        appliedX = XEnabled ? X : 0;
        appliedY = YEnabled ? Y : 0;
        appliedZ = ZEnabled ? Z : 0;
        if (!XEnabled || !YEnabled || !ZEnabled)
        {
            var current = GetCurrentOffsetAngles(look);
            if (!XEnabled) appliedX = current.heading;
            if (!YEnabled) appliedY = current.pitch;
            if (!ZEnabled) appliedZ = current.roll;
        }

        var rotation = look;
        rotation.localRotation = look.localRotation * QuaternionD.Euler(-appliedY, appliedX, appliedZ) * QuaternionD.Euler(90, 0, 0);
        return rotation;
    }

    // PAR+/PAR- (Target Parallel) align with - or against - the target's own facing (nose) direction,
    // unlike TargetPlus/Minus which point at the target's *position*. TelemetryComponent.TargetFrame
    // is a live "targetTransformInternal.bodyFrame" accessor that throws a NullReferenceException when
    // no target is selected (unlike TargetDirection etc., which are auto-properties that just hold a
    // stale/zero value) - so this must be guarded and can't be computed unconditionally up front like
    // the other direction vectors. Falls back to holding current attitude when there's no target, same
    // as Maneuver/KillRot.
    //
    // The target's "nose" is TargetFrame.up, not .forward: every vessel-attached frame in this engine
    // treats the long/up axis as the facing direction, not Unity's usual Z-forward - GetAngleToRotation
    // below reads the SAME vessel's current facing via "_vessel.MOI.coordinateSystem.up", and
    // BuildPointingRotation's trailing Euler(90,0,0) exists specifically to remap LookRotation's
    // Z-forward result onto that up axis. Using .forward/.back here (an earlier version of this method)
    // reads a different axis of the same orthonormal frame - 90 degrees off, which is exactly the
    // horizon-ish/off-to-the-side error observed in-game instead of pointing along the target's nose.
    private Rotation BuildTargetOrientationRotation(bool reversed, Vector upHint, out double appliedX, out double appliedY, out double appliedZ)
    {
        if (!_telemetry.HasTargetObject)
        {
            appliedX = 0;
            appliedY = 0;
            appliedZ = 0;
            return _vessel.ControlTransform.Rotation;
        }

        var targetFacing = Vector.Reframed(reversed ? _telemetry.TargetFrame.down : _telemetry.TargetFrame.up, upHint.coordinateSystem);
        return BuildPointingRotation(targetFacing, upHint, out appliedX, out appliedY, out appliedZ);
    }

    // A disabled H/P/R control should let that axis drift freely instead of being pinned to a fixed
    // value - e.g. disabling Roll should let the vessel roll however it naturally wants to while still
    // pointing at the target, not just zero the roll offset (which would still lock it to one specific
    // roll). We get that "free" behavior without touching the SAS PID loop itself by feeding back
    // whatever the vessel's ACTUAL current angle already is on that axis: the commanded value then
    // matches reality, the autopilot's error on that axis is ~zero, and it applies ~no torque there.
    // Solve `currentRotation = look * offset * Euler(90,0,0)` for `offset`, then decompose it with the
    // same convention used to build it (Euler(-pitch, heading, roll)) via Unity's own
    // Quaternion.eulerAngles, which is defined to invert Quaternion.Euler exactly.
    private (double heading, double pitch, double roll) GetCurrentOffsetAngles(Rotation look)
    {
        var currentRotation = Rotation.Reframed(_vessel.ControlTransform.Rotation, look.coordinateSystem);
        var offset = QuaternionD.Inverse(look.localRotation) * currentRotation.localRotation * QuaternionD.Inverse(QuaternionD.Euler(90, 0, 0));
        var euler = ((Quaternion)offset).eulerAngles;
        return (NormalizeAngle(euler.y), NormalizeAngle(-euler.x), NormalizeAngle(euler.z));
    }

    private void SetMode(AttitudeMode mode)
    {
        _LOGGER.LogInfo($"SAS mode -> {mode} (was {AttitudeMode})");
        AttitudeMode = mode;
        _vessel.Autopilot.SetActive(true);
    }

    public void SetSASOff()
    {
        _LOGGER.LogInfo($"SAS mode -> {AttitudeMode.None} (was {AttitudeMode})");
        AttitudeMode = AttitudeMode.None;
        _vessel.Autopilot.SetActive(false);
    }

    public void SetSASKillrot()
    {
        // Capture whatever attitude the vessel is at right now - KillRot holds this, it doesn't
        // steer toward any particular direction (see the comment on AttitudeMode.KillRot).
        _killRotTarget = _vessel.ControlTransform.Rotation;
        SetMode(AttitudeMode.KillRot);
    }

    public void SetSASManeuver() => SetMode(AttitudeMode.Maneuver);

    public void SetOrbitPrograde() => SetMode(AttitudeMode.OrbitPrograde);
    public void SetOrbitRetrograde() => SetMode(AttitudeMode.OrbitRetrograde);
    public void SetOrbitNormal() => SetMode(AttitudeMode.OrbitNormal);
    public void SetOrbitAntiNormal() => SetMode(AttitudeMode.OrbitAntiNormal);
    public void SetOrbitRadialIn() => SetMode(AttitudeMode.OrbitRadialIn);
    public void SetOrbitRadialOut() => SetMode(AttitudeMode.OrbitRadialOut);

    public void SetSurfaceSvelPlus() => SetMode(AttitudeMode.SurfaceSvelPlus);
    public void SetSurfaceSvelMinus() => SetMode(AttitudeMode.SurfaceSvelMinus);
    public void SetSurfaceSurf() => SetMode(AttitudeMode.SurfaceSurf);
    public void SetSurfaceHvelPlus() => SetMode(AttitudeMode.SurfaceHvelPlus);
    public void SetSurfaceHvelMinus() => SetMode(AttitudeMode.SurfaceHvelMinus);
    public void SetSurfaceUp() => SetMode(AttitudeMode.SurfaceUp);

    public void SetTargetPlus() => SetMode(AttitudeMode.TargetPlus);
    public void SetTargetRvelPlus() => SetMode(AttitudeMode.TargetRvelPlus);
    public void SetTargetParPlus() => SetMode(AttitudeMode.TargetParPlus);
    public void SetTargetMinus() => SetMode(AttitudeMode.TargetMinus);
    public void SetTargetRvelMinus() => SetMode(AttitudeMode.TargetRvelMinus);
    public void SetTargetParMinus() => SetMode(AttitudeMode.TargetParMinus);

    public void SetSpecialStarPlus() => SetMode(AttitudeMode.SpecialStarPlus);
    public void SetSpecialStarMinus() => SetMode(AttitudeMode.SpecialStarMinus);

    /// <summary>
    /// Engages hover: SAS holds the vessel thrust-up (tilting to null horizontal velocity) while the
    /// throttle controller cancels vertical velocity and holds the engagement altitude.
    /// </summary>
    public void SetHover()
    {
        // Hold whatever altitude we engaged at, and seed the throttle integrator with the current
        // throttle so engaging hover doesn't jolt the engines.
        _hoverTargetAltitude = _vessel.AltitudeFromSurface;
        _throttleIntegral = _vessel.flightCtrlState.mainThrottle;
        _hoverCosTilt = 1.0;
        HoverThrottle = _vessel.flightCtrlState.mainThrottle;

        SetMode(AttitudeMode.Hover);
        _LOGGER.LogInfo($"Hover engage altitude={_hoverTargetAltitude:F1}m throttle={HoverThrottle:F2}");
    }

    /// <summary>
    /// Vertical-velocity / altitude-hold throttle controller. A proportional term gives an immediate
    /// response and an integral term self-tunes to the vessel's hover throttle regardless of TWR, so
    /// no per-vessel thrust/mass model is needed. Tilt (used to kill horizontal velocity) is divided
    /// out so the vertical thrust component stays on target while the vessel leans.
    /// </summary>
    private void UpdateHoverThrottle()
    {
        double dt = Time.deltaTime;
        if (dt <= 0.0)
            return;

        double altitudeError = _hoverTargetAltitude - _vessel.AltitudeFromSurface;
        double targetVerticalSpeed = Clamp(altitudeError * HoverAltitudeGain, -HoverMaxDescentRate, HoverMaxClimbRate);
        double verticalSpeedError = targetVerticalSpeed - _vessel.VerticalSrfSpeed;

        _throttleIntegral = Clamp(_throttleIntegral + verticalSpeedError * HoverThrottleKi * dt, 0.0, 1.0);
        double throttle = _throttleIntegral + verticalSpeedError * HoverThrottleKp;

        if (_hoverCosTilt > 0.1)
            throttle /= _hoverCosTilt;

        HoverThrottle = (float)Clamp(throttle, 0.0, 1.0);

        // Runs every frame, so log on its own slower timer (independent of the attitude
        // RefreshInterval) to avoid flooding the log while still catching throttle saturation
        // (pinned at 0 or 1 - meaning no authority left to also correct horizontal speed via tilt)
        // or a vertical-speed error that never settles.
        if (_UT - _lastHoverLogTime > 0.25)
        {
            _lastHoverLogTime = _UT;
            _LOGGER.LogDebug(
                $"[Hover/throttle] altitude={_vessel.AltitudeFromSurface:F1}m target={_hoverTargetAltitude:F1}m altErr={altitudeError:F2}m " +
                $"targetVSpeed={targetVerticalSpeed:F2}m/s actualVSpeed={_vessel.VerticalSrfSpeed:F2}m/s vSpeedErr={verticalSpeedError:F2}m/s " +
                $"throttleIntegral={_throttleIntegral:F3} throttlePreClamp={throttle:F3}[{(throttle <= 0.0 || throttle >= 1.0 ? "SATURATED" : "ok")}] " +
                $"HoverThrottle={HoverThrottle:F3} hoverCosTilt={_hoverCosTilt:F3}");
        }
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : (value > max ? max : value);

    private CelestialBodyComponent GetParentStar(VesselComponent vessel)
    {
        var body = vessel.mainBody;

        try
        {
            while (!body.IsStar)
            {
                body = body.referenceBody;
            }
        }
        catch (Exception ex)
        {
            _LOGGER.LogError($"Unable to fetch parent star for vessel {vessel.Name}. How is this possible?! Exception: {ex.Message}");
        }

        return body;
    }

    private double GetAngleToRotation()
    {
        // _rotation.coordinateSystem is always our shared "referenceFrame" (see SetRotation), which is
        // generally NOT the same frame as _vessel.transform.coordinateSystem - comparing their raw
        // vectors directly (as the previous code did) is the same coordinate-mixing bug fixed elsewhere
        // in this file. Reframe the vessel's current "nose" direction into _rotation's frame first.
        var currentAttitude = Vector.Reframed(_vessel.MOI.coordinateSystem.up, _rotation.coordinateSystem);
        var currentRotation = _rotation.localRotation * Vector3d.up;
        return Vector3d.Angle(currentAttitude.vector, currentRotation);
    }

    private void SetRefreshInterval()
    {
        var angleToRotation = GetAngleToRotation();
        if (angleToRotation > AngleToRotation_large)
            RefreshInterval = RefreshInterval_short;
        else if (angleToRotation > AngleToRotation_small)
            RefreshInterval = RefreshInterval_mid;
        else
            RefreshInterval = RefreshInterval_long;
    }

    private Vector3 _dif;

    public static Vector3 GetEulerAngleDifference(Vector fromDirection, Vector toDirection)
    {
        // Normalize to unit vectors (directions)
        fromDirection.vector = fromDirection.vector.normalized;
        toDirection.vector = toDirection.vector.normalized;

        // Convert to Unity float vectors for rotation math
        Vector3 fromUnity = new Vector3((float)fromDirection.vector.x, (float)fromDirection.vector.y, (float)fromDirection.vector.z);
        Vector3 toUnity = new Vector3((float)toDirection.vector.x, (float)toDirection.vector.y, (float)toDirection.vector.z);

        // Create rotations (LookRotation equivalent: forward along vector, up is world Y)
        Quaternion fromRot = Quaternion.LookRotation(fromUnity, Vector3.up);
        Quaternion toRot = Quaternion.LookRotation(toUnity, Vector3.up);

        // Relative rotation: to * inverse(from) = shortest rotation from 'from' to 'to'
        Quaternion diffRot = toRot * Quaternion.Inverse(fromRot);

        // Extract Euler angles (degrees)
        Vector3 euler = diffRot.eulerAngles;

        // Normalize to -180 to +180 for intuitive differences
        return new Vector3(
            NormalizeAngle(euler.x),
            NormalizeAngle(euler.y),
            NormalizeAngle(euler.z)
        );
    }

    // Helper: Wrap 0-360 to -180 to +180
    private static float NormalizeAngle(float angle)
    {
        if (angle > 180f)
            angle -= 360f;

        if (angle < -180f)
            angle += 360f;

        return angle;
    }

    // TEMP - for debugging
    public void SetHorizon() => SetMode(AttitudeMode.Horizon);
}
}
