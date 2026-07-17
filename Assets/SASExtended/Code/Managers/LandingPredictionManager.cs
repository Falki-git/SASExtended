using System;
using KSP.Api;
using KSP.Game;
using KSP.Rendering;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Utilities;
using Shapes;
using UnityEngine;
using UnityEngine.Rendering;

namespace SASExtended.Managers
{

/// <summary>
/// Draws an optional flight-view landing prediction for the active vessel: a trajectory line
/// following its predicted unpowered (coast) path down to the ground, and a marker at the
/// predicted impact point. Modelled on KSP1 MechJeb2's "Landing Guidance -> Show Landing
/// predictions", toggled from the SAS Extended window's settings panel - see
/// <c>.claude/landing_prediction_plan.md</c> for the full design.
///
/// Pure ballistic/Keplerian coast - no drag or parachute model, so on a body with an atmosphere
/// the prediction ignores atmospheric effects entirely (still useful as a rough indicator for
/// low-drag-interference flights; just don't trust it through a real reentry). Always-on-top
/// rendering (no depth occlusion/horizon culling), flight view only.
///
/// Follows <see cref="FlightAxesVisualizer"/>'s conventions: lives on the persistent
/// SASExtended_Providers GameObject, rebuilds against the active vessel each tick. Cached
/// trajectory data is stored as offsets from the vessel's own position (not absolute positions)
/// and re-anchored to the vessel's freshly re-queried current position every render frame - see
/// the field comment on <see cref="_sampleOffsets"/> for why.
/// </summary>
public class LandingPredictionManager : MonoBehaviour
{
    private static readonly ReduxLib.Logging.ILogger _LOGGER =
        ReduxLib.ReduxLib.GetLogger("SASExtended|LandingPredictionManager");

    public static LandingPredictionManager Instance { get; private set; }

    // SideToggleControl element name, wired by MainWindowController.
    public const string ToggleId = "show-landing-prediction";

    // Recompute (impact search + trajectory sampling) is throttled - it's a few hundred orbit
    // samples plus a bisection, not something to redo every frame. Marker/line positions are
    // still re-projected every frame in UpdateVisualPositions (cheap, and required for the
    // floating origin). User-configurable via Settings.LandingPredictionRefreshInterval.

    // Two-pass search: a coarse pass (over the full orbital-period-scale horizon) just brackets
    // roughly when the terrain crossing happens, then a fine pass re-integrates from scratch with
    // a step size scaled to that actual fall duration - so a several-second suborbital hop gets
    // just as smooth a rendered curve as a multi-minute coast, instead of both sharing one step
    // size sized for the longer case. Both passes use the same RK4 integrator/terrain check.
    private const int SearchSteps = 100;
    private const int MarchSteps = 200;
    private const int TrajectorySampleCount = MarchSteps + 1;
    // Bisection refinement of the final impact point within the bracketing pair of samples found
    // by MarchToImpact - see the call site for why a single linear-interpolation guess isn't
    // enough. 20 halvings shrinks the bracket by ~2^20, far finer than the visual marker's size.
    private const int ImpactBisectionIterations = 20;

    private const float LineWidthMeters = 0.6f;
    private const float MarkerRadiusMeters = 5f;
    // The line's world-space width naturally reads as thinner the farther a point is from the
    // camera (perspective) - desirable, since it reads as "receding into the distance." But a
    // fixed world-space width can shrink below what the GPU rasterizes at all once a point gets
    // far enough away, making the line vanish rather than just look thin. Floor each vertex's
    // width to whatever world size subtends this many screen pixels at that vertex's distance
    // (a little over the literal 1px floor requested, as a safety margin against AA/rounding).
    private const float MinLineWidthPixels = 1.5f;
    // Ring/crosshair line thickness for the impact marker reticle - see ReticleMarker.
    private const float MarkerLineThicknessMeters = 0.4f;
    // Colors are read from Settings every frame (not cached) so an in-game settings-page change
    // via the color picker takes effect immediately, without needing visuals to be torn down.

