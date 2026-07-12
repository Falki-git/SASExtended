# Hover mode — instability fix investigation log

Detailed round-by-round log of the Hover mode (SPEC → Hov) instability debugging on branch
`hover-fix`. Referenced from [`mod_specifics.md`](mod_specifics.md) § Hover mode. All fixes are in
`Assets/SASExtended/Code/Managers/SASManager.cs` unless noted. Kept in its own file since this
level of detail isn't needed for day-to-day mod work — read it only when touching Hover's
throttle/tilt control law again.

## Round-by-round log (branch `hover-fix`, 2026-07-09/2026-07-10, 11 rounds)

**Round 1 (2026-07-09, diagnosed from a 2026-07-08 `Player.log` capture)** — both bugs were in the
horizontal-velocity-cancellation path. **Fixed by porting the tilt/throttle coupling from MechJeb2's
Translatron + ThrustController** (`MechJebModuleTranslatron.cs` / `MechJebModuleThrustController.cs`
KEEP_VERTICAL + `TransKillH`, see [[external-sources]] for the MechJeb2 source path):

1. **Throttle starving all horizontal-correction authority.** The old P+I law correctly zeroed
   throttle whenever actual vertical speed exceeded the target rate (you can't thrust to *increase*
   descent), but that also zeroed thrust entirely, so tilt had no force left to redirect (reproduced
   by engaging Hover while still climbing at ~80 m/s right after an ascent — `HoverThrottle` pinned
   at `0.000` for the whole session while `horizontalSpeed` grew from ~47 to ~300 m/s uncorrected).
   **Fix:** a full-burn escape valve, ported from MechJeb's Translatron DIRECT mode - past
   `HoverEscapeHorizontalSpeed` (with hysteresis down to `HoverEscapeExitSpeed`, and gated on
   `AltitudeFromSurface > HoverEscapeMinAltitude` so a hard sideways burn can't trigger near terrain),
   Hover stops tracking vertical speed and just burns retrograde-to-drift at `HoverEscapeThrottle`,
   prioritizing killing the drift over the vertical-rate target.
2. **Horizontal-velocity cancellation oscillating instead of converging.** The old tilt calc used a
   hard angle cap (`HoverMaxTilt`, 30°) on a pure-proportional tilt-vs-horizontal-speed law: with the
   default gain, anything above ~1.15 m/s horizontal speed pinned tilt at the cap, making the
   correction bang-bang (always max-tilt toward instantaneous anti-velocity) instead of proportional
   - a classic lagged-proportional limit cycle (confirmed via `Player.log`: `horizontalSpeed`
   swinging ~1–24 m/s on a ~9–10s period, drift *direction* rotating through a full circle each
   cycle). **Fix:** replaced the hard cap with a floor blend (`HoverTiltAuthorityFloor`, or the
   vessel's actual vertical speed if that's bigger) - the same shape as MechJeb's
   `up * Max(|vspeed|, 20*gee)` - so tilt angle is now a smooth, uncapped function of drift-vs-floor
   instead of saturating almost immediately.

**Round 2 (2026-07-10, diagnosed from an in-game Minmus test after round 1)** - two more bugs, this
time in the vertical-speed throttle law itself, both confirmed via fresh `Player.log` telemetry:

3. **Altitude-hold setpoint fought a fresh engage.** The throttle law's target vertical speed used
   to be derived from an altitude lock captured at engage time (`_hoverTargetAltitude` +
   `HoverAltitudeGain`, capped by `HoverMaxClimbRate`/`HoverMaxDescentRate`). Engaging Hover while
   still climbing meant the vessel kept climbing well past the engage altitude before the
   altitude-derived target speed pulled back down near zero - logged coasting from 37m to over
   **137m** at `HoverThrottle=0.000[SATURATED]` the entire way, on a single engage. Altitude was
   never supposed to matter for Hover; wherever the vessel ends up once vertical speed reaches
   target *is* the hover altitude. **Fix:** removed the altitude lock entirely - `HoverThrottle` now
   holds `HoverTargetVerticalSpeed` directly (0 on engage) with no altitude term at all. This is also
   the foundation for the still-pending signed target-vertical-speed input (see below) - once a UI
   exists, it just sets this same field.
4. **Vertical speed overshooting and oscillating instead of settling at 0.** Confirmed via
   `Player.log`: throttle ramping 0→0.885, overshooting past the target, cutting to 0, coasting back
   down, and repeating - each cycle's peak throttle decaying (0.885 → 0.395 → 0.225 → ~0.16) but very
   slowly. Root cause: the throttle law only had P+I terms; MechJeb's own `PIDController(0.05,
   0.000001, 0.05)` has a **D** term too, which ours was missing. Nothing opposed a fast-changing
   vertical speed until the P/I terms had already caught up - by then the vessel had overshot.
   **Fix:** added `HoverThrottleKd`, a derivative-on-measurement term (reacts to the vessel's own
   vertical acceleration, not to the error, so a future setpoint change from an eventual UI won't
   itself spike the term) that backs off throttle pre-emptively before the overshoot happens.

**Round 3 (2026-07-10, same day, diagnosed from a follow-up Minmus test after round 2)** - round 2
made both symptoms much better but not perfect; two more fixes, one per symptom:

5. **Residual, slowly-decaying vertical-speed oscillation.** Confirmed via `Player.log`: no longer the
   wild round-1 swings, but still a visible ringing (actual vertical speed cycling roughly ±0.3 m/s,
   shrinking cycle over cycle) before settling fully at 0.00. Classic sign of an integral term still a
   little too aggressive relative to the new derivative term. **Fix:** rebalanced gains -
   `HoverThrottleKi` 0.10 → 0.06, `HoverThrottleKd` 0.05 → 0.08. Not independently re-tested yet; if
   ringing is still visible, nudge further in the same direction (lower Ki, higher Kd) rather than
   reworking the law again.
6. **Horizontal drift canceled far too slowly - thrust looked "nearly vertical."** This turned out
   *not* to be a tilt-angle bug - `Player.log` showed `hoverCosTilt=0.705` (a correct 45 degrees) for
   ~10 m/s of horizontal drift, matching the floor formula exactly. The real cause: `HoverThrottle`
   was sitting at ~0.015-0.018 (next to nothing) at the same moment, because `_throttleIntegral`
   (the vessel's self-tuned hover-equilibrium throttle) hadn't converged yet - vertical speed was
   already close to target, so the P+I+D law saw almost no error and had no reason to command more
   thrust. **Fix:** `HoverTiltAuthorityFloor` scales with `_throttleIntegral` relative to
   `HoverTiltReferenceThrottle` (a self-tuning mechanism, not a per-vessel constant) - a vessel with a
   powerful engine (low self-tuned hover throttle) has lots of spare thrust budget and tilts much more
   aggressively for the same drift, which combines with the *existing* `/hoverCosTilt` throttle boost
   in `UpdateHoverThrottle` to actually deliver "more lateral, compensated by greater total thrust" -
   exactly what was requested. `HoverTiltAuthorityFloorMin` guards against tilting near-90 degrees
   before the integral has any real data (defaults to the nominal floor - i.e. conservative - while
   `_throttleIntegral` is still exactly 0, e.g. right after engage).

**A ~200m altitude drop was observed in a follow-up test** with `hoverCosTilt` down to 0.077-0.13
(85-87 degrees) while `_throttleIntegral` was still ramping (0.002-0.09) at ~25-28 m/s of horizontal
drift. This was initially misdiagnosed as a bug in the round-3 tilt-floor scaling and briefly
reverted. **The user identified the actual cause: manual thrust/attitude input given while Hover was
still engaged**, deliberately introducing horizontal drift to test the drift-cancellation behavior -
not an autonomous failure of the throttle/tilt law. The round-3 fix was restored as-is. **Testing
convention going forward: disengage Hover before making any manual adjustment to attitude or
throttle**, so a test run's `Player.log` telemetry reflects Hover's own behavior only, not a mix of
manual control and autopilot response.

**Validated (2026-07-10, clean 3-scenario test, no manual intervention) - approved.** Confirmed via
`Player.log` across three engages in one session:
- **Climb (+13 m/s) + 25 m/s horizontal drift at engage:** correctly coasted at zero throttle while
  vertical speed decayed; once near target, the tilt floor scaled down aggressively
  (`hoverCosTilt` to ~0.08-0.09, throttle to 0.5-0.7) specifically to cancel the drift - horizontal
  speed dropped from 25 m/s to single digits within seconds, altitude only wobbled ~1.5m (not the
  200m of the manual-intervention incident) - then settled into a long, rock-steady 0.00 m/s hold
  (`hoverCosTilt=1.000`).
- **Near-hover baseline:** a brief wobble (tilt dipping to ~0.05, throttle briefly saturating)
  self-corrected within seconds into an extended, essentially perfect 0.00 m/s hold.
- **Extreme high-speed descent (-128.82 m/s at 12514m):** throttle saturated to 1.0 immediately, the
  escape valve engaged briefly and handed back control cleanly, the vertical-rate PID rode saturated
  throttle from -144 m/s through zero (~6.6 m/s overshoot only), and settled into a long, rock-steady
  0.00 m/s hold.

All three converge to genuinely tight, oscillation-free holds with no repeat of the tilt-floor
runaway - the mechanism does what it was designed to do (steep, throttle-heavy lean for fast drift
cancellation) without a repeat failure. `HoverTiltAuthorityFloorMin` (2 m/s) was not revisited since
no unsafe instance turned up in this clean test. **This validation turned out to be low-gravity-only
- see round 4 below.**

**Round 4 (2026-07-10, same day) - Kerbin test (much stronger gravity, different vessel) exposed a
bug the round-2/3 fixes never hit on Minmus.** Symptoms exactly as reported: engines rapidly firing
and cutting (`HoverThrottle` cycling ~0 <-> 0.5-1.0 every few frames), vertical speed permanently
stuck around +10 m/s (never converging), horizontal drift not just failing to cancel but growing and
rotating direction (mirrors the very first round-1 bug pattern). Root cause: the `HoverThrottleKd`
derivative term (added in round 2/3 to fix Minmus overshoot) reacts to **raw** vertical acceleration
with no gravity compensation. Coasting at zero throttle decelerates at ~1 g regardless of body -
~0.05 g on Minmus (`Kd * vAccel` negligible, ~0.02-0.04) vs. ~1 g on Kerbin (`Kd * vAccel` ~0.8-1.0,
confirmed via `Player.log`: `vAccel` consistently -10.9 to -12.0 while coasting). On Kerbin this
spurious term completely swamped the P term's correct "still climbing too fast, hold throttle at 0"
signal, firing throttle to fight *ordinary gravity* rather than any real overshoot - which then
looked like its own overshoot once thrust kicked in, cutting throttle back to 0 and repeating
forever. This is why it "worked for small gravity, doesn't work for larger gravity bodies" - the D
term was never actually gravity-invariant, the bug was just ~20x smaller on Minmus. **Fix (attempt
1, wrong property):** subtracted `_vessel.gravityTrue.magnitude` from the raw derivative. This
compiled fine but was a complete no-op - confirmed by a follow-up Tylo test (similar gravity to
Kerbin, no atmosphere so a clean test) that crashed with identical symptoms, and `Player.log` showed
`thrustDrivenAccel` logged **identical** to `vAccel` on every single line. Root cause of the no-op:
`VesselComponent.gravityTrue`'s private setter is never called anywhere in the whole decompiled type
(confirmed via `ilspycmd -t KSP.Sim.impl.VesselComponent`) - it's a dead field that always reads as a
zero vector, magnitude 0. **Fix (attempt 2, correct):** use `_vessel.gravityForPos.magnitude`
instead - a *live computed* property (`VesselComponent.gravityForPos =>
_universeModel.GetGeeForceAtPosition(CenterOfMass, mainBody)`, decompiled and confirmed to compute a
genuine `-GM/r^2` acceleration, not a cached field) - so `thrustDrivenAccel = verticalAccel +
_vessel.gravityForPos.magnitude`. See [[verify-decompiled-fields]] for the general lesson.

**Round 5 (2026-07-10, same day) - the gravity-compensated D term worked (confirmed:
`thrustDrivenAccel` no longer equals raw `vAccel` in `Player.log`), but a Tylo re-test still
crashed.** Different mechanism this time, in the tilt/escape-valve path, not the throttle law:
horizontal drift grew past 90 m/s (`Player.log`: `horizontalSpeed` climbing from ~65 to 354 m/s)
while altitude sat at 650-950m - **below `HoverEscapeMinAltitude` (3000)**, so `escape=False` the
entire time despite drift being 2x+ over the 40 m/s escape threshold. With the escape valve
unavailable, the normal tilt-floor blend was the only thing trying to cancel the drift, and it has
no ceiling of its own - it drove `hoverCosTilt` down to **0.026-0.045** (87-88 degrees). At that
angle even 100% throttle delivers almost no vertical thrust, so the vessel fell, accelerating past
-230 m/s before impact, while horizontal speed kept growing the whole time (never enough spare
thrust to fix either problem simultaneously). **Fix (two parts, same root cause: nothing bounded
worst-case tilt when the escape valve is unavailable):**
1. Added `HoverMaxTiltAngle` (75 deg) - a hard ceiling on the normal blend's tilt angle, enforced by
   raising `verticalFloor`'s effective minimum (`horizontalSpeed / tan(HoverMaxTiltAngle)`) so it
   composes with the existing floor-scaling logic instead of overriding it. Guarantees at least
   `cos(75°) ≈ 26%` of thrust stays available vertically no matter how aggressively the drift-kill
   tilt wants to lean.
2. Lowered `HoverEscapeMinAltitude` from 3000 to 300 - the old value was gating the escape valve off
   in exactly the low-altitude, high-drift emergency it exists to handle. Still nonzero so a hard
   sideways burn can't trigger right at the surface.

**Round 6 (2026-07-10, same day) - the escape valve itself was the problem; removed entirely
rather than tuned further.** A follow-up Tylo test crashed again - much worse this time. Once the
lowered altitude gate let the escape valve engage on genuinely large sustained drift,
`Player.log` showed `escape=True` continuously for thousands of meters of altitude: `hoverCosTilt`
pinned at **0.010** (the escape branch points purely perpendicular to "up" by construction, with a
fixed `HoverEscapeThrottle=1.0` regardless of vertical speed) - meaning it reserves **zero** vertical
thrust for as long as it stays engaged. `actualVSpeed` ran away to **-436 m/s** while escape stayed
active the entire time, and `horizontalSpeed` never converged either - it *grew*, from ~65 m/s past
**387 m/s**, because the escape target direction has no floor/damping (unlike the normal blend) and
re-triggered the *original* round-1 bang-bang problem (chasing a horizontal-velocity vector that
itself keeps rotating from attitude-slew lag), but this time with none of the normal blend's safety
margin - the worst of both known failure modes at once. Lowering `HoverEscapeMinAltitude` in round 5
didn't cause this design flaw, it just gave a pre-existing bad subsystem more opportunity to run at
altitudes where a multi-thousand-meter freefall is fatal. **Fix: removed the escape valve
subsystem entirely** (`_hoverEscapeActive`, `HoverEscapeHorizontalSpeed`, `HoverEscapeExitSpeed`,
`HoverEscapeMinAltitude`, `HoverEscapeThrottle`, and the `_hoverEscapeActive` branches in both
`SetRotation`'s Hover case and `UpdateHoverThrottle`) - all horizontal-drift handling now goes
through the single self-tuning floor blend, which already has the round-5 `HoverMaxTiltAngle` safety
cap (guaranteeing ~26% vertical thrust stays reserved) and the full gravity-compensated P+I+D
throttle law (never a hardcoded throttle override). This is a straightforward removal - not a
retune - because the escape branch's two defining properties (zero reserved vertical thrust, no
damping on its target direction) were structurally different from, and less safe than, the normal
blend in every dimension; there was nothing in it worth preserving once the normal blend had its own
safety cap.

**Round 7 (2026-07-10, same day) - `HoverMaxTiltAngle` (a flat 75 deg) was itself an unscaled
guess, same problem the flat `HoverTiltAuthorityFloor` had before round 3 made it self-tuning.**
A second Tylo crash, with a lower-TWR vessel than earlier tests: capped at 75 deg (`cos = 0.259`,
~26% of thrust reserved vertically), the vessel spent an extended stretch fighting ~100-165 m/s of
horizontal drift while Tylo's gravity quietly out-accelerated that 26% reserve - `Player.log` showed
`actualVSpeed` running away to **-365 m/s** before the tilt logic recognized the emergency and
correctly climbed back toward vertical (`hoverCosTilt` 0.259 → 0.95), but by then there wasn't
enough altitude or thrust budget left to arrest the fall, and horizontal speed had *also* plateaued
uncorrected (~118 m/s, flat) once tilt narrowed - a vessel with less spare TWR than the ones that
validated 75 deg earlier simply couldn't afford that angle. **Fix: replaced the flat
`HoverMaxTiltAngle` with a self-tuning cap** derived from `_throttleIntegral` (the same self-tuned
hover-equilibrium throttle the floor already uses) and a new `HoverTiltThrottleBudget` (0.8) - the
max tilt angle is whatever angle would make holding `HoverTargetVerticalSpeed` at that angle cost
exactly that fraction of total throttle, leaving the rest as headroom. A vessel that already needs
most of its throttle just to hover gets little to no tilt margin; a vessel with lots of spare thrust
still gets to tilt aggressively - the same principle the floor already uses, now applied to the
angle ceiling too, so neither term is a per-vessel guess anymore. `HoverMaxTiltAngleFallback` (45
deg) is used only before `_throttleIntegral` has any data (conservative, TWR unknown).

**Diagnostics added** (requested explicitly, to make the next test conclusive either way):
`[Hover/attitude]` now logs `throttleIntegral`, `tiltFloor`, `maxAngleFloor`, and `maxTiltAngleDeg`
every line - previously it was impossible to tell from the log alone which term (the self-tuned
floor, the actual-vertical-speed floor, or the angle cap) was deciding the tilt at any given moment,
which is exactly what made both this bug and the round-6 escape-valve bug slow to pin down.

**Round 8 (2026-07-10, same day) - found the actual root cause of the horizontal-cancellation
"overcompensation" the user described, thanks to round 7's new diagnostics.** The user correctly
suspected a high-TWR engine was the cause. `Player.log` from that round's richer telemetry showed
`actualVSpeed` staying small and controlled (-7 to +7 m/s) throughout a whole stretch, while
`thrustDrivenAccel` swung wildly between -8.7 and **+48 m/s²** every few frames, and
`throttleIntegral` swung the *full 0-1 range* in ~6-8 seconds. Mechanism: a powerful engine turns
even a brief full-throttle burst into a huge acceleration spike; `HoverThrottleKd` (a flat gain in
raw m/s², not normalized to the vessel's actual thrust capability) reacts to that spike violently,
slamming throttle back to 0 - which lets gravity swing the reading the other way, repeating (a
classic bang-bang/relay oscillation). Because `HoverTiltAuthorityFloor` and the tilt-angle cap are
both keyed off this same `_throttleIntegral`, they whipsawed in sync - the horizontal-drift
oscillation the user reported ("overcompensate ... start thrusting in the different direction") was
*downstream* of this throttle bug, not a separate one. **Fix: rate-limit the throttle actuator
itself** (`HoverThrottleMaxRate`, default 2.0 - full 0→100% takes at least 0.5s), applied as the
final step after the P/I/D formula and the 0-1 clamp, bounding what the engine can physically do
regardless of how large a jump the formula asks for. Deliberately *not* a smaller `HoverThrottleKd`
guess - that would be the same per-vessel-guess mistake already made twice (for the floor, then the
angle cap); a rate limit on the actuator is TWR-agnostic by construction; whatever the vessel's true
thrust capability, the commanded value physically cannot swing faster than this rate allows, so the
resulting real acceleration swings shrink too, which should calm the tilt-floor/cap whipsaw as a
side effect since they're downstream of the same real vertical dynamics.

**Round 8 result (2026-07-10, same day) - approved, "almost perfect."** Confirmed via a
multi-engage `Player.log` (progressively lower altitude/speed engages down to an 18m/-8 m/s final
approach): vertical hold converges smoothly and holds tight (~±0.5 m/s at the lowest altitudes),
horizontal drift cancels to near-zero when present, no crashes, `hoverCosTilt` reaches a clean 1.000
at rest. The rate limiter is doing real work (`RATE-LIMITED` on ~59% of samples in the first engage).

**Round 9 (2026-07-10, same day) - minor polish, not a bug fix.** Even in a well-settled hover
phase (stable altitude, tight vertical speed), `Player.log` showed the *raw* `thrustDrivenAccel`
signal still jittering hard frame to frame (~3.3 to ~14.2 m/s² on consecutive samples) - a raw
single-frame finite-difference derivative is a classic noise-amplification problem, especially on a
vessel responsive enough that small per-frame velocity-reading jitter produces a large computed
acceleration. The round-8 rate limiter already prevents this from reaching the actual engine as
visible chatter, so this wasn't causing a real problem, but filtering the signal at its source is the
more correct fix and should reduce unnecessary throttle micro-adjustments further. **Fix:** added
`HoverThrottleAccelFilterTime` (0.2s), a low-pass filter (`_filteredThrustDrivenAccel`) applied to
`thrustDrivenAccel` before it's multiplied by `HoverThrottleKd` - `[Hover/throttle]` now logs both
the raw and filtered values so the next test can confirm the improvement. Same category as the rate
limiter (a signal-processing fix, not another flat-gain guess) - TWR-agnostic, doesn't assume
anything about the vessel's thrust capability.

**Round 9 result (2026-07-10, same day) - "much better still," approved.**

**Round 10 (2026-07-10, same day) - the round-7 tilt-angle cap was too conservative for a large,
sustained drift-cancellation, leaving real spare thrust unused.** User's own diagnosis, confirmed by
`Player.log`: a 320+ m/s horizontal-drift cancellation took over 3 minutes. `HoverThrottle` sat at a
modest, non-saturating ~0.14-0.15 the *entire time* (`throttleIntegral` ~0.68) while `hoverCosTilt`
stayed pinned at the self-tuned cap (~0.83-0.87, ~30 deg) for the whole stretch - the vessel had
abundant spare thrust capacity it was never allowed to use, because `HoverTiltThrottleBudget=0.8`
only grants a ~31 deg cap at a 0.68 hover-equilibrium throttle, and the cap is calibrated against
that *worst-case* baseline, not the throttle actually in use at any given moment (which here was
nowhere close to saturating). **Fix:** raised `HoverTiltThrottleBudget` from 0.8 to 0.9 - at the same
0.68 hover-equilibrium throttle this grants ~41 deg (`tan` ratio, i.e. horizontal-deceleration
authority, up ~50% from the previous cap) while still reserving 10% of throttle as headroom, nowhere
near the razor-thin margin (75 deg flat, ~26% reserved) that caused the round-7 Tylo crash on a
vessel with much less real spare capacity than this one had. A tuning increase to an existing
self-tuning mechanism, not a new one. Not yet re-tested - if a future test still shows excessive
conservatism on a similar large-drift case, the next lever is another increment here (e.g. to
0.93-0.95) rather than reintroducing a flat angle or reverting to fixed constants.

**Round 10 result (2026-07-10, same day) - an extensive multi-scenario test (many engages from
852m down to 8m, one extreme -186 m/s / 30059m descent-arrest, plus SURF/TGT/ORB/SPEC exercised
too) - user satisfied with the results.**

**Round 11 (2026-07-10, same day) - fixed a residual tilt flip-flop found while reviewing that
extensive test, not reported by the user.** `Player.log` showed `hoverCosTilt` oscillating
repeatedly between 0.707 (the "unknown data" fallback) and steep values (0.06-0.22) on consecutive
samples, while `horizontalSpeed` barely changed - tracking `_throttleIntegral` swinging between
exactly 0.0 and small positive values, deep into an already-established hover (not near engage).
Root cause: `throttleDataKnown` was inferred from `_throttleIntegral > 0.0`, conflating "no data
yet" (only true right after an all-engines-off engage) with "the integral currently sits at its
clamped floor of exactly 0.0" - a completely normal, ongoing PID state, not a sign of missing data.
Every time the integral legitimately grazed 0 during flight, the tilt logic incorrectly reverted to
the conservative engage-time fallback (nominal floor, 45 deg cap) instead of recognizing "we have
real data, and it happens to be 0" (which the self-tuning formulas already handle sensibly - a floor
clamped to `HoverTiltAuthorityFloorMin`, an angle cap of ~90 deg). **Fix:** replaced the
`_throttleIntegral > 0.0` inference with an explicit one-time flag, `_hoverThrottleIntegralKnown`,
set `false` only in `SetHover()` and `true` unconditionally the first time `UpdateHoverThrottle` runs
after that (regardless of what value it computes) - so "do we have real data" and "what does the
data say" are no longer the same test. Not yet re-tested.