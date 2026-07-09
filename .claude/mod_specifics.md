# Mod specifics — SAS Extended

The current mod under development in this repo. Read this before doing feature work; keep the
root [`CLAUDE.md`](../CLAUDE.md) mod-agnostic and put mod-specific detail here. For engine/stack
idioms see [`architectural_patterns_ksp2.md`](architectural_patterns_ksp2.md) and
[`ksp2_assembly_csharp_reference.md`](ksp2_assembly_csharp_reference.md).

## What it is

**SAS Extended** — a flight mod that extends KSP2's stock SAS (Stability Assist System) to give
the player far finer control over vessel orientation. On top of the stock hold directions
(prograde, retrograde, normal, radial, etc.) the player can:

- Pick a **base direction** from Orbital / Surface / Target / Special (Misc) categories, and
- **Fine-tune the orientation** by entering angular **offsets** — **Heading**, **Pitch**, and
  **Roll** — relative to that base direction. E.g. select *Prograde* and enter `10` in Heading →
  the hold direction is offset 10° in heading from prograde.

**North star:** feature parity with KSP1 **MechJeb2's "Smart A.S.S."** module. MechJeb source is
mirrored locally at `E:\GitHub\KSP2\MechJeb2-master` (the module is
`MechJeb2/MechJebModuleSmartASS.cs`) — use it for reference on modes, offset semantics, and the
per-axis HDG/PIT/ROL controls.

- **swinfo:** id `SASExtended`, name "SAS Extended", author **Falki**, v0.1.0,
  `minKsp2Version` 0.2.8.3, depends on `SpaceWarp2 >= 2.0.0`.
- **Mod folder:** `Assets/SASExtended/` (see CLAUDE.md → Repository layout for the per-mod
  template structure).
- **Entry point:** `Assets/SASExtended/Code/SASExtendedPlugin.cs`.

## Mod base class — use `KerbalMod`

The scaffold currently has `SASExtendedPlugin : GeneralMod` (the ThunderKit default). **Convert
it to `Redux.ExtraModTypes.KerbalMod`.** SAS Extended is autopilot-driven: it must recompute and
re-apply a target orientation every frame, so it needs the MonoBehaviour `Update` loop and direct
game references that `KerbalMod` provides (this also mirrors the legacy SpaceWarp-1.x design). See
`architectural_patterns_ksp2.md` §6.

## How KSP2 SAS works (the API we build on)

Reached from the active vessel:
`GameManager.Instance.Game.ViewController.GetActiveSimVessel()` → `VesselComponent.Autopilot`
(`KSP.Sim.VesselAutopilot`) → `.SAS` (`KSP.Sim.VesselSAS`).

Two control paths matter:

| Path | Signature | Controls | Stock use |
|------|-----------|----------|-----------|
| **Directional** | `SAS.SetTargetOrientation(Vector tgt, bool reset)` | Points **one axis** at a direction vector. **No roll control.** | Stock prograde/retro/normal/radial/target/maneuver buttons. |
| **Full orientation** | `SAS.LockRotation(Rotation)` / `LockRotation(QuaternionD)` — writes `VesselSAS.LockedRotation` | Full 3-axis attitude incl. roll. | StabilityAssist ("hold current"). |

**SAS Extended uses `LockRotation`**, because heading + pitch + roll fine control requires
commanding a full orientation, which `SetTargetOrientation` can't express.

- `VesselAutopilot.SetActive(true)` engages SAS (as `AutopilotMode.StabilityAssist`);
  `SetActive(false)` / `Deactivate()` turns it off. `AutopilotMode` enum lives in `KSP.Sim`
  (`StabilityAssist, Prograde, Retrograde, Normal, Antinormal, RadialIn, RadialOut, Target,
  AntiTarget, Maneuver, Navigation, Autopilot`).
- **Direction vectors** all come from `TelemetryComponent` (`vessel._telemetryComponent` /
  `SimulationObject.Telemetry`), refreshed via `RefreshAutopilotTelemetry()`:
  - Orbital: `OrbitMovementPrograde/Retrograde/Normal/AntiNormal/RadialIn/RadialOut`,
    `OrbitalMovementVelocity`.
  - Surface: `SurfaceMovementPrograde/Retrograde/Velocity`, `HorizonNorth/South/East/West/Up/Down`.
  - Target: `TargetDirection`, `AntiTargetDirection`, `TargetPrograde/Retrograde`, `TargetVelocity`.
  - Maneuver: `ManeuverDirection`. Special: parent-star direction computed from body positions.