    private bool _enabled;
    private float _nextComputeTime;

    private VesselComponent _builtVessel;

    private bool _hasValidPrediction;

    // The trajectory as computed at the last recompute, stored as DE-ROTATED OFFSETS from the
    // vessel's own compute-time position (not absolute positions). Rendered every frame by adding
    // these offsets to the vessel's freshly re-queried CURRENT position - converting an offset via
    // VectorToPhysics is immune to whatever floating-origin re-basing PositionToPhysics does
    // between recomputes for an absolute position, which was the actual cause of the "line drifts
    // away from the vessel, then snaps back at every recompute" bug: the vessel's own on-screen
    // position is re-anchored every frame by the game, while a cached absolute PositionToPhysics
    // result was not - anchoring every rendered point to the vessel's current position directly
    // sidesteps that mismatch regardless of its exact underlying cause.
    private Vector3d[] _sampleOffsets;
    // How many leading entries of the array above are valid this recompute - impact is usually
    // found well before MarchSteps samples, and the array is fixed-size (reused every recompute)
    // rather than reallocated/resized down to the exact count.
    private int _trajectorySampleCount;
    private Vector3d _impactOffset;
    // Body-local outward surface normal at the impact point (from CelestialBodyComponent.
    // GetSurfaceNVector), used only to lay the impact reticle flat against the ground instead of
    // in some arbitrary/world-aligned orientation. Same recompute-throttled/every-frame-converted
    // treatment as _impactOffset, for the same reason (see the field comment above).
    private Vector3d _impactNormal;
    private ICoordinateSystem _predictionFrame;

    private GameObject _lineGo;
    private LineRenderer _line;
    private Material _lineMaterial;
    private Vector3[] _linePositionsBuffer;

    private GameObject _markerGo;
    private ReticleMarker _marker;

    private static VesselComponent ActiveVessel =>
        GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

    private static IPhysicsSpaceProvider PhysicsSpace =>
        GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;

    private static bool InFlightView =>
        GameManager.Instance?.Game?.GlobalGameState?.GetState() == GameState.FlightView;

    // Same physics-space flight camera the game itself renders through - used only to floor the
    // line's per-vertex width against how many world units a pixel covers at that distance.
    private static Camera FlightCamera =>
        GameManager.Instance?.Game?.CameraManager
            ?.GetCameraRenderStack(CameraID.Flight, RenderSpaceType.PhysicsSpace)
            ?.GetMainRenderCamera();

    private void Awake()
    {
        Instance = this;
        _enabled = Settings.ShowLandingPrediction.Value;
    }

    /// <summary>Called by MainWindowController from the settings-panel toggle.</summary>
    public void SetEnabled(bool on)
    {
        _enabled = on;
        _LOGGER.LogInfo($"SetEnabled: {on}");
        if (!on)
            DestroyVisuals();
    }

    private void Update()
    {
        var vessel = ActiveVessel;
        if (vessel != _builtVessel)
        {
            DestroyVisuals();
            _builtVessel = vessel;
        }

        if (!_enabled || _builtVessel == null || !InFlightView)
        {
            LogNoPrediction($"gated off (enabled={_enabled}, vessel={_builtVessel != null}, inFlightView={InFlightView}).");
            DestroyVisuals();
            return;
        }

        if (Time.time >= _nextComputeTime)
        {
            _nextComputeTime = Time.time + Settings.LandingPredictionRefreshInterval.Value;
            RecomputeTrajectory();
        }

        UpdateVisualPositions();
    }

