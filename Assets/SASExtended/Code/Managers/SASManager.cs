using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Models;
using SASExtended.Utilities;
using UnityEngine;

namespace SASExtended.Managers
{

public class SASManager : MonoBehaviour
{
    private SASManager() { }

    public static SASManager Instance { get; set; }

    // Fired when SASManager disengages itself (vessel switch/undock/revert/scene-exit - see
    // DisengageForVesselChange) rather than the player clicking a toggle. MainWindowController
    // subscribes so the OFF button and Heading/Pitch/Roll fields don't keep showing a mode that
    // silently stopped being engaged underneath the UI.
    public event Action Disengaged;

    //public double X = 90, Y = 90, Z = 90;
    public double X = 0, Y = 0, Z = 0;
    public bool XEnabled = true, YEnabled = true, ZEnabled = true;
    public AttitudeMode AttitudeMode = AttitudeMode.None;
    public bool IsHoverActive => AttitudeMode == AttitudeMode.Hover;

    // Read by MainWindowController to grey out the NODE button / TGT-tab mode buttons
    // (enhancement_roadmap.md item 2) and by Update() below to auto-disengage if the node/target
    // disappears while its mode is active. _vessel is guarded first since _telemetry (a property,
    // not a field) NREs on a null _vessel.
    public bool HasManeuverNode => _vessel != null && _telemetry.HasManeuver;
    public bool HasTarget => _vessel != null && _telemetry.HasTargetObject;

    private static bool IsTargetMode(AttitudeMode mode) =>
        mode is AttitudeMode.TargetPlus or AttitudeMode.TargetMinus or
                AttitudeMode.TargetRvelPlus or AttitudeMode.TargetRvelMinus or
                AttitudeMode.TargetParPlus or AttitudeMode.TargetParMinus;

    public double RefreshInterval = 0;
    public double RefreshInterval_short = 0.02;
    public double RefreshInterval_mid = 0.05;
    public double RefreshInterval_long = 0.08;
    public double AngleToRotation_small = 10;
    public double AngleToRotation_large = 30;

    // --- Hover control ---------------------------------------------------------------------------
    // Hover both points the vessel (thrust axis up, tilted to null horizontal velocity) and drives
    // the throttle - the throttle value is read back by the Harmony patch on FlightInputHandler (see
    // Patches/FlightInputHandlerThrottlePatch) each FixedUpdate. The tilt/throttle coupling is
    // inspired by MechJeb2's Translatron + ThrustController (KEEP_VERTICAL + TransKillH).
    //
    // Holds HoverTargetVerticalSpeed directly, NOT the engagement altitude - whatever altitude the
    // vessel ends up at once vertical speed reaches target is where it hovers. Tilt floor
    // (HoverTiltAuthorityFloor) and max tilt angle (HoverTiltThrottleBudget) both self-tune off the
    // vessel's own hover-equilibrium throttle (_throttleIntegral) instead of flat per-vessel guesses,
    // so no per-vessel thrust/mass/TWR model is needed anywhere in this control law. Full round-by-
    // round debugging history (why each piece of this exists, what broke without it) is in
    // .claude/hover_mode_fixes.md - read it before changing this control law again.
    public float HoverThrottle;                    // 0..1, commanded throttle while hovering
    // m/s, signed target vertical speed. User-set via the hover-controls UI; deliberately NOT reset
    // on engage (SetHover) - it should carry over from the last time hover was used, per user request.
    public double HoverTargetVerticalSpeed;
    // Whether hover's tilt logic actively nulls horizontal surface velocity. User-set via the
    // hover-controls UI; deliberately NOT reset on engage (SetHover) - same persistence as
    // HoverTargetVerticalSpeed above. Off means point straight up regardless of horizontal drift.
    public bool CancelHorizontalVelocity = true;
    public double HoverThrottleKp = 0.05;          // immediate throttle per m/s of vertical-speed error
    public double HoverThrottleKi = 0.06;          // throttle trim per m/s of vertical-speed error per second
    public double HoverThrottleKd = 0.08;          // throttle damping per m/s^2 of vertical acceleration
    // Rate limit on the commanded throttle itself - a powerful engine turns even a brief throttle
    // burst into a large acceleration spike, which HoverThrottleKd then reacts to violently (a
    // classic bang-bang/relay oscillation). TWR-agnostic by construction: the commanded value can
    // only change this fast per second regardless of what the P/I/D formula asks for. See
    // hover_mode_fixes.md round 8 for the failure this fixes.
    public double HoverThrottleMaxRate = 2.0;      // max |Δthrottle|/second - 0->100% takes at least 0.5s
    // Low-pass time constant for thrustDrivenAccel before it's multiplied by HoverThrottleKd - a raw
    // single-frame finite-difference derivative is noisy even with the rate limiter above damping its
    // effect on the actual engine. See hover_mode_fixes.md round 9.
    public double HoverThrottleAccelFilterTime = 0.2; // seconds - higher smooths more but reacts slower to genuine trends
    public double HoverTiltAuthorityFloor = 10;    // m/s - nominal floor when hover throttle == HoverTiltReferenceThrottle
    public double HoverTiltReferenceThrottle = 0.2; // throttle fraction the nominal floor above assumes; floor scales down for more powerful engines and up for weaker ones
    public double HoverTiltAuthorityFloorMin = 2;  // m/s - safety floor so tilt can't go near-90 deg before the throttle integral has converged
    // Hard ceiling on the normal blend's tilt angle, self-tuned off _throttleIntegral (the same
    // self-tuned hover-equilibrium throttle the floor above uses) and HoverTiltThrottleBudget: the
    // max tilt angle is whatever angle would make holding HoverTargetVerticalSpeed cost exactly that
    // fraction of total throttle, leaving the rest as headroom. A flat angle is either unsafe on a
    // low-TWR vessel (not enough reserved vertical thrust) or overly conservative on a high-TWR one
    // (real spare thrust left unused) - self-tuning avoids guessing which. See hover_mode_fixes.md
    // rounds 5, 7, and 10 for the crashes/over-conservatism this fixes and why the budget is 0.9.
    public double HoverTiltThrottleBudget = 0.9;    // fraction of throttle the tilt cap will let holding vertical speed cost, leaving the rest as headroom
    public double HoverMaxTiltAngleFallback = 45;   // deg - used only before _throttleIntegral has any data yet (conservative, TWR unknown)

