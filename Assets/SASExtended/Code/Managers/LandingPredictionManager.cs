using System;
using KSP.Api;
using KSP.Game;
using KSP.Rendering;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.PureMath;
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

    private const float LineWidthMeters = 0.6f;
    private const float MarkerRadiusMeters = 5f;
    // Floor each vertex's width to whatever world size subtends this many screen pixels at that
    // vertex's distance, so perspective thinning is preserved but the line never rasterizes below
    // visibility - see landing_prediction_fixes.md round 11.
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
    // VectorToPhysics is immune to whatever floating-origin re-basing PositionToPhysics does to an
    // absolute position between recomputes. See landing_prediction_fixes.md rounds 9-10 for the
    // floating-origin drift bug this avoids - don't "simplify" back to caching absolute positions.
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

    // Reused across frames instead of allocating a fresh Keyframe[] + AnimationCurve every render
    // frame (widths themselves DO need recomputing every frame - PixelWorldSize depends on
    // camera-to-vertex distance, which changes continuously - but the curve object and its backing
    // array don't need to). _widthCurve's keys are updated in place via MoveKey each frame, then
    // still has to be reassigned to _line.widthCurve every frame regardless - LineRenderer copies a
    // curve's keyframe data on assignment rather than keeping a live reference to it, so mutating
    // this object alone doesn't reach the renderer. _widthCurveKeyCount tracks how many keys it
    // currently has, so a change in _trajectorySampleCount (only happens right after a recompute,
    // not every frame - see its field comment) is the one case that still reallocates, rebuilding
    // the curve to the new key count.
    private AnimationCurve _widthCurve;
    private int _widthCurveKeyCount;

    private GameObject _markerGo;
    private ReticleMarker _marker;

    // Tracks whether _lineGo/_markerGo currently exist, so DestroyVisuals() can no-op instead of
    // running its teardown (several null checks + a field reset) every single frame while gated
    // off/no valid prediction - both are common per-frame steady states (the feature is disabled by
    // default, and UpdateVisualPositions calls DestroyVisuals() every frame a recompute doesn't
    // currently have a valid impact), not one-off events.
    private bool _visualsDestroyed = true;

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

        // Read once rather than once in the condition and again inside the log string below (the
        // string used to re-evaluate it a second time whenever this branch was actually taken).
        bool inFlightView = InFlightView;
        if (!_enabled || _builtVessel == null || !inFlightView)
        {
            // This branch runs every single frame for the (default, disabled) common case - guard
            // the interpolated string with the same gate LogNoPrediction applies internally, so it's
            // never actually built unless something would be logged. See ShouldLogNoPrediction.
            if (ShouldLogNoPrediction)
                LogNoPrediction($"gated off (enabled={_enabled}, vessel={_builtVessel != null}, inFlightView={inFlightView}).");
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
    // position reconstruction) - unreliable for the near-radial, near-zero-angular-momentum orbits
    // this predictor always deals with (see landing_prediction_fixes.md round 1). Integrates forward
    // from the live tracked state vector (orbit.localPosition/relativeVelocity) instead.
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
        // vector via vis-viva rather than read from orbit.period/orbit.eccentricity, which are
        // unreliable for this predictor's near-radial orbits - see landing_prediction_fixes.md
        // round 8.
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
        // orbit.EndUT is the game's OWN patched-conic solution boundary, but for a Collision
        // transition it comes from the same unreliable Kepler-anomaly reconstruction as above - see
        // landing_prediction_fixes.md round 12. Still respect EndUT for transitions this integrator
        // can't model anyway (SOI encounter/escape), just never the game's own collision guess.
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
        // the whole recompute (re-querying sceneryOffset at future/derotated points is unreliable
        // near the ground, but it's smooth/stable enough at a live position). See
        // landing_prediction_fixes.md rounds 14-16.
        body.GetAltitudeFromTerrain(new Position(frame, startPos), out var rawTerrainAtVessel, out var sceneryOffsetAtVessel);
        double groundCorrection = -sceneryOffsetAtVessel;
        double altAtNow = rawTerrainAtVessel + groundCorrection;
        double searchDt = (upperUt - now) / SearchSteps;

        // The only game-coupled piece of the coarse/fine marches below - everything else
        // (LandingPredictionMath.MarchToImpact/IntegrateStep/Acceleration/Derotate) is pure
        // Vector3d/QuaternionD math with no live ICoordinateSystem, so it lives in PureMath/ and is
        // covered by LandingPredictionMathTests instead of only being verifiable in-game - see
        // code_review_2026-07-17.md #10.
        double AltitudeAt(Vector3d derotatedPos)
        {
            body.GetAltitudeFromTerrain(new Position(frame, derotatedPos), out var terrainAltitude, out _);
            return terrainAltitude + groundCorrection;
        }

        // Coarse pass: cheaply bracket roughly when the crossing happens, over the full horizon.
        bool foundCoarse = LandingPredictionMath.MarchToImpact(
            AltitudeAt, omegaAxis, omegaMag, mu, startPos, startVel, altAtNow,
            searchDt, SearchSteps, null,
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
        bool foundFine = LandingPredictionMath.MarchToImpact(
            AltitudeAt, omegaAxis, omegaMag, mu, startPos, startVel, altAtNow,
            fineDt, MarchSteps, _sampleOffsets,
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
            // log the raw local-frame numbers instead.
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

    // Gated behind VerboseLoggingEnabled - this runs every throttled recompute (every ~0.2s while
    // the toggle is on), so it would spam the log otherwise. Time-throttled rather than deduped by
    // content, since the diagnostic messages carry continuously-varying numbers (altitude, period)
    // that would defeat a content-based dedup.
    private float _lastNoPredictionLogTime = float.NegativeInfinity;
    private const float NoPredictionLogIntervalSeconds = 2f;

    // The same gate LogNoPrediction applies internally, exposed so a caller about to build an
    // interpolated diagnostic string (a heap allocation) can skip that work entirely when nothing
    // would be logged anyway - see the call site in Update(), which runs every single frame for
    // every player who leaves the feature at its default (disabled) setting. Callers passing a
    // plain string literal (free to construct) don't need this - just call LogNoPrediction
    // directly, which still applies this same gate on its own.
    private bool ShouldLogNoPrediction =>
        Settings.VerboseLoggingEnabled.Value && Time.time >= _lastNoPredictionLogTime + NoPredictionLogIntervalSeconds;

    private void LogNoPrediction(string reason)
    {
        if (!ShouldLogNoPrediction)
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

        // Rebuild (reallocate) only when the key count actually changes - i.e. right after a
        // recompute changes _trajectorySampleCount, not every frame. See _widthCurve's field
        // comment.
        if (_widthCurve == null || _widthCurveKeyCount != _trajectorySampleCount)
        {
            var widthKeys = new Keyframe[_trajectorySampleCount];
            for (int i = 0; i < _trajectorySampleCount; i++)
            {
                float t = _trajectorySampleCount > 1 ? (float)i / (_trajectorySampleCount - 1) : 0f;
                widthKeys[i] = new Keyframe(t, 0f); // width filled in by the MoveKey loop below
            }
            // Not assigned to _line.widthCurve here - the unconditional assignment after the loop
            // below runs every frame regardless (see the comment there) and covers this case too.
            _widthCurve = new AnimationCurve(widthKeys);
            _widthCurveKeyCount = _trajectorySampleCount;
        }

        for (int i = 0; i < _trajectorySampleCount; i++)
        {
            Vector3d offsetWorld = physics.VectorToPhysics(new Vector(_predictionFrame, _sampleOffsets[i]));
            Vector3 point = vesselWorldNow + offsetWorld;
            _linePositionsBuffer[i] = point;
            // Distance-to-camera (and so this width) genuinely does change every frame as the
            // camera/vessel move - only the curve's key COUNT is stable between recomputes, which
            // is what the rebuild check above skips reallocating on.
            float width = cam != null
                ? Mathf.Max(LineWidthMeters, PixelWorldSize(cam, camPos, point) * MinLineWidthPixels)
                : LineWidthMeters;
            float t = _trajectorySampleCount > 1 ? (float)i / (_trajectorySampleCount - 1) : 0f;
            _widthCurve.MoveKey(i, new Keyframe(t, width));
        }
        _line.positionCount = _trajectorySampleCount;
        _line.SetPositions(_linePositionsBuffer);
        // LineRenderer has no per-vertex SetWidths in this Unity version - widthCurve is sampled
        // at each vertex's normalized position (0 at the first vertex, 1 at the last), so one
        // keyframe per vertex gives an exact per-vertex width just like SetWidths would.
        // LineRenderer.widthCurve's setter copies the curve's keyframe data rather than keeping a
        // live reference to the AnimationCurve object - mutating _widthCurve via MoveKey above does
        // NOT by itself update what the renderer draws, so it still has to be reassigned every frame
        // (confirmed in-game: without this, the line rendered at its placeholder 0-width forever).
        // What the caching above actually saves is the Keyframe[]/AnimationCurve allocation, not
        // this assignment.
        _line.widthCurve = _widthCurve;
        _line.widthMultiplier = 1f;
        // Read every frame (not cached) so a color-picker change in the settings menu applies
        // immediately.
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
        _visualsDestroyed = false;

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
        // Nothing to do if already torn down (or never created) - _hasValidPrediction is
        // already false whenever _visualsDestroyed is true (it's only ever set true again by
        // RecomputeTrajectory, and CreateVisuals - which flips _visualsDestroyed back to false -
        // always runs in the same UpdateVisualPositions call before DestroyVisuals could next see
        // it), so there's nothing left below worth doing.
        if (_visualsDestroyed)
            return;

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

        // _line is gone (destroyed above), so the curve it referenced needs rebuilding against
        // whatever new LineRenderer CreateVisuals hands out next time - otherwise the rebuild check
        // in UpdateVisualPositions could see a non-null _widthCurve with a matching key count and
        // skip reassigning it to the new _line entirely, leaving the new LineRenderer on its
        // default (unset) widthCurve.
        _widthCurve = null;
        _widthCurveKeyCount = 0;

        _hasValidPrediction = false;
        _visualsDestroyed = true;
    }

    private void OnDestroy()
    {
        DestroyVisuals();
        if (Instance == this)
            Instance = null;
    }
}
}