    // "Where do I land if I cut thrust now" - a pure two-body-gravity coast to the first terrain
    // crossing, numerically integrated (RK4) forward from the orbit's live current state vector.
    // No drag/parachute model (that would need something like MechJeb's ReentrySimulation), so on
    // a body with an atmosphere this ignores atmospheric effects entirely - still rendered, since
    // it's a useful rough indicator for flights with little drag interference, but it will read
    // increasingly wrong through an actual reentry.
    //
    // Deliberately NOT sampled via PatchedConicsOrbit.GetTruePositionAtUT (analytic Kepler-anomaly
    // position reconstruction): confirmed in-game that formula is numerically unreliable for
    // near-radial/near-zero-angular-momentum orbits (eccentricity pinned near 1.000, periapsis
    // reported near the body's center) - exactly the regime a landing predictor spends most of its
    // time in. orbit.localPosition/relativeVelocity (the tracked, not reconstructed, current state)
    // don't share that failure mode, so integrate forward from there instead.
    private void RecomputeTrajectory()
    {
        _hasValidPrediction = false;

        var vessel = _builtVessel;
        if (vessel == null || vessel.LandedOrSplashed)
        {
            LogNoPrediction($"vessel null or landed/splashed (LandedOrSplashed={vessel?.LandedOrSplashed}).");
            return;
        }

        var body = vessel.mainBody;
        if (body == null || !body.hasSolidSurface)
        {
            LogNoPrediction($"body={body?.bodyName} hasSolidSurface={body?.hasSolidSurface}.");
            return;
        }

        var orbit = vessel.Orbit;
        if (orbit == null)
        {
            LogNoPrediction("vessel.Orbit is null.");
            return;
        }

        if (orbit.PeriapsisArl > body.MaxTerrainHeight)
        {
            LogNoPrediction($"periapsisArl={orbit.PeriapsisArl:F0}m > maxTerrainHeight={body.MaxTerrainHeight:F0}m - orbit never comes down.");
            return;
        }

        var universeModel = GameManager.Instance?.Game?.UniverseModel;
        if (universeModel == null)
        {
            LogNoPrediction("UniverseModel is null.");
            return;
        }

        double now = universeModel.UniverseTime;

        var frame = orbit.coordinateSystem;
        Vector3d startPos = orbit.localPosition;
        Vector3d startVel = orbit.relativeVelocity.vector;
        double mu = body.gravParameter;

        // Bound the search: a closed orbit gets at most one full period; a hyperbolic/parabolic
        // one has no period, so fall back to a fixed horizon. Computed here from the live state
        // vector via vis-viva rather than read from orbit.period/orbit.eccentricity - confirmed
        // in-game that the orbit's own analytic elements become unreliable/unstable for the
        // near-radial, near-zero-angular-momentum orbits this predictor spends a lot of time in
        // (periapsis reported at/near the body's center), which was bleeding into this search's
        // sampling resolution and causing the "first crossing found" to intermittently jump
        // between two nearby candidates (e.g. a terrain bump vs. the true final impact) between
        // recomputes despite a smoothly-evolving real trajectory.
        double specificEnergy = 0.5 * startVel.sqrMagnitude - mu / startPos.magnitude;
        double horizon;
        if (specificEnergy < 0.0)
        {
            double semiMajorAxis = -mu / (2.0 * specificEnergy);
            horizon = 2.0 * Math.PI * Math.Sqrt(semiMajorAxis * semiMajorAxis * semiMajorAxis / mu);
        }
        else
        {
            horizon = double.NaN; // parabolic/hyperbolic - no periodic bound
        }
        if (!(horizon > 0.0) || double.IsInfinity(horizon))
            horizon = 6.0 * 3600.0;
        double upperUt = now + horizon;
        // orbit.EndUT is the game's OWN patched-conic solution boundary. When that boundary is a
        // Collision transition, PatchedConics.WillCollideWithParent computed it by bisecting
        // orbit.GetRelativePositionAtUT for a terrain crossing - the exact same analytic
        // Kepler-anomaly reconstruction already shown (see the horizon comment above) to be
        // unreliable in this predictor's near-radial regime. Trusting it here clipped the search
        // horizon short of the real RK4-computed impact time on longer/higher falls, which is why
        // the whole prediction would intermittently vanish above a few thousand meters (the coarse
        // pass found "no crossing" before ever reaching the true one). Still respect EndUT for
        // transitions our own single-body integrator can't model anyway (SOI encounter/escape) -
        // just never for the game's own (unreliable) collision guess.
        if (orbit.PatchEndTransition != PatchTransitionType.Collision && orbit.EndUT > now && orbit.EndUT < upperUt)
            upperUt = orbit.EndUT;
        if (upperUt <= now)
            return;

        // The body keeps rotating under the falling vessel while we look ahead, so a future
        // sample's terrain altitude has to be checked against where the ground *will* be at that
        // time, not where it is right now - otherwise a near-zero horizontal (surface-relative)
        // velocity vessel gets predicted to land far to the side (the body's rotational speed
        // shows up as inertial-frame horizontal velocity, and only cancels out if the terrain
        // check accounts for the same rotation). AltitudeAt rotates the query position backward
        // by the angle the body sweeps forward over the elapsed time to correct for this.
        Vector3d omega = Vector.Reframed(body.relativeAngularVelocity, frame).vector;
        double omegaMag = omega.magnitude;
        Vector3d omegaAxis = omegaMag > 1e-12 ? omega / omegaMag : Vector3d.up;

        // Ground-height correction for "scenery" (KSC's runway/buildings) sitting above the raw PQS
        // terrain mesh - sampled ONCE here, at the vessel's own live position, and held constant for
        // the whole recompute. See landing_prediction_fixes.md (KSC runway rounds) for why - the
        // short version being that re-querying sceneryOffset at future/derotated points is unreliable
        // near the ground, but it's smooth/stable enough at a live position to use as-is.
        body.GetAltitudeFromTerrain(new Position(frame, startPos), out var rawTerrainAtVessel, out var sceneryOffsetAtVessel);
        double groundCorrection = -sceneryOffsetAtVessel;
        double altAtNow = rawTerrainAtVessel + groundCorrection;
        double searchDt = (upperUt - now) / SearchSteps;

        // Coarse pass: cheaply bracket roughly when the crossing happens, over the full horizon.
        bool foundCoarse = MarchToImpact(
            body, frame, omegaAxis, omegaMag, mu, startPos, startVel, altAtNow,
            searchDt, SearchSteps, null, groundCorrection,
            out _, out Vector3d coarseImpactOffset, out double coarseImpactSpan);

        if (!foundCoarse)
        {
            LogNoPrediction(
                $"no terrain crossing found within the search horizon ({upperUt - now:F0}s); " +
                $"altAtNow={altAtNow:F0}m, ecc={orbit.eccentricity:F3}, periapsisArl={orbit.PeriapsisArl:F0}m, " +
                $"apoapsisArl={orbit.ApoapsisArl:F0}m, maxTerrainHeight={body.MaxTerrainHeight:F0}m, bodyRadius={body.radius:F0}m.");
            return;
        }

        // Fine pass: re-integrate from scratch with a step size scaled to the actual predicted
        // fall duration (not the full search horizon), so the rendered curve is well-resolved
        // whether impact is seconds or many minutes away. Deliberately overshoots the coarse
        // estimate a bit: that estimate comes from linearly interpolating between two widely
        // spaced coarse samples, which tends to slightly underestimate the true crossing time for
        // a curving trajectory - marching exactly to it can finish just short of actually crossing.
        if (_sampleOffsets == null || _sampleOffsets.Length != TrajectorySampleCount)
            _sampleOffsets = new Vector3d[TrajectorySampleCount];

        double fineSpan = Math.Max(coarseImpactSpan * 1.1, coarseImpactSpan + searchDt);
        double fineDt = fineSpan > 0.0 ? fineSpan / MarchSteps : 0.0;
        bool foundFine = MarchToImpact(
            body, frame, omegaAxis, omegaMag, mu, startPos, startVel, altAtNow,
            fineDt, MarchSteps, _sampleOffsets, groundCorrection,
            out int sampleCount, out Vector3d impactOffset, out double impactSpan);

        if (!foundFine)
        {
            // Shouldn't normally happen given the margin above, but fall back to the coarse
            // pass's own (less precise, but valid) result rather than rendering a bogus marker
            // at the vessel's current position (MarchToImpact's un-updated defaults).
            impactOffset = coarseImpactOffset;
            impactSpan = coarseImpactSpan;
            LogNoPrediction($"fine pass didn't cross within {fineSpan:F1}s (coarse estimate was {coarseImpactSpan:F1}s) - using coarse result.");
        }

        _sampleOffsets[sampleCount - 1] = impactOffset; // exact, not the raw integrated sample
        _trajectorySampleCount = sampleCount;
        _impactOffset = impactOffset;
        _predictionFrame = frame;

        Vector3d impactPos = startPos + impactOffset;
        body.GetLatLonAltFromRadius(new Position(frame, impactPos), out var impactLat, out var impactLon, out _);
        _impactNormal = body.GetSurfaceNVector(impactLat, impactLon);

        _hasValidPrediction = true;

        if (Settings.VerboseLoggingEnabled.Value)
        {
            // lat/lon (2 decimals) can't resolve meter-scale wobble on a 600km-radius body -
            // log the raw local-frame numbers instead so a repeat of this can pin down whether
            // the *input* (startVel) or the *computation* (impactPos) is what's actually moving.
            Vector3d up = startPos.normalized;
            Vector3d horizontalDelta = impactOffset - up * Vector3d.Dot(impactOffset, up);
            double vertVel = Vector3d.Dot(startVel, up);
            Vector3d horizVelVec = startVel - up * vertVel;

            _LOGGER.LogDebug(
                $"Landing prediction: impact in ~{impactSpan:F1}s at lat={impactLat:F2} lon={impactLon:F2}, " +
                $"horizOffset={horizontalDelta.magnitude:F2}m, startVel(vert={vertVel * 1000.0:F1}mm/s, " +
                $"horiz={horizVelVec.magnitude * 1000.0:F1}mm/s), bodyRotationPeriod={(omegaMag > 0.0 ? 2.0 * Math.PI / omegaMag : 0.0):F0}s, " +
                $"groundCorrection={groundCorrection:F1}m.");
        }
    }

