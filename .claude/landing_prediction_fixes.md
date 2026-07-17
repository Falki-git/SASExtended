# Landing prediction visuals — fix investigation log

Round-by-round log of the Landing Prediction (`LandingPredictionManager`) debugging on branch
`feature/landing-prediction-visuals`, 2026-07-15. Referenced from
[`mod_specifics.md`](mod_specifics.md) § Landing prediction visuals. All fixes are in
`Assets/SASExtended/Code/Managers/LandingPredictionManager.cs` unless noted. Kept in its own file
since this level of detail isn't needed for day-to-day mod work — read it only when touching this
prediction/rendering pipeline again, especially before "simplifying" the offset-based rendering
scheme below; every earlier, more obvious-looking approach was tried and demonstrably broken.

## Round-by-round log

**Round 1 — catastrophic altitude error (100x) on near-radial orbits.** The original implementation
sampled future positions via `PatchedConicsOrbit.GetTruePositionAtUT` (analytic Kepler
eccentric/true-anomaly reconstruction). Confirmed via a real in-game test: `altAtNow` (the position
at `t=0`, no extrapolation at all) was off by ~100x (60,451,005m vs. an apoapsis of only 22,251m),
with `eccentricity` pinned near 1.000 and `PeriapsisArl` reported at/near the body's *center* — the
classic near-radial/near-zero-angular-momentum degenerate case that breaks anomaly-based Kepler
solvers, and exactly the regime a landing predictor spends most of its time in (falling almost
straight down has ~zero angular momentum relative to the body). **Fix:** switched to numerically
integrating (RK4, pure two-body gravity) forward from the orbit's *live tracked* state vector
(`orbit.localPosition` / `orbit.relativeVelocity`, physics-tracked every tick, not reconstructed
from anomaly), which doesn't share that failure mode.

**Round 2 — marker snapping to the vessel's own position instead of the trajectory's end.** After
switching to a two-pass coarse-then-fine RK4 search (coarse pass roughly brackets the crossing time
over the full horizon; fine pass re-integrates at a step size scaled to that estimate for a smooth
render), the *fine* pass would sometimes finish all its steps without ever finding `alt <= 0`,
because the coarse pass's linearly-interpolated crossing-time estimate is systematically biased to
slightly *underestimate* the true crossing for a curving trajectory — marching exactly to that
estimate can land just short of actually crossing. `MarchToImpact`'s "not found" return value was
being silently ignored, so `impactPos` fell back to its un-updated default (the vessel's own
position) — a straight line from the trajectory's true (never-detected) end back to the vessel.
**Fix:** give the fine pass a deliberate margin beyond the coarse estimate
(`max(coarse * 1.1, coarse + searchDt)`), and if it still somehow doesn't find a crossing, fall back
to the coarse pass's own (less precise but valid) result instead of the vessel's position.