- Related messages (for UI/state sync): `SASEnabledMessage`, `SASDisabledMessage`,
  `SASModeChangedMessage`, `PropertyWatchers.SASMode`.

### Redux SAS changes vs. vanilla — HIGH-PRIORITY reference

The Redux team substantially reworked `KSP.Sim.VesselSAS`, fixing real vanilla problems. **This
diff has priority when we build our control layer** — we want to sit *on top of* the fixed Redux
SAS, not reintroduce the vanilla bugs. Diff basis: vanilla `Assembly-CSharp.dll` from
`D:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Managed` vs. the Redux
build in `Packages/KSP2_x64/`. `VesselSAS` grew ~959 → ~1601 decompiled lines; `VesselAutopilot`
is essentially unchanged. Recipe to re-derive:
`ilspycmd -t KSP.Sim.VesselSAS "<dll>"` on each, then diff.

**What Redux changed (four themes):**

1. **High-Q (high dynamic-pressure) damping — NEW.** The headline fix for vanilla's infamous SAS
   **oscillation/wobble during atmospheric ascent**. When `vessel.DynamicPressure_kPa` rises
   through a blend band (default 100→400 kPa, smoothstep), Redux blends the control response
   toward a gentler, integral-biased mode: settled axes switch to `IntegralOnlyOutput()`, the
   response is slewed (`Mathf.MoveTowards(..., 0.05f)`), and integral gain (`Ki`) is scaled by the
   Q-blend so it doesn't wind up. A latch/unlatch deadband (0.5°/1.5°) stops per-axis chatter.
   Tunable via static `SetHighQDampingEnabled(bool)`, `SetHighQDampingBlend(low,high)`,
   `SetHighQDampingDeadband(latch,unlatch)`. All new: `ComputeHighQDampingBlend`,
   `_highQDampingSettled{X,Y,Z}`, `_highQDampingLastResponse`, `_lastQBlendWasZero`.

2. **Torque-authority fix.** `GetTotalVesselTorque` now sums **`Mathf.Abs`** of each part's
   positive/negative potential torque per axis; **vanilla summed signed values**, which could
   partially cancel and **underestimate available control authority** (→ mushy/weak SAS). Redux
   also reuses a cached `List<PartBehaviourModule> cachedModuleList` instead of allocating a
   `GetComponents` array every part every frame (perf).

3. **PID auto-tuning / auto-scalar rework.** New constants drive `AutoTuneScalar()` /
   `TuneScalars()`: `AUTO_SCALAR_{LOW,HIGH,NOMINAL}_ACCELERATION_*` (slope/intercept/threshold),
   `DEFAULT_{PITCH,ROLL,YAW}_{KP,KI,KD,CLAMP}`, `MIN/MAX_SAS_RESPONSE`,
   `MIN/FALLBACK_ANGULAR_ACCELERATION`. PID clamp scalars are derived from
   `autoScalar / decayScalar` and adapt to the vessel's actual max angular acceleration. A pending-
   Ki mechanism (`SetPidKi`, `ClearPidKi`, `_pendingKiActive`) lets integral gain be staged and is
   folded in via the Q-blend.

4. **Diagnostics/instrumentation — NEW, useful to us.** A `TraceFrame` ring buffer with
   `BeginTrace()/EndTrace()/ClearTrace()/DumpTrace(path)` (CSV) plus a large set of **public**
   live-telemetry fields on `VesselSAS`: `torque{X,Y,Z}`, `moi{X,Y,Z}`, `alphaMax{X,Y,Z}`,
   `omega{X,Y,Z}`, `angDelta{X,Y,Z}`, `pid{Pitch,Roll,Yaw}{Raw,Integral,Ki}`, `sasResp{X,Y,Z}`,
   `stateIn/Out{Pitch,Roll,Yaw}`, `dynPressureKpa`, `altitudeFromSurface`, `surfaceSpeed`. These
   expose SAS internals we can read directly for a debug/telemetry view.

`VesselAutopilot`'s only functional change: it now calls `_telemetry.RefreshAutopilotTelemetry()`
before applying a non-StabilityAssist directional mode (telemetry freshness).