    // Integrates up to maxSteps of size dt from (startPos, startVel) looking for the first terrain
    // crossing, recording every sample - de-rotated (see Derotate) and expressed as an OFFSET from
    // startPos, not an absolute position - into `offsets` (index 0 = start, always zero) if it
    // isn't null. Returns false if no crossing is found in maxSteps * dt seconds. Storing offsets
    // rather than absolute positions is what lets rendering anchor to the vessel's current
    // position every frame instead of a cached absolute one (see the field comment on
    // _sampleOffsets).
    private static bool MarchToImpact(
        CelestialBodyComponent body, ICoordinateSystem frame, Vector3d omegaAxis, double omegaMag, double mu,
        Vector3d startPos, Vector3d startVel, double startAlt, double dt, int maxSteps,
        Vector3d[] offsets, double groundCorrection,
        out int sampleCount, out Vector3d impactOffset, out double impactSpan)
    {
        sampleCount = 1;
        if (offsets != null)
            offsets[0] = Vector3d.zero;

        impactOffset = Vector3d.zero;
        impactSpan = 0.0;

        if (startAlt <= 0.0)
            return true; // already at/under the ground this instant

        Vector3d pos = startPos, vel = startVel;
        double prevAlt = startAlt;
        Vector3d prevOffset = Vector3d.zero;

        for (int i = 1; i <= maxSteps; i++)
        {
            IntegrateStep(ref pos, ref vel, mu, dt);

            double t = i * dt;
            Vector3d derotated = Derotate(pos, omegaAxis, omegaMag, t);
            Vector3d offset = derotated - startPos;
            if (offsets != null)
                offsets[sampleCount] = offset;
            sampleCount++;

            double alt = AltitudeAt(body, frame, derotated, groundCorrection);
            if (alt <= 0.0)
            {
                // The straight-line segment between the last two samples is a fine approximation
                // of the true (continuous) path over such a short RK4 step, but terrain height is
                // NOT guaranteed to vary linearly along it - a boulder or ridge crossed between
                // the two samples (common on rubbly/asteroid-like terrain, and more likely the
                // faster the vessel's horizontal speed, since each step then covers more ground)
                // can make a single-shot linear-interpolation guess land the "impact" point on the
                // far side of that feature, embedded underground, instead of right at its surface.
                // Bisect along the segment instead, re-querying the actual terrain altitude at
                // each candidate, so the final point converges onto wherever the surface actually
                // is regardless of how it varies between the two samples.
                Vector3d hiOffset = offset;
                double loAlt = prevAlt, hiAlt = alt;
                double loFrac = 0.0, hiFrac = 1.0;
                for (int bisect = 0; bisect < ImpactBisectionIterations; bisect++)
                {
                    double frac = loFrac + (hiFrac - loFrac) * (loAlt / (loAlt - hiAlt));
                    frac = Math.Min(Math.Max(frac, loFrac + 1e-6), hiFrac - 1e-6);
                    Vector3d candidateOffset = Vector3d.Lerp(prevOffset, offset, frac);
                    double candidateAlt = AltitudeAt(body, frame, startPos + candidateOffset, groundCorrection);
                    if (candidateAlt > 0.0)
                    {
                        loAlt = candidateAlt;
                        loFrac = frac;
                    }
                    else
                    {
                        hiOffset = candidateOffset;
                        hiAlt = candidateAlt;
                        hiFrac = frac;
                    }
                }
                // The hi side is always at-or-under the surface, so ending there (rather than the
                // midpoint) never renders the marker floating visibly above the ground.
                impactOffset = hiOffset;
                impactSpan = (i - 1 + hiFrac) * dt;
                return true;
            }

            prevAlt = alt;
            prevOffset = offset;
        }

        return false;
    }