**Round 3 — large sideways offset predicted for a vessel with ~zero horizontal velocity.** The body
keeps rotating while the integrator looks ahead, but `GetAltitudeFromTerrain` is a snapshot-*now*
query with no concept of a future time — so a future sample's terrain check was being evaluated
against where the ground *currently* is, not where it *will be* when the vessel gets there. A vessel
with ~zero real (surface-relative) horizontal velocity still has substantial *inertial*-frame
horizontal velocity (equal to the body's own rotation speed at that latitude), so without this
correction the predicted impact swings far to the side. **Fix:** added `Derotate` — rotate the query
position backward by the angle the body will have swept forward by the sample's elapsed time —
before every terrain-altitude check.

**Round 4 — jagged/blocky rendered line.** The march step size was sized to the *entire* search
horizon (up to a full orbital period, potentially hundreds of seconds), so a short suborbital hop
(actual fall time of only a few seconds) got only a handful of samples. **Fix:** the two-pass design
from round 2 already addresses this — the fine pass's step size is scaled to the *actual* predicted
fall duration, not the full search horizon, so a 5-second hop and a 10-minute coast both get the
same ~200-sample resolution.

**Round 5 — the round-3 fix wasn't applied to the rendered points, only the internal check.** Fixing
the terrain-*check* wasn't enough — the *stored/rendered* trajectory samples and impact marker were
still the raw, un-rotated positions, so the rendered line still showed a large sideways offset even
though the impact-detection logic itself was now correct. **Fix:** apply the same `Derotate`
correction to every sample stored for rendering, not just the altitude-check query. This produced a
correct baseline (line pointing straight down for near-zero velocity) but introduced a new,
precisely-timed "drift sideways over ~0.5s, then snap back" oscillation in the rendered line —
rounds 6–10 below are that investigation. Verified via `Player.log` at every step of that
investigation: the diagnostic fields added to the verbose log (`horizOffset`, raw `startVel`
components) stayed **perfectly smooth** throughout — the recomputed data was never the problem.

**Round 6 — tried re-deriving the rotation correction every render frame.** Hypothesis: the
correction baked in once at compute time (round 5) goes stale as real time passes within each 0.2s
recompute window, since the body keeps rotating. Tried re-applying `Derotate` every frame using
`elapsedSinceRecompute`. **No change** — ruled out staleness of the rotation-angle *value* as the
cause.

**Round 7 — tried removing rendering-side de-rotation entirely.** Decompiled
`IPhysicsSpaceProvider.PositionToPhysics` and found it's literally
`InertialFrame.ToLocalPosition(position)` — a conversion between two **inertial** (non-rotating)
frames only, with no rotation awareness. Hypothesized the rotating ground must be a separately-
animating mesh within that fixed inertial space, so a raw (un-rotated) ballistic path should already
visually track the rotating ground on its own, making the round-5 fix redundant/wrong. **This was
wrong**: removing rendering-side de-rotation brought back the round-3 sideways-offset bug in full,
proving de-rotation genuinely is needed for correct baseline geometry — and the oscillation was
still present regardless, proving de-rotation was never the oscillation's cause either way.

**Round 8 — replaced the search horizon's dependency on native orbital elements.** While chasing the
oscillation, noticed the coarse/fine search horizon was computed from `orbit.period`/
`orbit.eccentricity` — the same class of native "analytic orbital element" that round 1 had already
shown to be unreliable for near-radial orbits (which this predictor is *always* dealing with, by
definition — you're never in this code unless the vessel is heading toward the ground). **Fix:**
compute the horizon directly from the live state vector via vis-viva
(`specificEnergy = v²/2 - mu/r`, then `T = 2π√(a³/mu)` for a bound orbit), removing the dependency on
`orbit.period`/`orbit.eccentricity` entirely. Good robustness improvement, but subsequent `Player.log`
analysis showed the pre-fix values were already stable in the tested scenarios — this did not turn
out to be the oscillation's root cause either.

**Round 9 — found the actual root cause: floating-origin drift on cached absolute positions.** With
compute-time data confirmed stable via logging (round 5's note), and the user confirming the *vessel
itself* followed a "perfect physics trajectory" while only the *rendered line* oscillated, the bug
had to be in how cached data gets converted for rendering. `UpdateVisualPositions` runs every render
frame (not throttled to the 0.2s recompute), converting the *same cached absolute positions* via
`PositionToPhysics` every time. The vessel's own on-screen position is continuously re-anchored by
the game's floating-origin system every frame; the cached absolute samples were frozen at the last
recompute and did not participate in that continuous re-anchoring. Between recomputes the frozen
points drift out of sync with wherever the floating origin currently is, then snap back into
alignment the instant a fresh recompute re-baselines everything — a sawtooth precisely timed to the
0.2s recompute interval, exactly matching the reported symptom (and exactly the user's own working
hypothesis: "as if the drift is happening because of planet's movement... snap it back with new
recalculation").

**Round 10 — the fix: anchor to the vessel's live position, render offsets not absolute positions.**
Changed `MarchToImpact`/`RecomputeTrajectory` to store every sample and the impact point as a
**de-rotated offset vector relative to the vessel's own position at compute time**
(`_sampleOffsets`/`_impactOffset`), not an absolute position. `UpdateVisualPositions` now, every
frame: (1) re-queries the vessel's *current* position fresh (`vessel.Orbit.localPosition`, not
cached) and converts *that* through `PositionToPhysics`; (2) converts each cached offset through
`physics.VectorToPhysics` (a vector conversion — direction+magnitude only, no absolute anchor, so
unaffected by whatever the floating origin does between recomputes); (3) adds them together. This
guarantees the line's start point always exactly coincides with wherever the game renders the
vessel this frame, regardless of the floating origin's exact re-anchoring behavior. **Confirmed
fixed in-game** — smooth curve, correct baseline for near-zero horizontal velocity, no drift/snap.

**Round 11 — trajectory line fades to invisible at distance.** Unity's `LineRenderer` draws width
in fixed world-space units; perspective naturally (and desirably) makes a fixed-width line read
thinner farther from the camera, but once a point gets far enough away its world-space width
subtends less than one screen pixel and the GPU rasterizer drops that geometry outright instead of
rendering it merely faint. Confirmed by decompiling the actual installed Unity Editor's own
`UnityEngine.CoreModule.dll` that this Unity version's `LineRenderer` has no per-vertex `SetWidths`
method, only `widthCurve`. **Fix:** rebuild a `widthCurve` every frame (one `Keyframe` per vertex)
that floors each vertex's width to whatever world size subtends `MinLineWidthPixels` (1.5) screen
pixels at that vertex's actual distance from the flight camera
(`GameManager...CameraManager.GetCameraRenderStack(CameraID.Flight,
RenderSpaceType.PhysicsSpace).GetMainRenderCamera()`), so perspective thinning is preserved but the
line never rasterizes below the visibility floor. **Confirmed fixed in-game.**

**Round 12 — trajectory line/marker vanish entirely above a few thousand meters altitude.** The
coarse search's horizon was being clamped to `orbit.EndUT` whenever the orbit's own
`PatchEndTransition` was `Collision` - but that value comes from
`PatchedConics.WillCollideWithParent`, which bisects the SAME analytic Kepler-anomaly
reconstruction (`orbit.GetRelativePositionAtUT`) already shown unreliable in this predictor's
near-radial regime (round 1). For a higher/longer fall, that native estimate under-ran the true
RK4-computed impact time, clipping the search horizon short so the coarse pass reported "no
crossing found" before ever reaching the real one - a total disappearance of both line and marker.
**Fix:** only respect `orbit.EndUT` as a horizon bound for transitions the single-body integrator
genuinely can't model itself (SOI encounter/escape), never for the game's own (unreliable)
`Collision` guess. **Confirmed fixed in-game.**

**Round 13 — impact marker occasionally rendering underground on rugged/bouldery terrain.**
`MarchToImpact` estimated the exact impact point by linearly interpolating between the last two
RK4 samples' altitude values, implicitly assuming terrain height varies linearly between them. On
rugged (rubbly/asteroid-like) terrain with meaningful horizontal velocity, a single fine-pass step
can cover enough ground to skip over a boulder or ridge between the two samples, landing the
linear-interpolation guess on the far side of that feature - embedded underground instead of at its
true surface. **Fix:** replaced the single linear guess with a 20-iteration (`ImpactBisectionIterations`)
bisection that re-queries the actual terrain altitude at each candidate point along the segment,
converging onto wherever the surface genuinely is regardless of how it varies between the two
samples. **Confirmed fixed in-game.**

**Round 14 — KSC runway impact marker rendered underground; `GetAltitudeFromTerrain`'s
`sceneryOffset` out-param wasn't accounted for at all.** `GetAltitudeFromTerrain` returns
`terrainAltitude` (height above the raw PQS terrain mesh) and a separate `sceneryOffset` for
built structures (KSC's runway/buildings) sitting above/instead of that raw mesh — the code was
discarding `sceneryOffset` entirely (`out _`), so the impact search converged on raw-terrain-zero
instead of the real (much higher) runway surface. Two sign attempts both failed in-game: adding
`sceneryOffset` (wrong), then subtracting it after decompiling `KSP.Sim.impl.TelemetryComponent`
and confirming the game's own `AltitudeFromScenery = terrainAltitude - sceneryOffset` formula
(right formula, still didn't fix it). Added logging at the user's request revealed why: queried at
future/derotated march-search positions, `sceneryOffset` held a rock-solid ~8.9m while comfortably
above the surface, then fell to **exactly 0** right as the query point neared/crossed it — silently
corrupting exactly the samples closest to touchdown regardless of which sign was used. A "cache the
last known-good nonzero reading" heuristic (threaded via `ref` through the whole search) was tried
next and also didn't help — **reverted**, since the true offset turned out to vary too much by
location (a separate debug overlay measured ~277m at one runway spot vs. the ~8.9m logged at
another) for a single cached "last good value" to reliably apply near touchdown either.

**Round 15 — `Physics.Raycast`-based ground truth: fixed the underground marker, but introduced
new problems.** Real collision geometry (layers `Physx.Terrain`/`Physx.Scenery`/`Local.Terrain`/
`Local.Scenery`/`Internal.Scenery`/`KSCBuildings`, from `ProjectSettings/TagManager.asset`) is
ground truth for "what would the vessel actually land on," sidestepping `GetAltitudeFromTerrain`'s
scenery handling entirely. Cast one ray per recompute, straight down through the coarse pass's
rough impact estimate (generous margin/max-distance since that rough estimate could itself be
hundreds of meters wrong), and derived a constant `groundCorrection` from how far off raw
`terrainAltitude` read at the real hit point. **This worked** — confirmed in-game — but the user
flagged two side effects: a performance concern about raycasting every recompute, and a visible
"twitching" in the rendered marker, since the raycast's origin depended on the coarse pass's
impact estimate, which shifts slightly between recomputes even for an otherwise-steady vessel.

**Round 16 — the actual fix: sample `GetAltitudeFromTerrain` once at the vessel's own LIVE
position, not the raycast.** Added extensive comparison logging (vessel's own official telemetry —
`AltitudeFromTerrain`/`AltitudeFromScenery`/`AltitudeFromSurface`/`AltitudeFromRadius` — vs. our own
`GetAltitudeFromTerrain` calls at the vessel's position, at the coarse pass's rough impact point,
and the round-15 raycast result) and had the user fly a structured test: a fixed-altitude hover,
then a slow horizontal pass along the runway, then a slow vertical descent to touchdown, all with
verbose logging on. The `Player.log` data showed: (1) `sceneryOffset` queried at the vessel's own
*real* current position **never once collapsed to 0** across 1321 logged samples, including all the
way down to ~2m above the true ground during the slow descent — only future/derotated *hypothetical*
query positions ever showed that collapse (round 14); (2) that value tracked the round-15 raycast's
answer closely even at the largest lateral distance in the test (~250m: worst-case ~20m difference,
mean ~6.5m, against a ~230–280m correction — a small relative error). **Fix:** removed the
`Physics.Raycast` entirely; `groundCorrection` is now `-sceneryOffset` from a single
`GetAltitudeFromTerrain` call at the vessel's live `startPos`, held constant across the coarse pass,
fine pass, and bisection for that recompute. Cheaper (no raycast, no collider-streaming dependency
for a distant predicted impact point), and the twitching is gone since the vessel's own real
position changes smoothly frame to frame, unlike the coarse pass's search-derived estimate.
**Confirmed fixed in-game.**

## Takeaways for next time

- **`GetAltitudeFromTerrain`'s `sceneryOffset` is only reliable queried at a real, live position -
  not at a hypothetical future/derotated one.** It silently collapses to 0 once a future-position
  query point gets within a few meters of the true surface (rounds 14-16), but never showed that
  failure sampled at the vessel's own current position, even down to ~2m of true altitude. Sample it
  ONCE per recompute at a live position and hold it constant rather than re-querying it at every
  march/bisection sample.
- **Never trust `orbit.period`/`orbit.eccentricity`/`GetTruePositionAtUT` in this code.** This
  predictor lives in the near-radial, near-zero-angular-momentum regime by definition, and every
  native "analytic orbit element" property has turned out to be unreliable there at least once.
  Prefer the live tracked state vector (`orbit.localPosition`/`orbit.relativeVelocity`) plus your own
  vis-viva/RK4 math over anything derived from the orbit's cached Keplerian elements.
- **Absolute positions cached across frames and converted via `PositionToPhysics` are not safe** —
  only vectors/offsets anchored to something the game re-renders fresh every frame are. This is a
  stronger version of the general "don't cache a converted-to-Unity position across frames" rule
  already in `FlightAxesVisualizer`'s pattern — it turns out even the *sim-space* input needs to be
  re-anchored to a live reference, not just re-converted.
- **Terrain-altitude checks (`GetAltitudeFromTerrain`) and anything rendered need independent
  rotation handling** — the former needs `Derotate` (snapshot-now query, no future-time concept); the
  latter does too (round 5/7), but for the *offset from the vessel*, not the absolute position
  (round 9/10) — don't conflate "does this need de-rotation" with "does this need to be anchored to
  a live reference," they're two separate axes of the same bug class.
- **Never trust the game's own patched-conic boundary (`orbit.EndUT`) when it comes from a
  `Collision` transition** (round 12) — it's derived from the same unreliable analytic Kepler-anomaly
  reconstruction as `GetTruePositionAtUT` above, just one level removed. Fine to trust for transitions
  this integrator can't model itself (SOI encounter/escape).
- **A single linear-interpolation guess between two RK4 samples isn't enough to place the final
  impact point** (round 13) — terrain isn't guaranteed to vary linearly between samples (boulders/
  ridges), so bisect and re-query the actual terrain altitude at each candidate instead of trusting
  the endpoints' altitude values alone.