    private double _throttleIntegral;
    private bool _hoverThrottleIntegralKnown;
    private double _hoverCosTilt = 1.0;
    private double _lastVerticalSpeedForDerivative;
    private double _filteredThrustDrivenAccel;
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
    // The vessel that was active when the current AttitudeMode was engaged. _vessel (above) is a live
    // property that silently resolves to whatever vessel is active *now* - compared against this every
    // Update tick so a switch/undock/revert/scene-exit disengages instead of applying this vessel's
    // captured per-vessel state (_killRotTarget, the Hover throttle integrator/filters) to a different
    // vessel. See enhancement_roadmap.md item 4.
    private VesselComponent _engagedVessel;

    private void Start()
    {
        Instance = this;

        // Seed from the remembered value once at startup only - SetHover() deliberately never resets
        // this on re-engage (see the field comment above), so it must not be reloaded from config here
        // on every engage, just the one time the vessel/session starts.
        HoverTargetVerticalSpeed = Settings.HoverVerticalVelocity.Value;
    }

    private void Update()
    {
        if (AttitudeMode == AttitudeMode.None)
            return;

        // _vessel is a live lookup (GetActiveSimVessel()) - if it no longer matches the vessel we
        // engaged on, the active vessel was switched/undocked/reverted out from under us, or flight
        // was exited entirely (_vessel goes null). Disengage rather than silently applying this
        // vessel's captured state (KillRot's held attitude, Hover's throttle integrator) to whatever
        // vessel is active now. See enhancement_roadmap.md item 4.
        var vessel = _vessel;
        if (vessel == null || vessel != _engagedVessel)
        {
            DisengageForVesselChange(vessel);
            return;
        }

        // vessel.Autopilot (unlike _vessel itself) can legitimately be null for a beat after a vessel
        // becomes the active vessel - the game hasn't finished setting up its VesselAutopilot yet (its
        // own VesselComponent.AutopilotStatus/SetAutopilotMode/SetAutopilotEnableDisable all guard this
        // the same way). Skip this tick rather than NRE; _lastRefreshTime is deliberately left
        // untouched so the very next tick retries immediately once Autopilot is ready.
        if (vessel.Autopilot == null)
            return;

        // Maneuver/Target modes silently held current attitude when their reference disappeared
        // (see the fallback branches in SetRotation/BuildTargetOrientationRotation) - the button
        // stayed lit as if still tracking with no way to tell. Auto-disengage to OFF instead, same
        // as a vessel switch, so the UI honestly reflects that the mode stopped doing anything.
        // See enhancement_roadmap.md item 2.
        if (AttitudeMode == AttitudeMode.Maneuver && !_telemetry.HasManeuver)
        {
            DisengageForLostReference("Maneuver node was removed");
            return;
        }

        if (IsTargetMode(AttitudeMode) && !_telemetry.HasTargetObject)
        {
            DisengageForLostReference("Target was lost");
            return;
        }

        if (_UT - _lastRefreshTime > RefreshInterval /*DebugUI.Instance.RefreshInterval*/)
        {
            SetRotation();
            vessel.Autopilot.SAS.LockRotation(_rotation);
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
        //
        // Only "north" and "upwards" are hoisted here - every mode-specific direction vector below is
        // computed lazily inside its own switch case instead (AttitudeMode is single-valued, so only
        // one case's vector(s) are ever needed per tick; see enhancement_roadmap.md item 5). This cuts
        // per-tick Vector.Reframed calls from ~18 down to 1 (2 for negated +/- pairs).
        var north = _telemetry.HorizonNorth;
        var referenceFrame = north.coordinateSystem;
        var upwards = Vector.Reframed(Vector.normalize(Position.Delta(_telemetry.RootPosition, _telemetry.SOIPosition)), referenceFrame);

        // Values actually commanded this tick - populated per-branch below (a disabled H/P/R control
        // doesn't just force its value to 0; see BuildPointingRotation/GetCurrentOffsetAngles).
        double effX, effY, effZ;

        // Whatever direction the active mode actually pointed at this tick, purely for the trailing
        // debug log below - left null for modes that don't point anywhere (KillRot, Hover - which has
        // its own dedicated [Hover/attitude] log line - and the no-maneuver/no-target fallbacks).
        Vector? loggedTarget = null;

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
            {
                var orbitPrograde = Vector.Reframed(Vector.normalize(_telemetry.OrbitalMovementVelocity), referenceFrame);
                _rotation = BuildPointingRotation(orbitPrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitPrograde;
                break;
            }
            case AttitudeMode.OrbitRetrograde:
            {
                var orbitRetrograde = Vector.negate(Vector.Reframed(Vector.normalize(_telemetry.OrbitalMovementVelocity), referenceFrame));
                _rotation = BuildPointingRotation(orbitRetrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitRetrograde;
                break;
            }
            case AttitudeMode.OrbitNormal:
            {
                var orbitNormal = Vector.Reframed(_telemetry.OrbitMovementNormal, referenceFrame);
                _rotation = BuildPointingRotation(orbitNormal, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitNormal;
                break;
            }
            case AttitudeMode.OrbitAntiNormal:
            {
                var orbitAntiNormal = Vector.negate(Vector.Reframed(_telemetry.OrbitMovementNormal, referenceFrame));
                _rotation = BuildPointingRotation(orbitAntiNormal, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitAntiNormal;
                break;
            }
            case AttitudeMode.OrbitRadialIn:
            {
                var orbitRadialIn = Vector.Reframed(_telemetry.OrbitMovementRadialIn, referenceFrame);
                _rotation = BuildPointingRotation(orbitRadialIn, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitRadialIn;
                break;
            }
            case AttitudeMode.OrbitRadialOut:
            {
                var orbitRadialOut = Vector.Reframed(_telemetry.OrbitMovementRadialOut, referenceFrame);
                _rotation = BuildPointingRotation(orbitRadialOut, upwards, out effX, out effY, out effZ);
                loggedTarget = orbitRadialOut;
                break;
            }


            case AttitudeMode.SurfaceSurf:
                _rotation = BuildPointingRotation(north, upwards, out effX, out effY, out effZ);
                loggedTarget = north;
                break;
            case AttitudeMode.SurfaceSvelPlus:
            {
                var surfacePrograde = Vector.Reframed(Vector.normalize(_telemetry.SurfaceMovementPrograde), referenceFrame);
                _rotation = BuildPointingRotation(surfacePrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = surfacePrograde;
                break;
            }
            case AttitudeMode.SurfaceSvelMinus:
            {
                var surfaceRetrograde = Vector.negate(Vector.Reframed(Vector.normalize(_telemetry.SurfaceMovementPrograde), referenceFrame));
                _rotation = BuildPointingRotation(surfaceRetrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = surfaceRetrograde;
                break;
            }
            case AttitudeMode.SurfaceHvelPlus:
            {
                // Horizontal component of surface velocity (vertical component projected out via the
                // same dot/minus pattern Hover already uses for its tilt calc - Vector.dot/minus
                // reframe their second argument into the first's coordinateSystem automatically, so
                // mixing the unreframed telemetry vector with the already-reframed "upwards" here is
                // safe).
                var horizontalVelocity = Vector.Reframed(
                    Vector.normalize(Vector.minus(_telemetry.SurfaceMovementVelocity, Vector.scale(upwards, Vector.dot(_telemetry.SurfaceMovementVelocity, upwards)))),
                    referenceFrame);
                _rotation = BuildPointingRotation(horizontalVelocity, upwards, out effX, out effY, out effZ);
                loggedTarget = horizontalVelocity;
                break;
            }
            case AttitudeMode.SurfaceHvelMinus:
            {
                // Same horizontal-velocity projection as SurfaceHvelPlus (see comment there), negated.
                var antiHorizontalVelocity = Vector.negate(Vector.Reframed(
                    Vector.normalize(Vector.minus(_telemetry.SurfaceMovementVelocity, Vector.scale(upwards, Vector.dot(_telemetry.SurfaceMovementVelocity, upwards)))),
                    referenceFrame));
                _rotation = BuildPointingRotation(antiHorizontalVelocity, upwards, out effX, out effY, out effZ);
                loggedTarget = antiHorizontalVelocity;
                break;
            }
            case AttitudeMode.TargetPlus:
            {
                var target = Vector.Reframed(Vector.normalize(_telemetry.TargetDirection), referenceFrame);
                _rotation = BuildPointingRotation(target, upwards, out effX, out effY, out effZ);
                loggedTarget = target;
                break;
            }
            case AttitudeMode.TargetMinus:
            {
                var antiTarget = Vector.negate(Vector.Reframed(Vector.normalize(_telemetry.TargetDirection), referenceFrame));
                _rotation = BuildPointingRotation(antiTarget, upwards, out effX, out effY, out effZ);
                loggedTarget = antiTarget;
                break;
            }
            case AttitudeMode.TargetRvelPlus:
            {
                // Vessel's velocity relative to the target (MechJeb's RELATIVE_VELOCITY) - confirmed
                // via decompile that TelemetryComponent computes TargetPrograde as exactly
                // normalize(OrbitalMovementVelocity - targetOrbitalVelocity), i.e. relative velocity,
                // not orbital prograde around the target.
                var targetRelativePrograde = Vector.Reframed(_telemetry.TargetPrograde, referenceFrame);
                _rotation = BuildPointingRotation(targetRelativePrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = targetRelativePrograde;
                break;
            }
            case AttitudeMode.TargetRvelMinus:
            {
                // Negation of the target-relative velocity described above TargetRvelPlus.
                var targetRelativeRetrograde = Vector.negate(Vector.Reframed(_telemetry.TargetPrograde, referenceFrame));
                _rotation = BuildPointingRotation(targetRelativeRetrograde, upwards, out effX, out effY, out effZ);
                loggedTarget = targetRelativeRetrograde;
                break;
            }
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
            {
                var sunBody = GetParentStar(_vessel);
                var sun = Vector.Reframed(Vector.normalize(Position.Delta(sunBody.Position, _telemetry.RootPosition)), referenceFrame);
                _rotation = BuildPointingRotation(sun, upwards, out effX, out effY, out effZ);
                loggedTarget = sun;
                break;
            }
            case AttitudeMode.SpecialStarMinus:
            {
                var sunBody = GetParentStar(_vessel);
                var antiSun = Vector.negate(Vector.Reframed(Vector.normalize(Position.Delta(sunBody.Position, _telemetry.RootPosition)), referenceFrame));
                _rotation = BuildPointingRotation(antiSun, upwards, out effX, out effY, out effZ);
                loggedTarget = antiSun;
                break;
            }
            case AttitudeMode.SurfaceUp:
                // Target is "upwards" itself, so it can't also be used as LookRotation's up-hint
                // (degenerate/parallel) - use "north" as the hint instead, same remap pattern as
                // every other case.
                _rotation = BuildPointingRotation(upwards, north, out effX, out effY, out effZ);
                loggedTarget = upwards;
                break;

            case AttitudeMode.Hover:
            {
                // Desired thrust direction: local "up", blended against the horizontal
                // surface-velocity component so thrust bleeds off horizontal motion. Self-tuning
                // floor blend capped by a self-tuning max tilt angle - see the field-block comment
                // above and hover_mode_fixes.md for why (a flat-angle cap and a separate escape-valve
                // branch were both tried and found unsafe).
                var up = upwards;
                var actualVerticalSpeed = _vessel.VerticalSrfSpeed;
                var surfaceVelocity = _telemetry.SurfaceMovementVelocity;
                var horizontal = Vector.minus(surfaceVelocity, Vector.scale(up, Vector.dot(surfaceVelocity, up)));
                var horizontalSpeed = horizontal.magnitude;

                // _hoverThrottleIntegralKnown is false only before the first UpdateHoverThrottle tick
                // after engage - treat that as "unknown yet" (fall back to a nominal floor scale and
                // HoverMaxTiltAngleFallback) rather than inferring it from _throttleIntegral == 0,
                // which is also a normal mid-flight PID state, not just an engage-time default (see
                // hover_mode_fixes.md round 11).
                bool throttleDataKnown = _hoverThrottleIntegralKnown;
                double throttleScale = throttleDataKnown ? _throttleIntegral / HoverTiltReferenceThrottle : 1.0;

                // cos(max tilt angle): the angle at which holding HoverTargetVerticalSpeed would cost
                // exactly HoverTiltThrottleBudget of total throttle (_throttleIntegral / cos(angle) ==
                // budget). Clamped to at most 0.999 so the tan-based floor conversion below never
                // divides by (near-)zero.
                double cosMaxTilt = throttleDataKnown
                    ? Math.Min(_throttleIntegral / HoverTiltThrottleBudget, 0.999)
                    : Math.Cos(HoverMaxTiltAngleFallback * Math.PI / 180.0);
                double sinMaxTilt = Math.Sqrt(Math.Max(0.0, 1.0 - cosMaxTilt * cosMaxTilt));

                // Logged unconditionally (even when unused below) so [Hover/attitude] always shows
                // which term is binding - self-tuned floor, actual-speed floor, or the angle cap.
                double tiltFloor = Math.Max(HoverTiltAuthorityFloor * throttleScale, HoverTiltAuthorityFloorMin);
                double maxAngleFloor = horizontalSpeed * cosMaxTilt / sinMaxTilt;

                Vector desired;
                if (!CancelHorizontalVelocity || horizontalSpeed < 0.05)
                {
                    desired = up;
                }
                else
                {
                    double verticalFloor = Math.Max(Math.Abs(actualVerticalSpeed), tiltFloor);
                    // Hard ceiling: raise verticalFloor's effective minimum to whatever value caps
                    // atan(horizontalSpeed/verticalFloor) at the self-tuned max tilt angle.
                    verticalFloor = Math.Max(verticalFloor, maxAngleFloor);
                    desired = Vector.normalize(Vector.minus(Vector.scale(up, verticalFloor), horizontal));
                }
                // cos(tilt angle) - used by UpdateHoverThrottle to keep the vertical thrust component
                // on target while the vessel leans.
                _hoverCosTilt = Math.Max(Vector.dot(desired, up), 0.01);

                // Point the vessel's nose along the desired thrust vector directly via LookRotation
                // (see the comment on AttitudeMode.OrbitPrograde). Heading/pitch aren't user-configurable
                // in Hover, only roll - which gets the same free-when-disabled treatment as elsewhere.
                var look = Rotation.LookRotation(desired, upwards);
                effX = 0;
                effY = 0;
                effZ = ZEnabled ? Z : GetCurrentOffsetAngles(look).roll;
                _rotation = look;
                _rotation.localRotation = look.localRotation * QuaternionD.Euler(0, 0, effZ) * QuaternionD.Euler(90, 0, 0);

                // Gated behind VerboseLoggingEnabled - ILogger.LogDebug takes a plain object, so an
                // interpolated string passed directly would be built every tick regardless of whether
                // Debug-level logging is even on. See enhancement_roadmap.md item 6.
                if (Settings.VerboseLoggingEnabled.Value)
                {
                    _LOGGER.LogDebug(
                        $"[Hover/attitude] horizontalSpeed={horizontalSpeed:F2}m/s horizontal={FormatVector(horizontal)} " +
                        $"actualVerticalSpeed={actualVerticalSpeed:F2}m/s throttleIntegral={_throttleIntegral:F3} " +
                        $"tiltFloor={tiltFloor:F2} maxAngleFloor={maxAngleFloor:F2} maxTiltAngleDeg={Math.Acos(Clamp(cosMaxTilt, -1.0, 1.0)) * 180.0 / Math.PI:F1} " +
                        $"desired={FormatVector(desired)} hoverCosTilt={_hoverCosTilt:F3} refreshInterval={RefreshInterval:F3}s");
                }

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
                    var maneuver = Vector.Reframed(Vector.normalize(_telemetry.ManeuverDirection), referenceFrame);
                    _rotation = BuildPointingRotation(maneuver, upwards, out effX, out effY, out effZ);
                    loggedTarget = maneuver;
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

        // Gated behind VerboseLoggingEnabled (see the [Hover/attitude] comment above) - also skips the
        // GetAngleToRotation() call itself, not just the string formatting.
        if (Settings.VerboseLoggingEnabled.Value)
        {
            var angleToTarget = GetAngleToRotation();
            _LOGGER.LogDebug(
                $"[SetRotation] mode={AttitudeMode} offsets(H={effX:F1}[{XEnabled}],P={effY:F1}[{YEnabled}],R={effZ:F1}[{ZEnabled}]) angleToTarget={angleToTarget:F2}deg " +
                $"upwards={FormatVector(upwards)} north={FormatVector(north)}" +
                (loggedTarget.HasValue ? $" target={FormatVector(loggedTarget.Value)}" : ""));
        }
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

    // Every mode-engage entry point (SetMode itself, and SetSASKillrot/SetHover which touch _vessel
    // before delegating to SetMode) needs the same "is there actually an active vessel" guard - clicking
    // a mode button with none active (e.g. between vessel destruction and a new one becoming active)
    // used to NRE here. See enhancement_roadmap.md item 4.
    //
    // Also requires vessel.Autopilot to already exist - it's a separate, independently-populated
    // VesselAutopilot object that isn't guaranteed to be set the instant a vessel becomes _vessel (the
    // game's own VesselComponent.AutopilotStatus/SetAutopilotMode/SetAutopilotEnableDisable all null-
    // check it too). Engaging a mode before it exists would immediately NRE the next Update() tick.
    private bool TryGetVesselToEngage(string modeDescription, out VesselComponent vessel)
    {
        vessel = _vessel;
        if (vessel != null && vessel.Autopilot != null)
            return true;

        _LOGGER.LogWarning($"Cannot engage {modeDescription}: no active vessel (or its autopilot isn't ready yet).");
        vessel = null;
        return false;
    }

    private void SetMode(AttitudeMode mode)
    {
        if (!TryGetVesselToEngage(mode.ToString(), out var vessel))
            return;

        _LOGGER.LogInfo($"SAS mode -> {mode} (was {AttitudeMode})");
        AttitudeMode = mode;
        _engagedVessel = vessel;
        vessel.Autopilot.SetActive(true);
    }

    public void SetSASOff()
    {
        _LOGGER.LogInfo($"SAS mode -> {AttitudeMode.None} (was {AttitudeMode})");
        AttitudeMode = AttitudeMode.None;
        // _vessel may no longer be the vessel we were actually engaged on (see Update/
        // DisengageForVesselChange) - deactivate whichever one we last commanded, not whatever's
        // active now. _engagedVessel and/or its Autopilot may be null (no active vessel at all, or a
        // vessel switched away from and already torn down) - a single "?." only short-circuits on
        // _engagedVessel itself being null, NOT on .Autopilot being null, so both are chained.
        _engagedVessel?.Autopilot?.SetActive(false);
        _engagedVessel = null;
        ResetPerVesselState();
    }

    // Fired from Update() when the active vessel no longer matches the one the current mode was
    // engaged on (switch/undock/revert/scene-exit all resolve _vessel to something else, including
    // null) - mirrors SetSASOff's cleanup so no per-vessel control-law state leaks onto the new vessel.
    private void DisengageForVesselChange(VesselComponent newVessel)
    {
        _LOGGER.LogInfo(
            $"Active vessel changed ({_engagedVessel?.Name ?? "none"} -> {newVessel?.Name ?? "none"}) " +
            $"while {AttitudeMode} was engaged; disengaging SAS Extended.");
        Disengage();
    }

    // Fired from Update() when the maneuver node/target the current mode depends on disappears
    // (node deleted/executed, target cleared) - same cleanup as DisengageForVesselChange above,
    // just a different trigger. See enhancement_roadmap.md item 2.
    private void DisengageForLostReference(string reason)
    {
        _LOGGER.LogInfo($"{reason} while {AttitudeMode} was engaged; disengaging SAS Extended.");
        Disengage();
    }

    private void Disengage()
    {
        AttitudeMode = AttitudeMode.None;
        // See the matching comment in SetSASOff - chain "?." through .Autopilot too, it can be null.
        _engagedVessel?.Autopilot?.SetActive(false);
        _engagedVessel = null;
        ResetPerVesselState();
        Disengaged?.Invoke();
    }

    // Per-vessel control-law state that must never carry over from one vessel to another - KillRot's
    // held attitude and Hover's throttle PID integrator/filters are only meaningful for the vessel they
    // were captured on.
    private void ResetPerVesselState()
    {
        _killRotTarget = default;
        _throttleIntegral = 0;
        _hoverThrottleIntegralKnown = false;
        _hoverCosTilt = 1.0;
        _lastVerticalSpeedForDerivative = 0;
        _filteredThrustDrivenAccel = 0;
        HoverThrottle = 0;
    }

    public void SetSASKillrot()
    {
        if (!TryGetVesselToEngage("KillRot", out var vessel))
            return;

        // Capture whatever attitude the vessel is at right now - KillRot holds this, it doesn't
        // steer toward any particular direction (see the comment on AttitudeMode.KillRot).
        _killRotTarget = vessel.ControlTransform.Rotation;
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
    /// Engages hover: SAS holds the vessel thrust-up (tilting to null horizontal velocity, unless
    /// disabled via <see cref="CancelHorizontalVelocity"/>) while the throttle controller drives
    /// vertical velocity to <see cref="HoverTargetVerticalSpeed"/> - "hold vertical speed", not
    /// altitude; see the field-block comment above. Both are user-set values carried over from the
    /// last time hover was engaged (deliberately NOT reset here) rather than forced back to a default
    /// every engage.
    /// </summary>
    public void SetHover()
    {
        if (!TryGetVesselToEngage("Hover", out var vessel))
            return;

        // Seed the throttle integrator with the current throttle so engaging hover doesn't jolt the
        // engines, and the derivative term with the current vertical speed so its first tick doesn't
        // see a spurious jump from 0.
        _throttleIntegral = vessel.flightCtrlState.mainThrottle;
        _hoverThrottleIntegralKnown = false;
        _hoverCosTilt = 1.0;
        _lastVerticalSpeedForDerivative = vessel.VerticalSrfSpeed;
        _filteredThrustDrivenAccel = vessel.gravityForPos.magnitude; // matches a coasting vessel's true accel (0 thrust-driven) so the D term doesn't see a spurious jump from 0 on the first tick
        HoverThrottle = vessel.flightCtrlState.mainThrottle;

        SetMode(AttitudeMode.Hover);
        _LOGGER.LogInfo(
            $"Hover engage altitude={vessel.AltitudeFromSurface:F1}m verticalSpeed={vessel.VerticalSrfSpeed:F2}m/s throttle={HoverThrottle:F2}");
    }

    /// <summary>
    /// Vertical-speed throttle controller (holds <see cref="HoverTargetVerticalSpeed"/>, not
    /// altitude - see the field-block comment above). A proportional term gives an immediate
    /// response, an integral term self-tunes to the vessel's hover throttle regardless of TWR (so
    /// no per-vessel thrust/mass model is needed), and a derivative term on the vessel's own
    /// vertical acceleration damps the overshoot the P+I terms alone let through. Tilt (used to
    /// kill horizontal velocity) is divided out so the vertical thrust component stays on target
    /// while the vessel leans.
    /// </summary>
    private void UpdateHoverThrottle()
    {
        double dt = Time.deltaTime;
        if (dt <= 0.0)
            return;

        double actualVerticalSpeed = _vessel.VerticalSrfSpeed;
        double verticalSpeedError = HoverTargetVerticalSpeed - actualVerticalSpeed;
        // Derivative-on-measurement (not on error) so a future change to HoverTargetVerticalSpeed
        // doesn't itself spike this term - only the vessel's own acceleration does.
        double verticalAccel = (actualVerticalSpeed - _lastVerticalSpeedForDerivative) / dt;
        _lastVerticalSpeedForDerivative = actualVerticalSpeed;
        // Subtract out the baseline free-fall deceleration so the D term only reacts to
        // thrust/drag-driven acceleration, not to gravity itself - a raw-vAccel D term can't tell
        // ordinary coasting deceleration apart from a real overshoot, and fires throttle to fight
        // gravity on high-g bodies (see hover_mode_fixes.md round 4). Use gravityForPos, not
        // gravityTrue - the latter is a dead field, never assigned anywhere in the decompiled type
        // (see [[verify-decompiled-fields]] in memory).
        double thrustDrivenAccel = verticalAccel + _vessel.gravityForPos.magnitude;
        // Low-pass filter before this feeds the D term - see HoverThrottleAccelFilterTime's field
        // comment for why.
        double filterAlpha = 1.0 - Math.Exp(-dt / HoverThrottleAccelFilterTime);
        _filteredThrustDrivenAccel += (thrustDrivenAccel - _filteredThrustDrivenAccel) * filterAlpha;

        _throttleIntegral = Clamp(_throttleIntegral + verticalSpeedError * HoverThrottleKi * dt, 0.0, 1.0);
        // From here on _throttleIntegral is a real, live PID value - even if it happens to be exactly
        // 0.0 (a normal state, not "no data yet"; see SetRotation's Hover case for why that
        // distinction matters).
        _hoverThrottleIntegralKnown = true;
        double throttle = (_throttleIntegral + verticalSpeedError * HoverThrottleKp - _filteredThrustDrivenAccel * HoverThrottleKd) / _hoverCosTilt;
        double clampedThrottle = Clamp(throttle, 0.0, 1.0);

        // Rate-limit the actuator itself - see HoverThrottleMaxRate's field comment for why. This is
        // the last step before the value is published, so it bounds what the engine actually does
        // regardless of how large a jump the P/I/D formula above just asked for.
        double maxDelta = HoverThrottleMaxRate * dt;
        double rateLimitedThrottle = Clamp(clampedThrottle, HoverThrottle - maxDelta, HoverThrottle + maxDelta);

        HoverThrottle = (float)Clamp(rateLimitedThrottle, 0.0, 1.0);

        // Runs every frame, so log on its own slower timer (independent of the attitude
        // RefreshInterval) to avoid flooding the log while still catching throttle saturation
        // (pinned at 0 or 1) or a vertical-speed error that never settles. Also gated behind
        // VerboseLoggingEnabled (see the [Hover/attitude] comment above) - the 0.25s timer alone
        // still built this string every quarter-second regardless of whether debug logging was on.
        if (Settings.VerboseLoggingEnabled.Value && _UT - _lastHoverLogTime > 0.25)
        {
            _lastHoverLogTime = _UT;
            _LOGGER.LogDebug(
                $"[Hover/throttle] altitude={_vessel.AltitudeFromSurface:F1}m targetVSpeed={HoverTargetVerticalSpeed:F2}m/s " +
                $"actualVSpeed={actualVerticalSpeed:F2}m/s vSpeedErr={verticalSpeedError:F2}m/s vAccel={verticalAccel:F2}m/s^2 " +
                $"thrustDrivenAccel={thrustDrivenAccel:F2}m/s^2 filteredThrustDrivenAccel={_filteredThrustDrivenAccel:F2}m/s^2 throttleIntegral={_throttleIntegral:F3} " +
                $"throttlePreClamp={throttle:F3}[{(throttle <= 0.0 || throttle >= 1.0 ? "SATURATED" : "ok")}] " +
                $"clampedThrottle={clampedThrottle:F3}[{(clampedThrottle != rateLimitedThrottle ? "RATE-LIMITED" : "ok")}] " +
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

    // Public: also read by MainWindowController for the status readout's generic angle-to-target line
    // (every pointing mode - ORB/SURF/TGT/SPEC/Node/TGT PAR - shares this, per enhancement_roadmap.md
    // item 3; KillRot/Hover don't point anywhere, so they use their own readouts below instead).
    public double GetAngleToRotation()
    {
        // Guards the same one-frame window as GetAngularVelocityDegPerSec below: the UI (MainWindowController)
        // reads this whenever AttitudeMode != None, and Unity doesn't guarantee this MonoBehaviour's own
        // Update (which disengages AttitudeMode the moment _vessel no longer matches _engagedVessel) runs
        // before the UI's Update in the same frame.
        if (_vessel == null)
            return 0;

        // _rotation is only ever assigned inside SetRotation(), which Update() doesn't call until its
        // own next tick - engaging a mode sets AttitudeMode immediately, so the UI can read this in the
        // same frame before _rotation has ever been computed (default(Rotation).coordinateSystem is
        // null, which Vector.Reframed below would NRE on).
        if (_rotation.coordinateSystem == null)
            return 0;

        // _rotation.coordinateSystem is always our shared "referenceFrame" (see SetRotation), which is
        // generally NOT the same frame as _vessel.transform.coordinateSystem - comparing their raw
        // vectors directly (as the previous code did) is the same coordinate-mixing bug fixed elsewhere
        // in this file. Reframe the vessel's current "nose" direction into _rotation's frame first.
        var currentAttitude = Vector.Reframed(_vessel.MOI.coordinateSystem.up, _rotation.coordinateSystem);
        var currentRotation = _rotation.localRotation * Vector3d.up;
        return Vector3d.Angle(currentAttitude.vector, currentRotation);
    }

    // KillRot doesn't point anywhere (see the AttitudeMode.KillRot case in SetRotation), so an
    // angle-to-target isn't meaningful there - angular velocity magnitude shows how fast the vessel is
    // still tumbling instead, trending to 0 as it settles. KSP2's physics angular velocity is
    // radians/second (Unity Rigidbody convention); converted to degrees/second to match every other
    // angle in this file.
    public double GetAngularVelocityDegPerSec()
    {
        if (_vessel == null)
            return 0;
        return _vessel.AngularVelocityMassAvg.relativeAngularVelocity.magnitude * (180.0 / Math.PI);
    }

    // Actual current vertical/horizontal surface speed for the Hover status readout - the same
    // telemetry UpdateHoverThrottle's control law and tilt calc already use, just via the plain
    // VesselComponent accessors since this is read independently of the control loop's own cadence.
    public double GetHoverVerticalSpeed() => _vessel?.VerticalSrfSpeed ?? 0;
    public double GetHoverHorizontalSpeed() => _vessel?.HorizontalSrfSpeed ?? 0;

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