    // Classical two-body (pure gravity, no drag) RK4 step - airless bodies only, see the plan.
    private static void IntegrateStep(ref Vector3d pos, ref Vector3d vel, double mu, double dt)
    {
        Vector3d k1V = Acceleration(pos, mu);
        Vector3d k1X = vel;

        Vector3d k2X = vel + k1V * (dt * 0.5);
        Vector3d k2V = Acceleration(pos + k1X * (dt * 0.5), mu);

        Vector3d k3X = vel + k2V * (dt * 0.5);
        Vector3d k3V = Acceleration(pos + k2X * (dt * 0.5), mu);

        Vector3d k4X = vel + k3V * dt;
        Vector3d k4V = Acceleration(pos + k3X * dt, mu);

        pos += (dt / 6.0) * (k1X + 2.0 * k2X + 2.0 * k3X + k4X);
        vel += (dt / 6.0) * (k1V + 2.0 * k2V + 2.0 * k3V + k4V);
    }

    private static Vector3d Acceleration(Vector3d pos, double mu)
    {
        double r = pos.magnitude;
        return pos * (-mu / (r * r * r));
    }

    // Rotates a future inertial-frame position backward by the angle the body will have swept
    // forward over `elapsedTime` seconds - i.e. "where would this point be if the ground hadn't
    // moved since now". GetAltitudeFromTerrain (and rendering the point at all, via the current
    // floating-origin conversion) only ever reflects the body's *current* orientation, so both the
    // terrain check and anything stored for display need this correction applied consistently -
    // otherwise a vessel with near-zero horizontal (surface-relative) velocity gets predicted to
    // land far to the side, since the body's own rotation shows up as inertial-frame horizontal
    // velocity that only cancels out if every future position/check shares this same correction.
    private static Vector3d Derotate(Vector3d localPos, Vector3d omegaAxis, double omegaMag, double elapsedTime)
    {
        if (omegaMag <= 0.0 || elapsedTime == 0.0)
            return localPos;

        var backRotation = QuaternionD.AngleAxis(-omegaMag * elapsedTime * (180.0 / Math.PI), omegaAxis);
        return backRotation * localPos;
    }

