using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Models;
using SASExtended.PureMath;
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

    public double X = 0, Y = 0, Z = 0;
    public bool XEnabled = true, YEnabled = true, ZEnabled = true;
    public AttitudeMode AttitudeMode = AttitudeMode.None;
    public bool IsHoverActive => AttitudeMode == AttitudeMode.Hover;

    // True while any SAS Extended mode is actively driving the vessel (i.e. not OFF/None). Read by
    // FlightAxesVisualizer to decide whether the "commanded attitude" arrow should be shown.
    public bool IsEngaged => AttitudeMode != AttitudeMode.None;

    // The exact Rotation last handed to SAS.LockRotation this tick - the slew-limited setpoint
    // (_commandedRotation), NOT the raw per-tick target (_rotation). Exposed for FlightAxesVisualizer's
    // "commanded attitude" arrow so the visual matches what the autopilot is actually being told to
    // hold. Holds a stale value once disengaged, so consumers must gate on IsEngaged first.
    public Rotation CommandedRotation => _commandedRotation;

    // The raw per-tick target the active mode wants to point at (_rotation), BEFORE the slew-rate limit
    // (AdvanceCommandedRotation) is applied - i.e. the final orientation the mode is steering toward,
    // which CommandedRotation slews up to over multiple ticks. Exposed for FlightAxesVisualizer's
    // "target attitude" arrow; the two arrows diverge during a reorientation and coincide once settled.
    // Holds a stale value once disengaged, so consumers must gate on IsEngaged first.
    public Rotation TargetRotation => _rotation;

    // Read by MainWindowController to grey out the NODE button / TGT-tab mode buttons and by
    // Update() below to auto-disengage if the node/target disappears while its mode is active.
    // _vessel is guarded first since _telemetry (a property, not a field) NREs on a null _vessel.
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

    // Max rate the commanded setpoint is allowed to slew toward the true target (_rotation) per
    // second - caps how large a single-tick reorientation Redux's SAS PID is ever asked to track.
    // Default picked from the Prograde->Retrograde oscillation bug's own telemetry: the vessel
    // sustained ~60-68 deg/s during the (bad) unlimited-jump case, so a flat cap in that
    // neighborhood should remove the overshoot/reversal without meaningfully slowing a typical
    // reorientation. See AdvanceCommandedRotation.
    public double AttitudeSlewMaxRate = 60; // deg/s

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
    // Setpoint actually fed to LockRotation - each tick, AdvanceCommandedRotation recomputes this
    // fresh as "the vessel's ACTUAL current attitude, moved up to AttitudeSlewMaxRate deg/s toward
    // _rotation (the true target)". Deliberately anchored to the vessel's real attitude every tick
    // rather than slewed from this field's own previous value - see AdvanceCommandedRotation's
    // comment for why that distinction matters (an earlier version that slewed from its own prior
    // value let the setpoint race ahead of the real vessel and reach the target early, reproducing
    // the oscillation this exists to fix). Seeded from the vessel's real current attitude on every
    // fresh engage (SetMode) and cleared in ResetPerVesselState purely so the diagnostic log's
    // commandedAngleToTarget reads sensibly on the very first tick after an engage; not load-bearing
    // for AdvanceCommandedRotation itself since it no longer depends on this field's prior value.
    private Rotation _commandedRotation;
    // Attitude held for AttitudeMode.KillRot - captured once at engage time (see SetSASKillrot) and
    // then just held, mirroring the game's own vanilla StabilityAssist ("SAS off, kill rotation, hold
    // whatever attitude you're currently at" - not pointed at any particular direction like north).
    private Rotation _killRotTarget;
    // Attitude held for AttitudeMode.Hold - captured once at engage time (see SetHold), reframed into
    // the universe's actual non-rotating inertial frame (NOT vessel.ControlTransform.Rotation's own
    // coordinateSystem, which is body/celestial-frame-relative and would silently rotate with the
    // reference body over time - see the SetHold comment). Pre-multiplied by Euler(-90,0,0) so it can
    // be fed straight into ApplyOffsets/AttitudeMath.ComposePointingRotation like every other mode's
    // LookRotation-built "look" value.
    private Rotation _holdTarget;
    // The vessel that was active when the current AttitudeMode was engaged. _vessel (above) is a live
    // property that silently resolves to whatever vessel is active *now* - compared against this every
    // Update tick so a switch/undock/revert/scene-exit disengages instead of applying this vessel's
    // captured per-vessel state (_killRotTarget, the Hover throttle integrator/filters) to a different
    // vessel.
    private VesselComponent _engagedVessel;

    // Tracks the no-vessel -> vessel transition for the stock-SAS reconciliation check below -
    // deliberately separate from _engagedVessel, which only ever refers to a vessel SAS Extended
    // itself engaged. See that check's comment for why this transition specifically matters.
    private VesselComponent _lastSeenVessel;

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
        var vessel = _vessel;

        // Stock SAS (Autopilot.Enabled/AutopilotMode) is persisted in the save file; AttitudeMode
        // here is not - it's an in-memory field on this MonoBehaviour, reset to None every scene/
        // save load. If a save is taken at the moment a SAS Extended mode had stock SAS latched on
        // (e.g. Hover mid-engage right before a crash) and later reloaded, the newly-created vessel
        // comes back with stock SAS already Enabled with nothing feeding it a live LockRotation
        // target - stuck half-engaged rather than off, and AttitudeMode == None means Update() below
        // would otherwise never notice or touch it. Reconciled only on the specific no-vessel ->
        // vessel transition (flight start / save load / scene load) - NOT on an ordinary
        // vessel-to-vessel switch mid-flight (_lastSeenVessel already non-null), where the new
        // vessel's own stock SAS state is the player's legitimate business and must not be touched.
        if (vessel != null && _lastSeenVessel == null && AttitudeMode == AttitudeMode.None
            && vessel.Autopilot != null && vessel.Autopilot.Enabled)
        {
            _LOGGER.LogInfo(
                $"Vessel '{vessel.Name}' became active with stock SAS already enabled but SAS Extended has no record of engaging it (likely a save/scene reload) - disabling stock SAS so it isn't left stuck.");
            vessel.Autopilot.SetActive(false);
        }
        _lastSeenVessel = vessel;

        if (AttitudeMode == AttitudeMode.None)
            return;

        // _vessel is a live lookup (GetActiveSimVessel()) - if it no longer matches the vessel we
        // engaged on, the active vessel was switched/undocked/reverted out from under us, or flight
        // was exited entirely (_vessel goes null). Disengage rather than silently applying this
        // vessel's captured state (KillRot's held attitude, Hover's throttle integrator) to whatever
        // vessel is active now.
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

        // Two-way sync with stock SAS: we only ever drive the
        // vessel through Autopilot.SetActive(true) (-> Activate(StabilityAssist)) followed by
        // SAS.LockRotation, so Enabled and AutopilotMode should always read back exactly
        // (true, StabilityAssist) while one of our modes is engaged. If either has drifted, something
        // external changed stock SAS out from under us - the player pressed T / clicked the SAS
        // toggle off (Enabled -> false) or clicked a stock direction button / hotkey (AutopilotMode ->
        // Prograde/Retrograde/Target/Maneuver/etc.). Disengaging here (rather than just letting our
        // next LockRotation silently fight the stock input) both stops the fight and - via the shared
        // Disengaged event - flips the UI back to OFF so it doesn't keep showing a mode we no longer
        // actually control.
        //
        // Polled here instead of subscribing to SASEnabledMessage/SASDisabledMessage/
        // SASModeChangedMessage as the roadmap item originally suggested: decompiling
        // VesselComponent.SetAutopilotEnableDisable confirmed those three only fire together, on the
        // Enabled/Disabled transition (T key) - stock direction-button clicks route through
        // TelemetryDataProvider.SetAutopilotMode -> VesselComponent.SetState -> SetAutopilotMode
        // without publishing anything. A pure message subscription would miss that second case
        // entirely; polling the two fields we already read every tick catches both uniformly.
        if (!vessel.Autopilot.Enabled || vessel.Autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
        {
            DisengageForExternalChange();
            return;
        }

        // _UT - _lastRefreshTime can go NEGATIVE: _lastRefreshTime is a plain UniverseTime bookmark
        // that survives a save/quickload (this MonoBehaviour is never destroyed across one), but
        // loading a save rewinds UniverseTime backward to whenever the save was taken. Without the
        // "< 0" branch below, a rewind left this gate permanently closed (elapsed stays negative,
        // never exceeds RefreshInterval) until real gameplay time climbed back up past the stale
        // pre-reload _lastRefreshTime - SetMode()/the Autopilot Enabled/AutopilotMode guard above all
        // read back fine, so the mode LOOKED engaged, but SetRotation()/LockRotation() silently never
        // ran and the vessel got no commands at all until the gap closed (confirmed via Player.log:
        // reload -> re-engage logged normally -> vessel motionless for a stretch of real time before
        // starting to respond, matching the pre-reload/post-reload UT gap exactly).
        double elapsed = _UT - _lastRefreshTime;
        if (elapsed < 0 || elapsed > RefreshInterval /*DebugUI.Instance.RefreshInterval*/)
        {
            // Clamp dt to RefreshInterval_long: normal operation already keeps elapsed time within
            // the 0.02-0.08s adaptive band (no-op here), but if SAS Extended was idle for a while
            // (mode was None, or a scene hitch), an unclamped dt would let RotateTowards snap
            // straight to target on the very first tick - silently reintroducing the oscillation
            // bug AdvanceCommandedRotation exists to fix, right when it matters most. A negative
            // elapsed (UT rewind, see above) is clamped to 0 rather than clamped-and-fed-through - a
            // negative dt would make AdvanceCommandedRotation/RotateTowards's maxDegrees negative too.
            double dt = Math.Min(Math.Max(elapsed, 0), RefreshInterval_long);
            SetRotation();
            AdvanceCommandedRotation(dt);
            vessel.Autopilot.SAS.LockRotation(_commandedRotation);
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
        // one case's vector(s) are ever needed per tick). This cuts per-tick Vector.Reframed calls
        // from ~18 down to 1 (2 for negated +/- pairs).
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
                // in Hover, and neither is roll in practice - the hover-controls panel (see
                // MainWindowController.UpdatePanelForMode) replaces the shared Heading/Pitch/Roll panel
                // entirely and has no roll control of its own, so ZEnabled/Z here would just be
                // whatever stale value was left over from the last non-Hover mode that did expose them.
                // Roll is therefore always free (matches the vessel's own current roll every tick, same
                // mechanism the shared panel's disabled-axis case uses) regardless of ZEnabled.
                //
                // Up-hint is "north", NOT "upwards": "desired" is always within a few degrees of
                // "upwards" in normal hover (near-zero drift means desired == up exactly - see just
                // above), which is the same degenerate/near-parallel forward-vs-up-hint case
                // AttitudeMode.SurfaceUp already works around by swapping in "north". Using "upwards"
                // here left LookRotation's twist-around-forward numerically ill-conditioned, which
                // showed up in-game as a slow continuous roll (and, since pitch sits ~90deg, a coupled
                // heading) spin while hovering - confirmed via Player.log's [SetRotation] telemetry
                // (heading and roll drifting together at a steady few deg/s with angleToTarget staying
                // near 0, i.e. the autopilot faithfully tracking a commanded target that was itself
                // spinning). "north" is always ~perpendicular to "up" so this is well-conditioned
                // regardless of tilt angle.
                var look = Rotation.LookRotation(desired, north);
                effX = 0;
                effY = 0;
                effZ = GetCurrentOffsetAngles(look).roll;
                _rotation = look;
                _rotation.localRotation = look.localRotation * QuaternionD.Euler(0, 0, effZ) * QuaternionD.Euler(90, 0, 0);

                // Gated behind VerboseLoggingEnabled - ILogger.LogDebug takes a plain object, so an
                // interpolated string passed directly would be built every tick regardless of whether
                // Debug-level logging is even on.
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

            case AttitudeMode.Hold:
                // Unlike every mode above, the "look" here isn't recomputed from live telemetry each
                // tick - _holdTarget is a fixed snapshot captured once at engage time (see SetHold), so
                // the vessel holds the same direction in inertial space through orbital motion, SOI
                // changes, and time warp. H/P/R trim still applies on top via the normal ApplyOffsets
                // path (unlike KillRot, which has no trim panel at all).
                _rotation = ApplyOffsets(_holdTarget, out effX, out effY, out effZ);
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

            default: // horizon
                _rotation = BuildPointingRotation(north, upwards, out effX, out effY, out effZ);
                break;
        }

        // Gated behind VerboseLoggingEnabled (see the [Hover/attitude] comment above) - also skips the
        // GetAngleToRotation() call itself, not just the string formatting.
        if (Settings.VerboseLoggingEnabled.Value)
        {
            var angleToTarget = GetAngleToRotation();
            var (currentHeading, currentPitch, currentRoll) = ComputeHeadingPitchRoll(north, upwards, _vessel.ControlTransform.Rotation);
            var angularVelocity = GetAngularVelocityDegPerSec();
            // Logged BEFORE AdvanceCommandedRotation runs this tick (Update() calls SetRotation()
            // first) - this is the gap the upcoming slew step is about to close, so comparing it
            // across ticks shows whether the commanded setpoint is closing on the true target at
            // roughly slewCapDeg per tick, never jumping straight to it. Mirrors GetAngleToRotation's
            // reframe-then-Vector3d.Angle pattern, just comparing _commandedRotation instead of the
            // vessel's actual attitude.
            var commandedReframed = Rotation.Reframed(_commandedRotation, _rotation.coordinateSystem);
            var commandedAngleToTarget = Vector3d.Angle(commandedReframed.localRotation * Vector3d.up, _rotation.localRotation * Vector3d.up);
            var slewCapDeg = AttitudeSlewMaxRate * Math.Min(_UT - _lastRefreshTime, RefreshInterval_long);
            _LOGGER.LogDebug(
                $"[SetRotation] mode={AttitudeMode} heading={currentHeading:F1}deg pitch={currentPitch:F1}deg roll={currentRoll:F1}deg " +
                $"angularVelocity={angularVelocity:F1}deg/s altitude={_vessel.AltitudeFromSurface:F1}m verticalSpeed={_vessel.VerticalSrfSpeed:F2}m/s " +
                $"offsets(H={effX:F1}[{XEnabled}],P={effY:F1}[{YEnabled}],R={effZ:F1}[{ZEnabled}]) angleToTarget={angleToTarget:F2}deg " +
                $"commandedAngleToTarget={commandedAngleToTarget:F2}deg slewCapDeg={slewCapDeg:F2} " +
                $"upwards={FormatVector(upwards)} north={FormatVector(north)}" +
                (loggedTarget.HasValue ? $" target={FormatVector(loggedTarget.Value)}" : ""));
        }
    }

    // Rate-limits the setpoint actually handed to LockRotation. Anchored to the vessel's ACTUAL
    // current attitude every tick (ControlTransform.Rotation) rather than the previous
    // _commandedRotation value - slewing from the setpoint's own prior value let it race ahead of
    // the real vessel (which is torque/inertia-limited) and reach the true target in a few seconds
    // while the vessel itself was still 100+ degrees away, at which point we were once again handing
    // Redux's SAS PID the raw, un-smoothed target - reproducing the original oscillation, just
    // delayed instead of fixed (confirmed in-game: commandedAngleToTarget hit 0 while angleToTarget
    // was still ~77 degrees and roll was mid-oscillation). Anchoring to the vessel's real attitude
    // instead means the commanded setpoint is never more than maxDegrees ahead of wherever the
    // vessel actually is, so the PID's tracking error is always bounded regardless of how fast the
    // vessel can physically turn.
    //
    // _rotation's coordinateSystem varies by AttitudeMode (referenceFrame for pointing modes,
    // ControlTransform's own frame for KillRot/Maneuver's no-target fallback, the universe inertial
    // frame for Hold), so the vessel's attitude is reframed into _rotation's CURRENT coordinateSystem
    // every single tick before interpolating - never assume they already share a frame (mirrors how
    // Rotation.Slerp reframes internally). QuaternionD.RotateTowards is the engine's own max-angle-
    // step primitive (Slerp clamped to at most maxDegreesDelta) - see the [SetRotation] log's
    // commandedAngleToTarget/slewCapDeg fields to verify this in a Player.log capture.
    private void AdvanceCommandedRotation(double dt)
    {
        // Hover is exempt: its target ("desired" thrust tilt, see AttitudeMode.Hover) is recomputed
        // every tick as a direct function of the vessel's OWN current horizontal velocity - a tight
        // closed loop, unlike every other mode's externally-driven target. Anchoring-and-chasing from
        // the vessel's actual attitude bakes a persistent, never-closing tracking lag into that loop
        // (Player.log showed commandedAngleToTarget holding steady at ~1.7deg indefinitely, never
        // reaching 0) - enough phase delay to turn horizontal-velocity nulling into a slow precession
        // instead of convergence (heading rotating ~24deg/s forever, horizontal drift orbiting rather
        // than decaying to 0). Hover's own desired vector never jumps antipodally the way
        // Prograde<->Retrograde does (see AttitudeMode.Hover - it's always within a bounded cone near
        // "up"), so it never needed this rate limiter's protection in the first place; feed it straight
        // through, matching pre-slew-limiter behavior for this mode only.
        if (AttitudeMode == AttitudeMode.Hover)
        {
            _commandedRotation = _rotation;
            return;
        }

        var currentAttitude = Rotation.Reframed(_vessel.ControlTransform.Rotation, _rotation.coordinateSystem);
        double maxDegrees = AttitudeSlewMaxRate * Math.Max(0, dt);
        _commandedRotation = _rotation; // adopt the true target's coordinateSystem
        _commandedRotation.localRotation =
            QuaternionD.RotateTowards(currentAttitude.localRotation, _rotation.localRotation, maxDegrees);
    }

    private static string FormatVector(Vector v) => $"({v.vector.x:F3},{v.vector.y:F3},{v.vector.z:F3})";

    // Actual current compass heading/pitch/roll of the vessel (relative to north/horizontal),
    // independent of whatever direction any mode happens to be pointing at - used both at
    // mode-activation time (SetMode) and in the regular per-tick telemetry log below, so
    // troubleshooting always has a ground truth for "where was the vessel actually facing/how was it
    // banked" alongside the commanded offsets. Reuses GetCurrentOffsetAngles' solve-for-offset trick
    // with the horizon frame itself as the "look" rotation (heading 0/pitch 0/roll 0 == pointing at
    // north, level, wings level) instead of whatever the engaged mode's own target is. Logging roll
    // here (previously omitted) is what lets a violent-roll-during-reorientation report actually be
    // confirmed from the log: a real multi-rotation roll shows up as this value cycling through its
    // full +-180 range repeatedly between successive ticks, instead of just heading/pitch swinging.
    private static (double heading, double pitch, double roll) ComputeHeadingPitchRoll(Vector north, Vector upwards, Rotation vesselRotation)
    {
        var horizonLook = Rotation.LookRotation(north, upwards);
        var current = Rotation.Reframed(vesselRotation, horizonLook.coordinateSystem);
        return AttitudeMath.GetCurrentOffsetAngles(horizonLook.localRotation, current.localRotation);
    }

    // Builds the commanded attitude for a "point the nose at `target`" mode: LookRotation aligns local
    // up (the nose - see the trailing Euler(90,0,0)) with `target` exactly, then the H/P/R offsets are
    // applied on top via ApplyOffsets. A disabled H/P/R control isn't just forced to 0 - see
    // GetCurrentOffsetAngles.
    private Rotation BuildPointingRotation(Vector target, Vector upHint, out double appliedX, out double appliedY, out double appliedZ)
        => ApplyOffsets(Rotation.LookRotation(target, upHint), out appliedX, out appliedY, out appliedZ);

    // Shared H/P/R offset application, factored out of BuildPointingRotation so AttitudeMode.Hold (whose
    // "look" is a captured attitude snapshot, not something built fresh from a target/upHint pair every
    // tick) can reuse the exact same fixed-value-vs-free-track behavior as every other pointing mode.
    private Rotation ApplyOffsets(Rotation look, out double appliedX, out double appliedY, out double appliedZ)
    {
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
        rotation.localRotation = AttitudeMath.ComposePointingRotation(look.localRotation, appliedX, appliedY, appliedZ);
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
        return AttitudeMath.GetCurrentOffsetAngles(look.localRotation, currentRotation.localRotation);
    }

    // Every mode-engage entry point (SetMode itself, and SetSASKillrot/SetHover which touch _vessel
    // before delegating to SetMode) needs the same "is there actually an active vessel" guard - clicking
    // a mode button with none active (e.g. between vessel destruction and a new one becoming active)
    // used to NRE here.
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

        // Snapshot heading/pitch/roll/hover (altitude, vertical speed) state at the exact moment any
        // mode engages - always-on (LogInfo, not gated behind VerboseLoggingEnabled) so there's a
        // ground-truth starting point for every mode switch even with verbose per-tick logging off.
        var telemetry = vessel.SimulationObject.Telemetry;
        telemetry.RefreshAutopilotTelemetry();
        var north = telemetry.HorizonNorth;
        var upwards = Vector.Reframed(Vector.normalize(Position.Delta(telemetry.RootPosition, telemetry.SOIPosition)), north.coordinateSystem);
        var currentAttitude = vessel.ControlTransform.Rotation;
        var (heading, pitch, roll) = ComputeHeadingPitchRoll(north, upwards, currentAttitude);

        _LOGGER.LogInfo(
            $"SAS mode -> {mode} (was {AttitudeMode}) heading={heading:F1}deg pitch={pitch:F1}deg roll={roll:F1}deg " +
            $"angularVelocity={GetAngularVelocityDegPerSec():F1}deg/s " +
            $"altitude={vessel.AltitudeFromSurface:F1}m verticalSpeed={vessel.VerticalSrfSpeed:F2}m/s");
        AttitudeMode = mode;
        _engagedVessel = vessel;
        // Seed the slew setpoint from the vessel's real current attitude so the very first
        // AdvanceCommandedRotation step starts from reality, not a stale rotation left over from a
        // previous mode/vessel - see _commandedRotation's field comment.
        _commandedRotation = currentAttitude;
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
    // just a different trigger.
    private void DisengageForLostReference(string reason)
    {
        _LOGGER.LogInfo($"{reason} while {AttitudeMode} was engaged; disengaging SAS Extended.");
        Disengage();
    }

    // Fired from Update() when stock SAS (Autopilot.Enabled / Autopilot.AutopilotMode) no longer
    // matches what we last commanded - see the poll above for why. Deliberately does NOT deactivate
    // the Autopilot like the other Disengage* helpers do: Enabled/AutopilotMode already reflect
    // whatever the player/stock UI just set (still Enabled, now pointed at Prograde; or already
    // deactivated by the game's own T-key handler) - calling SetActive(false) here would immediately
    // fight the very input that triggered this disengage (e.g. force stock SAS off right after the
    // player turned on Prograde). We only need to stop OUR tracking and reset our own UI/state.
    private void DisengageForExternalChange()
    {
        _LOGGER.LogInfo($"Stock SAS was changed externally while {AttitudeMode} was engaged; disengaging SAS Extended.");
        Disengage(deactivateAutopilot: false);
    }

    private void Disengage(bool deactivateAutopilot = true)
    {
        AttitudeMode = AttitudeMode.None;
        // See the matching comment in SetSASOff - chain "?." through .Autopilot too, it can be null.
        if (deactivateAutopilot)
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
        _holdTarget = default;
        _commandedRotation = default;
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

    /// <summary>
    /// Engages Hold: snapshots the vessel's current facing and locks to it, fixed in inertial space,
    /// through orbital motion, SOI changes, and time warp - unlike every direction-vector mode above
    /// (which tracks a frame that itself moves) and unlike KillRot (which - via
    /// vessel.ControlTransform.Rotation's own body/celestial-relative coordinateSystem - holds an
    /// attitude that would drift as the reference body rotates/orbits). The snapshot is reframed into
    /// the game's actual universe inertial frame (GameManager...UniverseModel.inertialReferenceFrame.
    /// inertialReferenceFrame - confirmed via decompile to be the frame VesselComponent's own
    /// ParentToInertialReferenceFrame()/IsChildOfInertialReferenceFrame() use) before being held, so it
    /// stays fixed in space rather than silently co-rotating with whatever frame ControlTransform
    /// happens to be parented to right now.
    /// </summary>
    public void SetHold()
    {
        if (!TryGetVesselToEngage("Hold", out var vessel))
            return;

        var universeFrame = GameManager.Instance.Game.UniverseModel.inertialReferenceFrame.inertialReferenceFrame;
        var snapshot = Rotation.Reframed(vessel.ControlTransform.Rotation, universeFrame);
        // Pre-multiply by Euler(-90,0,0) so ApplyOffsets/AttitudeMath.ComposePointingRotation's trailing
        // Euler(90,0,0) (baked in to remap LookRotation's Z-forward convention onto the nose/up axis -
        // see BuildPointingRotation) cancels back out to exactly the captured attitude when all three
        // offsets are 0: two rotations about the same (X) axis commute and simply cancel.
        snapshot.localRotation *= QuaternionD.Euler(-90, 0, 0);
        _holdTarget = snapshot;

        SetMode(AttitudeMode.Hold);
        _LOGGER.LogInfo("Hold engaged, snapshot captured in universe inertial frame.");
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

        // Altitude/verticalSpeed/heading/pitch are already logged by SetMode just above - only the
        // throttle seed is specific to Hover engagement.
        SetMode(AttitudeMode.Hover);
        _LOGGER.LogInfo($"Hover engage throttle={HoverThrottle:F2}");
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

        // Use gravityForPos, not gravityTrue - the latter is a dead field, never assigned anywhere in
        // the decompiled type (see [[verify-decompiled-fields]] in memory).
        var result = HoverThrottleMath.Step(
            new HoverThrottleState
            {
                ThrottleIntegral = _throttleIntegral,
                ThrottleIntegralKnown = _hoverThrottleIntegralKnown,
                LastVerticalSpeedForDerivative = _lastVerticalSpeedForDerivative,
                FilteredThrustDrivenAccel = _filteredThrustDrivenAccel,
                Throttle = HoverThrottle,
            },
            dt, actualVerticalSpeed, HoverTargetVerticalSpeed, _vessel.gravityForPos.magnitude, _hoverCosTilt,
            HoverThrottleKp, HoverThrottleKi, HoverThrottleKd, HoverThrottleAccelFilterTime, HoverThrottleMaxRate);

        _throttleIntegral = result.State.ThrottleIntegral;
        _hoverThrottleIntegralKnown = result.State.ThrottleIntegralKnown;
        _lastVerticalSpeedForDerivative = result.State.LastVerticalSpeedForDerivative;
        _filteredThrustDrivenAccel = result.State.FilteredThrustDrivenAccel;
        HoverThrottle = result.State.Throttle;

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
                $"actualVSpeed={actualVerticalSpeed:F2}m/s vSpeedErr={result.VerticalSpeedError:F2}m/s vAccel={result.VerticalAccel:F2}m/s^2 " +
                $"thrustDrivenAccel={result.ThrustDrivenAccel:F2}m/s^2 filteredThrustDrivenAccel={_filteredThrustDrivenAccel:F2}m/s^2 throttleIntegral={_throttleIntegral:F3} " +
                $"throttlePreClamp={result.ThrottlePreClamp:F3}[{(result.ThrottlePreClamp <= 0.0 || result.ThrottlePreClamp >= 1.0 ? "SATURATED" : "ok")}] " +
                $"clampedThrottle={result.ClampedThrottle:F3}[{(result.ClampedThrottle != result.RateLimitedThrottle ? "RATE-LIMITED" : "ok")}] " +
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
    // (every pointing mode - ORB/SURF/TGT/SPEC/Node/TGT PAR - shares this; KillRot/Hover don't point
    // anywhere, so they use their own readouts below instead).
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

}
}