**Implications for SAS Extended (important):**
- We drive SAS through **`SAS.LockRotation` with `lockedMode = true`** (the StabilityAssist path).
  All of the above — high-Q damping, the torque fix, auto-tuning — runs **underneath** our
  commanded orientation automatically. We get the Redux improvements for free by using this path.
- **Do NOT reimplement attitude PID/damping ourselves.** Our job is to compute the *target
  orientation* (base direction + offsets) and hand it to Redux SAS; let Redux do the stabilization.
  The legacy prototype already delegates correctly via `LockRotation`.
- The public `VesselSAS` telemetry fields (and the trace dump) are available if we want to surface
  SAS state or debug pointing accuracy in our UI.
- Decompiled copies used for this analysis are throwaway (scratchpad); re-derive with the recipe
  above if you need to re-verify against a newer Redux build.

## UI design

A single flight window (UXML + USS), styled like the stock KSP2 panels. Layout, top → bottom
(see the mock and the legacy `SASExtended.uxml`):

- **Header:** title "SAS EXTENDED" + close button. Draggable window.
- **Top row (3 global modes):** `OFF`, `KILL ROT` (implemented — captures whatever attitude the
  vessel is at when engaged and holds it, mirroring vanilla `StabilityAssist`; doesn't point
  anywhere), `NODE` (implemented — points at the maneuver node's burn vector
  (`_telemetry.ManeuverDirection`); holds current attitude instead if no node is planned).