    // groundCorrection covers the gap between the raw PQS terrain mesh and "scenery" (KSC's runway/
    // buildings) - see its computation in RecomputeTrajectory and landing_prediction_fixes.md.
    private static double AltitudeAt(CelestialBodyComponent body, ICoordinateSystem frame, Vector3d derotatedPos, double groundCorrection)
    {
        body.GetAltitudeFromTerrain(new Position(frame, derotatedPos), out var terrainAltitude, out _);
        return terrainAltitude + groundCorrection;
    }

    // Gated behind VerboseLoggingEnabled - this runs every throttled recompute (every ~0.2s while
    // the toggle is on), so it would spam the log otherwise. Time-throttled rather than deduped by
    // content, since the diagnostic messages carry continuously-varying numbers (altitude, period)
    // that would defeat a content-based dedup.
    private float _lastNoPredictionLogTime = float.NegativeInfinity;
    private const float NoPredictionLogIntervalSeconds = 2f;

    private void LogNoPrediction(string reason)
    {
        if (!Settings.VerboseLoggingEnabled.Value || Time.time < _lastNoPredictionLogTime + NoPredictionLogIntervalSeconds)
            return;
        _lastNoPredictionLogTime = Time.time;
        _LOGGER.LogDebug($"No landing prediction: {reason}");
    }

