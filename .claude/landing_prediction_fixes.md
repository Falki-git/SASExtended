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

## Takeaways for next time

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