- **Tabs:** `ORB` · `SURF` · `TGT` · `SPEC`. Selecting a tab shows that category's direction
  buttons. (MechJeb's equivalent row is OBT/SURF/TGT/ADV.)
  - **ORB:** Prograde, Retrograde, Normal+, Normal−, Radial+, Radial−.
  - **SURF:** S VEL+, S VEL−, SURF, H VEL+, H VEL−, UP.
  - **TGT:** TGT+, TGT−, R VEL+, R VEL−, PAR+, PAR−.
  - **SPEC:** Star+, Star− (parent-star pointing; misc/experimental), plus **Hov** (hover).
- **Offset rows (3):** `Heading`, `Pitch`, `Roll`. Each row = a per-axis enable toggle (green
  LED) + a numeric field (degrees) + `−` / `+` nudge buttons + two presets (`0`, and `90`/`90`/
  `180`). Roll's second preset is 180°.
- MechJeb additionally has an **EXECUTE** button and uses radio-style per-axis toggles.
  **Decision: no EXECUTE button for now** — SAS Extended **applies offsets live** as they change
  (the legacy prototype does this and it worked well). Revisit only if live application proves
  problematic in practice.

Only one direction/global mode is active at a time; selecting one clears the others (mutual
exclusion is handled in the window controller).

### Per-axis offset toggle (Heading / Pitch / Roll LED) — semantics

**Implemented** in `SASManager.BuildPointingRotation` / `GetCurrentOffsetAngles`, uniformly across
every pointing mode (not just SURF as originally speculated below — no partial-axis
`SetTargetOrientation` split turned out to be needed; a single `LockRotation` per tick handles it):

- **Toggle ON:** apply the entered angle (`X`/`Y`/`Z` = Heading/Pitch/Roll) as an offset on that
  axis.
- **Toggle OFF → axis is genuinely free, not forced to 0.** Each refresh, `GetCurrentOffsetAngles`
  measures the vessel's *actual current* angle on that axis — solve
  `currentRotation = look.localRotation * offset * Euler(90,0,0)` for `offset` (reframing the
  vessel's `ControlTransform.Rotation` into the same coordinate system as `look` first), then
  decompose `offset` via Unity's own `Quaternion.eulerAngles` (the exact inverse of
  `Quaternion.Euler`, same convention used to build it: `Euler(-Pitch, Heading, Roll)`) — and
  commands that measured value straight back. The autopilot's error on that axis is then ~zero, so
  it applies ~no torque there and the vessel drifts freely (e.g. disabling Roll lets the vessel
  roll however it wants while heading/pitch keep tracking the target — mirrors vanilla SAS's own
  `SetTargetOrientation`, which likewise only constrains 2 DOF and leaves roll uncontrolled).
- `Hover` only ever exposes **Roll** to this mechanism — heading/pitch are pinned to the thrust
  direction there and aren't user-configurable.

### Hover mode (SPEC → Hov) — throttle control

Unlike every other mode (which only commands orientation via `LockRotation`), **Hover also drives the
throttle**. It points the vessel thrust-axis up, blended against horizontal surface velocity to null it,
and modulates throttle to hold `HoverTargetVerticalSpeed` (a signed m/s setpoint, reset to 0 on every
engage - **not** an altitude lock; see below for why). The throttle law
(`SASManager.UpdateHoverThrottle`) is a full P+I+D controller on vertical-speed error; the integral
self-tunes to the vessel's hover throttle so **no per-vessel thrust/mass/TWR model is needed**. Gains
are public tunables (`Hover*` fields on `SASManager`).

### Hover mode — instability fixes (branch `hover-fix`, 2026-07-09/2026-07-10, 11 rounds)

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
_vessel.gravityForPos.magnitude`.

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

**Still pending (not part of this fix, per [[hover-roadmap]]):** the **toggle for whether to cancel
horizontal velocity at all** is UI-facing with no existing UXML control to wire it to yet (the X/Y/Z
offset fields' `FloatField` + +/- `Button` pattern in `SASExtended.uxml` is the natural template to
reuse for a future target-vertical-speed input control too, now that `HoverTargetVerticalSpeed` is a
live setpoint rather than a dead field).

**Why a Harmony patch is required for throttle:** the stock `FlightInputHandler` keeps a persistent
`_flightCtrlState.mainThrottle` and pushes it to the active vessel every FixedUpdate, so setting throttle
from our `Update` loop (via `SetFlightControlState`) gets overwritten. Instead
`Patches/FlightInputHandlerThrottlePatch` postfixes `FlightInputHandler.UpdateFlightControlState` and, while
`AttitudeMode == Hover`, overwrites `mainThrottle` (Harmony field-injection param `____flightCtrlState`) —
after player input is applied but before the autopilot pass and the push, so the hover throttle sticks.
The plugin now calls `CreateHarmonyAndPatchAll()` in `OnInitialized`. (There is **no** stock throttle entry
in `FlightCtrlStateInputOverride` — only yaw/pitch/roll/translate/wheel — which is why the patch is needed.)
Throttle is left sticky on disengage (matches stock behaviour; forcing it to 0 could drop a climbing vessel).
Engaging hover commands throttle but does not auto-stage — engines must already be activated.

## v1 scope

**First working release = the ORB tab, fully functional** — the six orbital directions plus
working Heading/Pitch/Roll offsets (including free-axis-when-disabled), engaged/disengaged via
OFF, with SAS actually holding the computed orientation on a real vessel. **`KILL ROT` and `NODE`
(maneuver) are now also implemented** (see the Offset math section below). **SURF, TGT, and SPEC
(Star+/Star-) direction buttons are now implemented in code too** (branch `port-orb-tab`, not yet
in-game tested — see Status below).

## Porting from the legacy prototype

The pre-Redux version lives at **`E:\GitHub\KSP2\SASExtended`**. It targets **SpaceWarp 1.x /
BepInEx**, so nothing ports verbatim — treat it as a design reference, and note the user's warning
that it is *prototype-grade and not architecturally sound* (especially the quaternion math).

Legacy → new mapping:

| Legacy (`src/SASExtended/…`) | Role | Port plan |
|------|------|-----------|
| `SASExtendedPlugin.cs` | SW1.x `BaseSpaceWarpPlugin` entry, appbar registration, assembly loading | **Rewrite** as `KerbalMod`; use SpaceWarp2 appbar/asset APIs. |
| `Managers/SASManager.cs` | **Core**: per-`Update` builds a target `Rotation` per `AttitudeMode` and calls `SAS.LockRotation`; adaptive refresh interval by angle-to-target | **Port the design**, re-derive the offset math cleanly. This is the heart of the mod. |
| `Models/AttitudeMode.cs` | enum of all modes (Orbit*, Surface*, Target*, Special*, KillRot, Maneuver, Horizon) | Port ~as-is. |
| `UI/SceneController.cs`, `UI/MainWindowController.cs` | window creation + all button/tab/offset wiring | Port to SpaceWarp2 UI (`UitkForKsp2`); large but mostly mechanical. |
| `Attitude.cs`, `Direction.cs`, `Movement.cs`, `RotationWrapper.cs`, `Horizon.cs`, `LocalCoordinates.cs`, `AttitudeControlOverride.cs` | assorted math/experimental helpers | **Assess individually**; most are prototype scratch. Keep only what earns its place. |
| `DebugUI.cs`, `MaterialManager.cs` | IMGUI debug window / material helper | Optional; debug-only. |
| `UI/MainGuiController.cs` | — | **Ignore** — it's fully commented-out OrbitalSurvey leftover, not this mod. |

Unity-side assets (legacy `src/SASExtended.Unity/…/Assets/`):

| Legacy asset | Port plan |
|------|-----------|
| `UI/SASExtended.uxml`, `UI/SASExtended.uss` | **Port 1:1 / verbatim.** The author spent significant time getting the visual design exact — do **not** restyle, re-lay-out, "clean up", or tweak markup/USS. Carry the UXML and USS across unchanged (only adjust asset *paths/GUIDs* as the new project structure strictly requires). Any visual change must be explicitly requested. |
| `Runtime/SideToggleControl.cs`, `Runtime/TabToggleControl.cs` | Custom UI Toolkit controls (LED toggle button + tab button). Port — the UXML/controllers depend on them. In SW1.x they shipped in a separate `SASExtended.Unity.dll` registered via `CustomControls.RegisterFromAssembly`; **confirm the SpaceWarp2 mechanism for registering custom UITK controls** against the Redux docs before wiring these up. |

## Offset math (final design — supersedes the legacy prototype's approach)

The legacy prototype's heading/pitch derivation — `Vector3d.SignedAngle` vs. north + `Math.Asin`
vs. up, then rebuilding via a chain of local `QuaternionD.AngleAxis` rotations — was carried over
**verbatim** during the initial port (byte-for-byte identical to the legacy prototype). It turned
out to be **mathematically wrong** except very close to the gimbal-lock singularity (up to ~90° of
pointing error elsewhere for orbital modes, confirmed via a standalone quaternion simulation, since
Claude cannot drive the Unity editor to test in-engine). It has been replaced with the following
(all in `SASManager.cs`):

1. `Rotation.LookRotation(target, upwards)` aligns local **up** (the vessel's nose axis, after the
   trailing `Euler(90,0,0)` remap) exactly with `target`, for any target/upwards pair — zero error,
   no gimbal-lock caveat. This is what `BuildPointingRotation` does for every ORB/SURF/TGT/SPEC
   case (and `SurfaceUp`, which can't use `upwards` as its own up-hint since target and hint would
   be parallel — it uses `north` as the hint instead).
2. User offsets are applied as
   `localRotation * QuaternionD.Euler(-Pitch, Heading, Roll) * QuaternionD.Euler(90,0,0)`.
3. A disabled axis doesn't zero its offset — it's measured live off the vessel's current attitude
   instead, so it's genuinely free rather than pinned to one value. See "Per-axis offset toggle"
   above for the mechanism (`GetCurrentOffsetAngles`).
4. `SetRotation()` calls `_telemetry.RefreshAutopilotTelemetry()` first (previously missing — the
   game's own documented "call this before consuming telemetry" entry point for autopilot-style
   readers, as opposed to the display-only per-frame telemetry cycle) and reframes every telemetry
   direction vector into `HorizonNorth`'s coordinate system before use — mixing raw vectors across
   coordinate systems un-reframed was the root cause of an earlier Normal/Radial-swap bug (Normal
   and RadialIn/Out come from a frame drastically rotated relative to the horizon frame, unlike
   Prograde/Retrograde whose source frame happens to sit close to it, which is why only some
   directions looked wrong).
5. `KillRot` and `Maneuver` don't use the pointing pipeline above at all:
   - `KillRot` captures `_vessel.ControlTransform.Rotation` once at engage time
     (`SetSASKillrot()`) into `_killRotTarget` and just holds it every tick — mirrors vanilla
     `StabilityAssist`'s own `LockRotation(vessel.ControlTransform.Rotation)` — rather than
     steering toward any particular direction.
   - `Maneuver` points at `_telemetry.ManeuverDirection` through the same `BuildPointingRotation`
     pipeline as other modes, guarded by `_telemetry.HasManeuver` (that telemetry field is a
     degenerate zero vector when no node is planned, confirmed by decompiling
     `TelemetryComponent.UpdateManeuverTelemetry`) — falls back to holding current attitude
     (same approach as `KillRot`) when there's no node.
6. Fed to `SAS.LockRotation` on an **adaptive refresh interval** (`RefreshInterval_short/mid/long`,
   keyed off `GetAngleToRotation()`, itself fixed to reframe the vessel's current nose direction
   into `_rotation`'s coordinate system before comparing — same class of coordinate-mixing bug as
   #4, just affecting the refresh-rate heuristic rather than pointing accuracy).

**Diagnostics:** `SASManager` logs through ReduxLib's per-class logger
(`SASExtended|SASManager`) — `LogInfo` on every mode change (`SetMode`), and a `LogDebug` line at
the end of every `SetRotation()` tick with the mode, applied H/P/R values (and each axis's
enabled/disabled flag), angle-to-target, and all the orbit/horizon direction vectors — so future
orientation bugs can be diagnosed from the log directly instead of re-deriving everything through
decompilation again.

**Remaining known gaps:** SURF/TGT/SPEC direction buttons are now wired end-to-end (`SASManager`
switch cases + `SetXxx()` calls + `MainWindowController` toggle registration) but **not yet
in-game verified** — needs a Unity build + real-vessel test (see Status below). Two new modes
needed extra derivation beyond the existing ORB pipeline:
- **H VEL+/-** (`SurfaceHvelPlus/Minus`): horizontal component of surface velocity, computed the
  same way Hover already projects out the vertical component for its tilt calc
  (`surfaceVelocity - up * dot(surfaceVelocity, up)`, normalized).
- **R VEL+/-** (`TargetRvelPlus/Minus`): confirmed via decompiling `TelemetryComponent` that
  `TargetPrograde`/`TargetRetrograde` are already exactly the vessel's velocity *relative to the
  target* (`normalize(OrbitalMovementVelocity - targetOrbitalVelocity)`), matching MechJeb's
  `RELATIVE_VELOCITY` reference — no new math needed, just reframe and wire up.
- **PAR+/-** (`TargetParPlus/Minus`): aligns with the *target's own facing* (e.g. a docking port),
  via `TelemetryComponent.TargetFrame.forward/back` — unlike every other telemetry direction field
  (auto-properties that hold a stale/zero value with no target), `TargetFrame` is a live accessor
  that **throws `NullReferenceException`** if no target is selected, so it's read inside a new
  `BuildTargetOrientationRotation` helper guarded by `HasTargetObject`, not in the unconditional
  vector block at the top of `SetRotation` — falls back to holding current attitude (same pattern
  as Maneuver/KillRot with nothing to point at).
- **Star+/-**: the stale `// doesn't work` code comments (predating the LookRotation rewrite) have
  been removed; the modes run through the same `BuildPointingRotation` as everything else and are
  now wired to the UI, but still awaiting an in-game re-check.

`MainWindowController` was also refactored: every mode toggle (global + all 24 direction/hover
buttons) now goes through one shared `RegisterModeButton`/`ClearAllModeToggles` pair instead of a
~15-line hand-copied handler per button. This wasn't just cleanup — the old hand-copied handlers
had a latent bug where clicking an ORB button never cleared the Hover toggle, and there was no
mechanism at all to clear toggles across tabs, which the new SURF/TGT/SPEC buttons needed anyway.

## Build / deploy

Standard template flow (CLAUDE.md → Conventions): build via **ThunderKit pipelines in the Unity
Editor** — `Assets/SASExtended/Pipelines/` has *Build for Editor*, *Build for Player*, *Deploy to
Zip File*. Claude can't drive the editor; ask the user to run pipelines. Output stages to
`Assets/Mods/__Testing/SASExtended/`. Game content changes (if any) go through **PatchManager Lua
patches** in `Copied/Patches/`, not a C# patch API — though SAS Extended is primarily a runtime
autopilot mod and may need none.

## Status

**ORB-tab port done in code** (branch `port-orb-tab`, cut off `development`). Ported into
`Assets/SASExtended/`:

- `Code/SASExtendedPlugin.cs` — rewritten as `KerbalMod`; loads UXML + appbar icon via the
  instance `Assets.LoadAssetAsync<T>(key).WaitForCompletion()` accessor, registers the Flight
  appbar button, spawns the `SASManager` MonoBehaviour.
- `Code/Managers/SASManager.cs` — ported; ReduxLib `ILogger`; telemetry reached through the
  **public** `vessel.SimulationObject.Telemetry` (the Redux `Assembly-CSharp` is **not**
  publicized, so the private `_telemetryComponent` is off-limits — no reflection needed since
  `SimulationObject.Telemetry`, `Autopilot`, `SAS`, `mainBody`, `MOI` are all public). ORB offset
  math initially carried over verbatim (Heading→Pitch→Roll) but has since been found wrong and
  replaced — see "Offset math" above. `KillRot`/`Maneuver` now implemented too. **SURF/TGT/SPEC
  are now fully implemented and wired** (`SurfaceHvelPlus/Minus`, `TargetMinus`, `TargetRvelPlus/
  Minus`, `TargetParPlus/Minus` added; Star cases un-flagged) — see the "Remaining known gaps"
  note above for the per-mode derivation. Not yet in-game tested.
- `Code/Models/AttitudeMode.cs` — verbatim.
- `Code/UI/{SceneController,MainWindowController}.cs` — ported; `SceneController.Initialize(uxml)`
  now takes the pre-loaded `VisualTreeAsset` (no more SW1.x static `AssetManager`).
- `Code/UI/Controls/{SideToggleControl,TabToggleControl}.cs` — logic verbatim, but **refactored to
  the Unity 6 UITK pattern**: `[UxmlElement] public partial class` + `[UxmlAttribute]` properties
  (the old `UxmlFactory`/`UxmlTraits` nested classes are removed — deprecated in Unity 6). Namespace
  `SASExtended.UI.Controls` preserved so the UXML tag resolves; **no `CustomControls.RegisterFromAssembly`**
  (that API was removed). Each `[UxmlAttribute]` is given an **explicit name** matching the verbatim
  UXML's PascalCase attributes (`Text`, `IsBig`, `IsSmall`, `IsEnabled`, `IsToggled`), and `IsEnabled`
  is declared before `IsToggled` because the new deserializer applies attributes in declaration order
  and `SetEnabled` resets the toggle — so controls authored `IsEnabled="true" IsToggled="true"`
  (orb-tab, the x/y/z offset toggles) end up correctly toggled on. Pattern cribbed from OrbitalSurvey
  (`E:\GitHub\KSP2\OrbitalSurveyRedux\...\UI\Controls\SideToggleControl.cs`).
- `UI/SASExtended.uxml`, `UI/SASExtended.uss`, `UI/Images/Icons/{retrograde,ICO-Close-med,ELE-Panel-Handle}.png`
  — copied **byte-for-byte with their `.meta` files** so GUIDs (and every UXML/USS cross-reference)
  are preserved. True 1:1 UI port; nothing restyled.

**Build constraint:** the Unity asmdef compiles at **C# 9.0** (LangVersion 9.0, Unity 6000.4.1f1)
— so **no file-scoped namespaces and no `with` on structs** (both C# 10). Use block namespaces and
object initializers. See [[langversion-csharp9]].

**Remaining — needs the Unity editor (Claude can't drive it):**
1. Confirm the copied UI assets imported (folder/`.cs` `.meta` files auto-generate on import).
2. Mark `SASExtended.uxml` and `retrograde.png` **addressable** with addresses matching the
   `const`s in `SASExtendedPlugin.cs` (`WindowUxmlAddress` / `FlightIconAddress`, currently
   `SASExtended/SASExtended_ui/ui/sasextended.uxml` and `.../images/icons/retrograde.png`) — or
   tell Claude the addresses you use and the consts get updated. USS + referenced icons ride along
   as UXML dependencies, so only these two need explicit addresses.
3. Run **Build for Editor** and test on a real vessel: ORB directions + Heading/Pitch/Roll offsets,
   engaged/disengaged via OFF.
4. Test the new SURF/TGT/SPEC buttons: S VEL+/-, SURF, H VEL+/-, UP; TGT+/-, R VEL+/-, PAR+/- (the
   latter two need a target selected - expect a hold-current-attitude fallback with no target
   selected, not a crash); Star+/-. Also sanity-check that switching between tabs/modes correctly
   clears the previously-active toggle everywhere (the `RegisterModeButton`/`ClearAllModeToggles`
   refactor in `MainWindowController.cs`).