    private void UpdateVisualPositions()
    {
        if (!_hasValidPrediction)
        {
            DestroyVisuals();
            return;
        }

        CreateVisuals();

        var physics = PhysicsSpace;
        if (physics == null)
            return;

        var orbit = _builtVessel?.Orbit;
        if (orbit == null)
            return;

        // Anchor every rendered point to the vessel's CURRENT position, re-queried fresh every
        // frame, rather than converting a cached absolute position - see the field comment on
        // _sampleOffsets for why. VectorToPhysics converts the (fixed-shape) offset, which is
        // unaffected by whatever origin-tracking difference caused the drift.
        Vector3d vesselWorldNow = physics.PositionToPhysics(new Position(_predictionFrame, orbit.localPosition));

        var cam = FlightCamera;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;

        var widthKeys = new Keyframe[_trajectorySampleCount];
        for (int i = 0; i < _trajectorySampleCount; i++)
        {
            Vector3d offsetWorld = physics.VectorToPhysics(new Vector(_predictionFrame, _sampleOffsets[i]));
            Vector3 point = vesselWorldNow + offsetWorld;
            _linePositionsBuffer[i] = point;
            float width = cam != null
                ? Mathf.Max(LineWidthMeters, PixelWorldSize(cam, camPos, point) * MinLineWidthPixels)
                : LineWidthMeters;
            float t = _trajectorySampleCount > 1 ? (float)i / (_trajectorySampleCount - 1) : 0f;
            widthKeys[i] = new Keyframe(t, width);
        }
        _line.positionCount = _trajectorySampleCount;
        _line.SetPositions(_linePositionsBuffer);
        // LineRenderer has no per-vertex SetWidths in this Unity version - widthCurve is sampled
        // at each vertex's normalized position (0 at the first vertex, 1 at the last), so one
        // keyframe per vertex gives an exact per-vertex width just like SetWidths would.
        _line.widthMultiplier = 1f;
        _line.widthCurve = new AnimationCurve(widthKeys);
        // Read every frame (not cached) so a color-picker change in the settings menu applies
        // immediately - see the field comment above the (now-removed) color constants.
        var lineColor = Settings.LandingPredictionLineColor.Value;
        _line.startColor = lineColor;
        _line.endColor = lineColor;
        _lineMaterial.color = lineColor;

        Vector3d impactOffsetWorld = physics.VectorToPhysics(new Vector(_predictionFrame, _impactOffset));
        Vector3d impactNormalWorld = physics.VectorToPhysics(new Vector(_predictionFrame, _impactNormal));
        _marker.Center = vesselWorldNow + impactOffsetWorld;
        _marker.Normal = ((Vector3)impactNormalWorld).normalized;
        _marker.Color = Settings.LandingPredictionMarkerColor.Value;
    }

    private void CreateVisuals()
    {
        if (_lineGo == null)
        {
            _lineGo = new GameObject("SASX_LandingTrajectory");
            _lineGo.transform.parent = transform;

            _line = _lineGo.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            // Per-vertex widths and color are set every frame in UpdateVisualPositions - no need
            // for static start/end values here.
            _line.material = CreateOverlayMaterial(Settings.LandingPredictionLineColor.Value, out _lineMaterial);

            _linePositionsBuffer = new Vector3[TrajectorySampleCount];
        }

        if (_markerGo == null)
        {
            _markerGo = new GameObject("SASX_LandingMarker");
            _markerGo.transform.parent = transform;
            _marker = _markerGo.AddComponent<ReticleMarker>();
            _marker.Radius = MarkerRadiusMeters;
            _marker.LineThickness = MarkerLineThicknessMeters;
        }
    }

    // Draws the impact marker as a flat crosshair-in-circle reticle laid against the ground
    // (oriented to the local terrain normal), using the Shapes library's immediate-mode API
    // directly - the same one the game's own DebugShapesDraw wraps for the sphere/line/cuboid
    // primitives it exposes, none of which fit a flat ground reticle. Kept as its own component
    // (rather than nested drawing calls in UpdateVisualPositions) because Shapes draws are only
    // valid from within a Camera.onPreRender callback, which KerbalImmediateModeShapeDrawer
    // (the game's own base class for this) already wires up.
    private class ReticleMarker : KerbalImmediateModeShapeDrawer
    {
        public Vector3 Center { get; set; }
        public Vector3 Normal { get; set; } = Vector3.up;
        public float Radius { get; set; } = 5f;
        public float LineThickness { get; set; } = 0.4f;
        public Color Color { get; set; } = Color.white;

        public override void DrawShapes(Camera cam)
        {
            using (Draw.Command(cam))
            {
                Draw.ZTest = CompareFunction.Always;
                Draw.BlendMode = ShapesBlendMode.Additive;
                Draw.ThicknessSpace = ThicknessSpace.Meters;
                Draw.Ring(Center, Normal, Radius, LineThickness, Color);

                // Arbitrary (but stable frame-to-frame) in-plane basis perpendicular to Normal -
                // a crosshair reads the same regardless of which way it's rotated about Normal.
                var rot = Quaternion.FromToRotation(Vector3.up, Normal);
                Vector3 tangent = rot * Vector3.right;
                Vector3 bitangent = rot * Vector3.forward;
                Draw.Line(Center - tangent * Radius, Center + tangent * Radius, LineThickness, LineEndCap.None, Color);
                Draw.Line(Center - bitangent * Radius, Center + bitangent * Radius, LineThickness, LineEndCap.None, Color);
            }
        }
    }

    // World-space size (in meters) that one screen pixel covers at `point`, for the given camera.
    // Multiplying by a pixel count gives the minimum line width that still rasterizes at that
    // distance - see MinLineWidthPixels.
    private static float PixelWorldSize(Camera cam, Vector3 camPos, Vector3 point)
    {
        int screenHeight = Mathf.Max(1, Screen.height);
        if (cam.orthographic)
            return cam.orthographicSize * 2f / screenHeight;

        float distance = Vector3.Distance(camPos, point);
        float worldHeightAtDistance = 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        return worldHeightAtDistance / screenHeight;
    }

    // Always-on-top recipe (no depth occlusion/horizon culling - see the plan) confirmed already
    // in use by Orbital Survey's GroundTrackingRenderer.
    private static Material CreateOverlayMaterial(Color color, out Material outMat)
    {
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        outMat = new Material(shader);
        outMat.color = color;
        outMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        outMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        return outMat;
    }

    private void DestroyVisuals()
    {
        if (_lineGo != null)
            Destroy(_lineGo);
        _lineGo = null;
        _line = null;

        if (_lineMaterial != null)
            Destroy(_lineMaterial);
        _lineMaterial = null;

        if (_markerGo != null)
            Destroy(_markerGo);
        _markerGo = null;
        _marker = null;

        _hasValidPrediction = false;
    }

    private void OnDestroy()
    {
        DestroyVisuals();
        if (Instance == this)
            Instance = null;
    }
}
}
