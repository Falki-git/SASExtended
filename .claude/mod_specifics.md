# Mod specifics — SAS Extended

The current mod under development in this repo. Read this before doing feature work; keep the
root [`CLAUDE.md`](../CLAUDE.md) mod-agnostic and put mod-specific detail here. For engine/stack
idioms see [`architectural_patterns_ksp2.md`](architectural_patterns_ksp2.md) and
[`ksp2_assembly_csharp_reference.md`](ksp2_assembly_csharp_reference.md). For custom UITK control
gotchas and the UI Authoring Mode workflow see
[`custom_uxml_controls.md`](custom_uxml_controls.md).

## What it is

**SAS Extended** — a flight mod that extends KSP2's stock SAS (Stability Assist System) to give
the player far finer control over vessel orientation. On top of the stock hold directions
(prograde, retrograde, normal, radial, etc.) the player can:

- Pick a **base direction** from Orbital / Surface / Target / Special (Misc) categories, and
- **Fine-tune the orientation** by entering angular **offsets** — **Heading**, **Pitch**, and
  **Roll** — relative to that base direction. E.g. select *Prograde* and enter `10` in Heading →
  the hold direction is offset 10° in heading from prograde.

Design north star is feature parity with KSP1 MechJeb2's "Smart A.S.S." module — see
[[external-sources]] for where that reference source and the legacy pre-Redux prototype live.

- **swinfo:** id `SASExtended`, name "SAS Extended", author **Falki**, v0.1.0,
  `minKsp2Version` 0.2.8.3, depends on `SpaceWarp2 >= 2.0.0`.
- **Mod folder:** `Assets/SASExtended/` (see CLAUDE.md → Repository layout for the per-mod
  template structure).
- **Entry point:** `Assets/SASExtended/Code/SASExtendedPlugin.cs` — a `Redux.ExtraModTypes.KerbalMod`.

## Code layout

| Path | Role |
|------|------|
| `Code/SASExtendedPlugin.cs` | Mod entry point. Binds config (`Settings.Initialize()`), registers the custom UITK control factories (reflection-based `VisualElementFactoryRegistry.RegisterFactory` — see `custom_uxml_controls.md`), loads the window UXML + appbar icon via `Assets.LoadAssetAsync<T>(key).WaitForCompletion()`, spawns the `SASManager` MonoBehaviour, applies Harmony patches, and hides/restores the window on flight-scene enter/exit (`GameStateChangedMessage`). |
| `Code/Managers/SASManager.cs` | **Core control loop.** A `MonoBehaviour` that every `Update()` computes a target `Rotation` for the active `AttitudeMode` and commands it via `SAS.LockRotation`; also drives Hover's throttle. See "How KSP2 SAS works" and "Offset math" below. Exposes `IsEngaged` and `CommandedRotation` (the slew-limited setpoint) for the Flight Axes visuals. |
| `Code/Managers/FlightAxesVisualizer.cs` | **In-world flight-axis visuals** — split control axes (fwd/up/right), commanded/target attitude arrows, orbital (prograde/normal/radial-in), surface & horizontal velocity arrows, CoM marker — toggled from the settings panel. Data-driven arrow registry (`_arrowSpecs`/`SetVisual`/`ToggleIds`). Reuses the game's own debug-shape components/prefabs. See "Flight Axes visuals" below. |
| `Code/Managers/LandingPredictionManager.cs` | **In-world landing prediction visuals** — a trajectory line + ground impact marker for the active vessel's predicted unpowered coast, on any body with a solid surface (pure ballistic coast, no atmospheric drag model - rough indicator only on atmospheric bodies). RK4-integrated off the orbit's live state vector (deliberately not the orbit's analytic Kepler-anomaly reconstruction, which is unreliable in this predictor's near-radial regime). See "Landing prediction visuals" below and [`landing_prediction_fixes.md`](landing_prediction_fixes.md) for the debugging history — read that before touching this file again. |
| `Code/PureMath/{AttitudeMath,HoverThrottleMath}.cs` | Pure quaternion/control-law math extracted out of `SASManager`, free of KSP.Sim's frame-aware types, so it can be exercised by edit-mode unit tests instead of only in-game. Change the *math* here; keep `SASManager` as the thin glue that feeds it live telemetry. |
| `Tests/EditMode/{AttitudeMathTests,HoverThrottleMathTests}.cs` | Unity Test Framework edit-mode tests for the above. Run from Unity's Test Runner window. |
| `Code/Models/AttitudeMode.cs` | Enum of every mode (see full list below). |
| `Code/UI/SceneController.cs`, `Code/UI/MainWindowController.cs` | Window creation and all button/tab/offset/status wiring. |
| `Code/UI/Controls/{SideToggleControl,TabToggleControl}.cs` | Custom UITK controls (LED toggle button + tab button) the UXML instantiates by tag. Dual-mode compiled — see `custom_uxml_controls.md`. |
| `Code/Utilities/Settings.cs` | Centralizes every `SWConfiguration` binding, all bound in `Initialize()` from `OnPreInitialized()` (must happen before the game snapshots mods' configs for the Settings → Mods page — binding lazily from UI code is too late). Window position/open-state, status-readout interval/toggle, SURF's default offset, verbose-logging toggle are persisted; per-mode Heading/Pitch/Roll memory and Hover's target vertical speed are **session-only** (`SessionValue<T>`, not `ConfigValue<T>`) — remembered across mode/vessel switches but reset on game restart. |
| `Code/Patches/FlightInputHandlerThrottlePatch.cs` | Harmony postfix on `FlightInputHandler.UpdateFlightControlState`; see Hover mode below for why it's needed. |
| `UI/SASExtended.uxml`, `UI/SASExtended.uss` | The window's UXML/USS. **Ported verbatim from the legacy prototype — do not restyle, re-lay-out, or "clean up."** Any visual change must be explicitly requested. |

## How KSP2 SAS works (the API we build on)

Reached from the active vessel:
`GameManager.Instance.Game.ViewController.GetActiveSimVessel()` → `VesselComponent.Autopilot`
(`KSP.Sim.VesselAutopilot`) → `.SAS` (`KSP.Sim.VesselSAS`).

Two control paths matter:

| Path | Signature | Controls | Stock use |
|------|-----------|----------|-----------|
| **Directional** | `SAS.SetTargetOrientation(Vector tgt, bool reset)` | Points **one axis** at a direction vector. **No roll control.** | Stock prograde/retro/normal/radial/target/maneuver buttons. |
| **Full orientation** | `SAS.LockRotation(Rotation)` / `LockRotation(QuaternionD)` — writes `VesselSAS.LockedRotation` | Full 3-axis attitude incl. roll. | StabilityAssist ("hold current"). |

**SAS Extended uses `LockRotation`** everywhere, because heading + pitch + roll fine control
requires commanding a full orientation, which `SetTargetOrientation` can't express.

- `VesselAutopilot.SetActive(true)` engages SAS (as `AutopilotMode.StabilityAssist`);
  `SetActive(false)` / `Deactivate()` turns it off.
- **Direction vectors** all come from `TelemetryComponent` (public via
  `vessel.SimulationObject.Telemetry` — the Redux `Assembly-CSharp` is not publicized, but this
  accessor, `Autopilot`, `SAS`, `mainBody`, `MOI` are all public, so no reflection is needed),
  refreshed via `RefreshAutopilotTelemetry()`:
  - Orbital: `OrbitMovementPrograde/Retrograde/Normal/AntiNormal/RadialIn/RadialOut`.
  - Surface: `SurfaceMovementPrograde/Retrograde/Velocity`, `HorizonNorth/South/East/West/Up/Down`.
  - Target: `TargetDirection`, `TargetPrograde/Retrograde` (= relative velocity), `TargetFrame`
    (live accessor, **throws `NullReferenceException`** with no target selected — guard on
    `HasTargetObject`, unlike the other telemetry fields which just hold a stale/zero value).
  - Maneuver: `ManeuverDirection` (degenerate zero vector when no node is planned — guard on
    `HasManeuver`).

### Redux SAS changes vs. vanilla

The Redux team substantially reworked `KSP.Sim.VesselSAS` (~959 → ~1601 decompiled lines) vs.
vanilla. Four themes, highest-priority first:

1. **High-Q damping — fixes vanilla's atmospheric-ascent SAS oscillation.** Above a dynamic-pressure
   blend band (default 100→400 kPa), Redux blends toward a gentler, integral-biased response.
2. **Torque-authority fix.** `GetTotalVesselTorque` now sums `Mathf.Abs` per axis instead of signed
   values, so opposing-torque parts no longer cancel out and understate available control authority.
3. **PID auto-tuning / auto-scalar rework**, adapting gain/clamp to the vessel's actual max angular
   acceleration.
4. **Diagnostics** — a `TraceFrame` ring buffer (`BeginTrace`/`EndTrace`/`DumpTrace`) plus public
   live-telemetry fields (`torque{X,Y,Z}`, `pid{Pitch,Roll,Yaw}{Raw,Integral,Ki}`, `dynPressureKpa`,
   etc.) if we ever want to surface SAS internals in our own UI.

**Implication:** because we drive SAS through `SAS.LockRotation`, all of the above runs
*underneath* our commanded orientation automatically — **do not reimplement attitude PID/damping
ourselves.** Our job is only to compute the target orientation (or, for Hover, target + throttle)
and hand it to Redux SAS.

To re-derive this diff against a newer Redux build: `ilspycmd -t KSP.Sim.VesselSAS "<dll>"` on
vanilla vs. Redux `Assembly-CSharp.dll`, then diff (decompiled copies are scratchpad-only, not
checked in).

## UI design

A single flight window (UXML + USS), styled like the stock KSP2 panels, opened via a Flight
appbar toggle button. Layout, top → bottom:

- **Header:** title "SAS EXTENDED" + a **settings button** (`#settings-button`) + close button.
  Draggable window; position persists across sessions (`Settings.WindowPositionX/Y`). The settings
  button swaps the window body between the normal SAS view and the Flight Axes settings panel — see
  "Flight Axes visuals" below.
- **Status line:** a single readout under the header, refreshed on `Settings.StatusRefreshInterval`
  (default 0.2s, disable via `Settings.StatusLoggingEnabled`). Content depends on mode: KillRot
  shows angular velocity, Hover shows vertical/horizontal speed (or throttle % if horizontal-vel
  cancel is off), everything else shows angle-to-target plus which H/P/R axes are free.
- **Top row (3 global modes):** `OFF`, `KILL ROT` (captures current attitude and holds it, no
  pointing), `NODE` (points at the maneuver node's burn vector; greyed out with no node planned).
- **Tabs:** `ORB` · `SURF` · `TGT` · `SPEC`, each showing that category's direction buttons.
  - **ORB:** Prograde, Retrograde, Normal+, Normal−, Radial+, Radial−.
  - **SURF:** S VEL+, S VEL−, SURF, H VEL+, H VEL−, UP.
  - **TGT:** TGT+, TGT−, R VEL+, R VEL−, PAR+, PAR− (all six greyed out with no target selected).
  - **SPEC:** Star+, Star− (parent-star pointing), `HOLD` (inertial hold), HOVER.
- **Offset rows (3):** `Heading`, `Pitch`, `Roll`. Each row = a per-axis enable toggle (green LED)
  + a numeric field (degrees) + `−`/`+` nudge buttons + two presets (`0` and `90`/`90`/`180`, Roll's
  second preset being 180°). Applied live as they change — no EXECUTE button. Per-mode H/P/R values
  are remembered for the session (`Settings.AttitudeOffsets`) and reapplied when you switch back to
  that mode. Swapped out entirely for **Hover's own panel** (target vertical speed + cancel-drift
  toggle) while Hover is active.
- **SAS mode color coding:** each ORB direction button's LED has an intrinsic color family
  (Prograde/Retrograde share one, Normal/AntiNormal another, Radial In/Out a third); the
  Heading/Pitch/Roll toggles pick up whichever family is currently active so offsets visually read
  as "relative to that direction."

Only one mode is active at a time; `MainWindowController.RegisterModeButton`/`ClearAllModeToggles`
enforce mutual exclusion across all tabs through one shared code path. The window also reacts to
`SASManager.Disengaged` (fired when SAS Extended turns itself off — vessel switch, node/target
lost, stock SAS toggled externally) by snapping the UI back to `OFF` rather than continuing to show
a mode that's no longer actually engaged.

### Per-axis offset toggle (Heading / Pitch / Roll LED) — semantics

Implemented uniformly (`SASManager.BuildPointingRotation`/`ApplyOffsets`/`GetCurrentOffsetAngles`)
across every pointing mode via one `LockRotation` per tick:

- **Toggle ON:** apply the entered angle (X/Y/Z = Heading/Pitch/Roll) as an offset on that axis.
- **Toggle OFF → the axis is genuinely free, not forced to 0.** Each refresh,
  `GetCurrentOffsetAngles` measures the vessel's *actual current* angle on that axis and commands
  that value straight back, so the autopilot applies ~no torque there and the vessel drifts freely
  on that axis (mirrors vanilla SAS's own `SetTargetOrientation`, which likewise only constrains
  2 DOF and leaves roll uncontrolled).
- `Hover` only ever exposes **Roll** to this mechanism — heading/pitch are pinned to the thrust
  direction and aren't user-configurable there.

## Flight Axes visuals (settings panel)

Optional in-world debug-style arrows/markers for the active vessel, toggled from a **settings panel**
reached via the header `#settings-button`. Modelled on Redux's own stock *Vessel Tools → Flight Axes*
debug feature — **we reuse the game's own debug-shape components and prefabs rather than rendering our
own**, so the visuals match the stock look for free. (To re-derive the reference implementation:
`ilspycmd -t DebugTools.Runtime.Controllers.VesselTools.VesselToolsWindowController "<Assembly-CSharp.dll>"`
plus the `DebugShapes*` component classes.)

**Rendering stack (all public in `Assembly-CSharp.dll`; `ShapesRuntime.dll` ships with the game and is
already in the asmdef):**
- `DebugShapesArrowComponent` — one arrow (`Shapes.Line` + `Shapes.Cone`); public `color`/`lineLength`.
- `DebugShapesAxesComponent` — three of the above as an RGB gizmo (`forward`/`up`/`right`).
- `DebugShapesObjectTracker` — the engine: each frame fires `OnUpdate(ITransformModel, SimulationObjectModel)`;
  our callback writes a sim-space position + rotation, then it maps sim→Unity world via
  `PhysicsSpace.PositionToPhysics/RotationToPhysics` and applies a `RotationOffset`.
- `DebugTools.Utils.DebugShapesSphereMarker` — wraps `DebugShapesDraw.Sphere` (the yellow CoM ball).
- Prefabs loaded by addressable key via `GameManager.Instance.Assets.Load<GameObject>(...)`:
  `Assets/Modules/DebugTools/Assets/DebugArrow.prefab` and `.../DebugAxes.prefab`.

**`FlightAxesVisualizer`** (a `MonoBehaviour` singleton on the same `SASExtended_Providers` object as
`SASManager`) owns all the visuals. It visualizes **only the active vessel**, rebuilding its instances
whenever the active vessel changes (switch/undock/revert) or flight is left/re-entered — each tick's
active vessel is compared against `_builtVessel`. The on/off flags persist across those rebuilds, so a
visual left on before a vessel switch comes back on the new vessel. Prefabs load async at startup; the
load callbacks re-run `RebuildForCurrentVessel` in case a toggle was flipped on before they arrived
(`Create*` no-op while their prefab is null; the CoM marker needs no prefab).

**Data-driven arrow registry.** Most visuals are plain direction/attitude arrows, so rather than a
copy-pasted block each, they live in a static `_arrowSpecs` dictionary keyed by toggle id — each spec is
`{ Color, Length, RequiresEngaged, AnchorAtCoM, TrackerSuffix, Func<VesselComponent, TelemetryComponent,
Rotation> }`. `CreateArrow`/`DestroyArrow` handle any of them generically (one closure captures the spec
and is stored on the live instance so it can be unsubscribed). The public entry point is
`SetVisual(string id, bool on)`; `ToggleIds` is the canonical id list the UI iterates. **Adding a new
arrow = one `_arrowSpecs` entry + its id in `ToggleIds`** (UI wiring and lifecycle are automatic).
Control axes and the CoM marker are the two special cases `SetVisual` dispatches outside the registry.

The visuals (currently implemented; **CoT/CoL deliberately deferred**, since `VesselComponent` exposes
`CenterOfMass` as one public field but has no thrust/lift-center equivalent):

| Toggle (`SideToggleControl`) | Visual | Source |
|---|---|---|
| `show-control-forward` / `-up` / `-right` | The vessel's **current** orientation, navball-aligned, split into three separately-toggleable 2m arrows: forward=blue, nose/up=white, right=red. One shared stock axes gizmo; each toggle enables/disables the prefab's own child arrow (so per-axis directions are exactly the game's). Anchored at the control point. | `vessel.ControlTransform.Position/Rotation` |
| `show-commanded-attitude` | **Orange arrow** (3m) along the nose direction SAS Extended is steering toward *right now*; hidden while OFF, lines up with the up/white control arrow when on target | `SASManager.CommandedRotation`, `RequiresEngaged` |
| `show-target-attitude` | **Violet arrow** (4m) along the mode's raw *final* target, before slew limiting; diverges from the commanded arrow during a reorientation and coincides once settled; hidden while OFF | `SASManager.TargetRotation`, `RequiresEngaged` |
| `show-orbital-prograde` | **Green arrow** (3m) from CoM along orbital prograde | `LookRotation(OrbitMovementNormal, OrbitMovementPrograde)` |
| `show-orbital-normal` | **Magenta arrow** (3m) from CoM along orbital normal | `LookRotation(OrbitMovementRetrograde, OrbitMovementNormal)` |
| `show-orbital-radial-in` | **Cyan arrow** (3m) from CoM along orbital radial-in | `LookRotation(OrbitMovementPrograde, OrbitMovementRadialIn)` |
| `show-surface-velocity` | **Gray arrow** (2.5m) from CoM along surface velocity | `LookRotation(cross(vel, HorizonUp), vel)` — perpendicular hint (see below) |
| `show-horizontal-velocity` | **Brown arrow** (2.5m) from CoM along the horizontal (radial-projected-out) component of surface velocity | `SurfaceMovementVelocity` minus its `HorizonUp` component |
| `show-com-marker` | Yellow sphere at center of mass | `vessel.CenterOfMass`, driven each frame |

Colors are all distinct (navball convention for the orbital arrows: prograde green, normal magenta,
radial-in cyan). For the attitude arrows the nested lengths (control 2m → commanded 3m → target 4m) tell
the reorientation story at a glance: target jumps to the goal, commanded slews toward it, the control
gizmo (real vessel) trails — all collapsing together once settled.

- **Navball offset trick:** the tracker renders `transform.rotation = physicsRotation * RotationOffset`
  with `RotationOffset = Euler(-90,0,0)`. That makes the arrow prefab's local +Z follow the fed
  `Rotation`'s **"up" (+Y) axis**, so (a) a full vessel-attitude `Rotation` fed straight in points along
  the nose (commanded/target), and (b) any direction `D` is drawn via `Rotation.LookRotation(hint, D)`.
  - **Gotcha (caused a real bug):** `LookRotation(forward, up)` sets +Z=`forward` *exactly* and
    +Y=`up` only *projected perpendicular to forward*. So the arrow points exactly along `D` **only when
    `hint` ⟂ `D`.** The orbital arrows satisfy this for free (two axes of the same orthonormal orbital
    frame) and the horizontal-velocity arrow does by construction (its direction is already ⟂ HorizonUp).
    Surface velocity originally used `OrbitMovementNormal` as the hint (as the stock debug tool does),
    which is *not* ⟂ the surface-velocity direction — skewing the arrow badly (most visible moving
    horizontally with ~0 vertical speed). Fixed by synthesizing a perpendicular hint via
    `cross(vel, HorizonUp)` (fallback `cross(vel, HorizonNorth)` when velocity is ~vertical).
- **Commanded vs. target arrow** — commanded feeds `CommandedRotation` (the *slew-limited* setpoint
  actually handed to `SAS.LockRotation`, i.e. what the autopilot is told to hold this tick); target
  feeds `TargetRotation` (`_rotation`, the mode's raw final goal before slew limiting). They only differ
  mid-reorientation.
- **Per-frame visibility gating** (`ComputeArrowVisible`, driven from the always-running `Update` loop —
  *not* an arrow's own handler, since a deactivated GameObject stops updating itself and could never
  re-show):
  - **`RequiresEngaged`** arrows (commanded/target) are hidden while SAS Extended is OFF — their source
    `Rotation` is stale otherwise.
  - **Velocity arrows** have a `Visible` speed gate (`MinVelocityArrowSpeed`, 0.05 m/s — a low noise
    floor so the arrow stays useful while nulling velocity for a precise landing): a near-zero
    velocity normalizes to an essentially random direction, so surface/horizontal arrows hide themselves
    at rest (e.g. a landed vessel — gear-spring jitter) rather than pointing at noise. Horizontal gates
    on the *horizontal* speed specifically, so a straight-down descent (real surface speed, ~no
    horizontal component) still hides it.
  - Arrows are hidden by deactivating the GameObject (not destroyed), so the condition reversing brings
    them straight back.

**Settings panel wiring (`MainWindowController`):** the toggles are **independent** on/off toggles (NOT
part of the mutually-exclusive `_allModeToggles` mode set — `ClearAllModeToggles`/
`OnSasManagerDisengaged` never touch them), wired in one loop over `FlightAxesVisualizer.ToggleIds`
(`WireFlightAxesToggles`) — each toggle's element name *is* its visual id. The header
`#settings-button` (`WireSettingsButton`/`ApplySettingsView`) swaps the body: when open it adds
`settings-button__background--checked` to `#settings-button__background`, sets `#upper-container`,
`#middle-container`, and `#footer` to `DisplayStyle.None`, and `#settings-container` to `Flex`; clicking
again reverses it. Every queried element is **null-guarded** so the window still builds while this UXML is being
authored. Toggle/panel states are **session-only** (not persisted to `Settings`) and reset to
off/main-view when the window is first created.

## Landing prediction visuals (settings panel)

Optional in-world visuals for the active vessel's predicted **unpowered coast**: a trajectory line
from the vessel down to the ground, plus a marker at the predicted impact point — one toggle
(`show-landing-prediction`, `LandingPredictionManager.ToggleId`) in the settings panel alongside the
Flight Axes toggles, persisted via `Settings.ShowLandingPrediction`. Modelled on KSP1 MechJeb2's
*Landing Guidance → Show Landing predictions*. **Pure ballistic/Keplerian coast, no drag/parachute
integrator** — renders on every body, including ones with an atmosphere, but atmospheric effects
are never modeled, so on an atmospheric body it's a rough indicator only (drifts further from
reality the more a real reentry's drag/heating actually matters), **always-on-top rendering**
(`_ZTest = Always`, `renderQueue = Overlay` — see
`FlightAxesVisualizer`'s CoM-marker precedent), **flight view only**. Full design rationale in
[`landing_prediction_plan.md`](landing_prediction_plan.md); the actual implementation deviates from
that plan in several load-bearing ways documented in
[`landing_prediction_fixes.md`](landing_prediction_fixes.md) — **read that file before changing
anything in `LandingPredictionManager.cs`**, several earlier, more "obvious" approaches were tried
in-game and demonstrably broken.

**Algorithm ("where do I land if I cut thrust now"):** every ~0.2s
(`LandingPredictionManager.ComputeIntervalSeconds`), numerically integrate (RK4, pure two-body
gravity) forward from the orbit's *live tracked* state vector (`orbit.localPosition`/
`relativeVelocity` — **not** `PatchedConicsOrbit.GetTruePositionAtUT`'s analytic Kepler-anomaly
reconstruction, confirmed unreliable for the near-radial/near-zero-angular-momentum orbits this
predictor lives in by definition) until the first terrain crossing. A cheap coarse pass first
brackets roughly when the crossing happens over a vis-viva-computed horizon (not
`orbit.period`/`orbit.eccentricity` — also unreliable in this regime), then a fine pass re-integrates
from scratch with a step size scaled to that estimate, so a multi-minute coast and a 5-second
suborbital hop both render an equally smooth ~200-sample curve. Each terrain-altitude check
(`GetAltitudeFromTerrain`) de-rotates the query position backward by the angle the body will have
swept forward by that sample's time — a necessary correction since that call is a snapshot-*now*
query with no future-time concept, and the body keeps rotating under the falling vessel while the
integrator looks ahead.

**Rendering (the load-bearing gotcha):** every sample and the impact point are stored as
**de-rotated offset vectors relative to the vessel's own position at compute time**, not absolute
positions. `UpdateVisualPositions` (every render frame, not throttled) re-queries the vessel's
*current* position fresh and converts it via `PositionToPhysics`, then converts each cached offset
via `PhysicsSpace.VectorToPhysics` and adds them together. Caching an *absolute* position and
re-converting it every frame via `PositionToPhysics` looks correct but drifts out of sync with the
game's own continuous floating-origin re-anchoring between recomputes (the vessel's own on-screen
position is re-anchored every frame; a cached absolute conversion was not) — see
`landing_prediction_fixes.md` round 9/10 for the full diagnosis. **Anchor to the vessel's live
position and only cache/convert relative offsets — never cache an absolute `PositionToPhysics`
result across more than one frame.**

## Hover mode (SPEC → Hov) — throttle control

Unlike every other mode (orientation-only via `LockRotation`), **Hover also drives the throttle.**
It points the thrust axis up, blended against horizontal surface velocity to null it (unless
`CancelHorizontalVelocity` is toggled off), and modulates throttle to hold `HoverTargetVerticalSpeed`
(a signed m/s setpoint, **not** an altitude lock — see `SASManager`'s Hover field-block comment for
why). Both `HoverTargetVerticalSpeed` and `CancelHorizontalVelocity` are user-set and **persist
across engage/disengage** (session-only, not reset to a default on every `SetHover()`). The
throttle law (`HoverThrottleMath.Step`, called from `SASManager.UpdateHoverThrottle`) is a
gravity-compensated P+I+D controller on vertical-speed error; the integral self-tunes to the
vessel's own hover-equilibrium throttle, so **no per-vessel thrust/mass/TWR model is needed.** Tilt
authority (how far it leans to cancel drift) and the tilt-angle safety ceiling both self-tune off
that same integral too, rather than using flat per-vessel constants. Gains are public tunables
(`Hover*` fields on `SASManager`).

**Why a Harmony patch is required for throttle:** the stock `FlightInputHandler` keeps a persistent
`_flightCtrlState.mainThrottle` and pushes it to the active vessel every `FixedUpdate`, so setting
throttle from our `Update` loop gets overwritten. `Patches/FlightInputHandlerThrottlePatch`
postfixes `FlightInputHandler.UpdateFlightControlState` and, while `AttitudeMode == Hover`,
overwrites `mainThrottle` after player input is applied but before the push, so the commanded
throttle sticks. Throttle is left sticky on disengage (matches stock behaviour). Engaging Hover
commands throttle but does not auto-stage — engines must already be activated.

Hover went through an 11-round instability debugging cycle (throttle bang-bang, tilt-cap, and
gravity-compensation bugs across low-gravity and high-gravity/high-TWR test cases) — full log in
[`hover_mode_fixes.md`](hover_mode_fixes.md). Read it before touching the tilt/throttle control law
again; don't rediscover those failure modes from scratch.

## Offset math (final design)

All in `SASManager.cs` / `PureMath/AttitudeMath.cs`:

1. `Rotation.LookRotation(target, upwards)` aligns local **up** (the vessel's nose axis, after a
   trailing `Euler(90,0,0)` remap) exactly with `target`, for any target/upwards pair — zero error,
   no gimbal-lock caveat. Used by `BuildPointingRotation` for every ORB/SURF/TGT/SPEC case.
2. User offsets are applied as
   `localRotation * QuaternionD.Euler(-Pitch, Heading, Roll) * QuaternionD.Euler(90,0,0)`
   (`ApplyOffsets`, shared by every pointing mode including `Hold`).
3. A disabled axis is measured live off the vessel's current attitude instead of pinned to 0 — see
   "Per-axis offset toggle" above (`GetCurrentOffsetAngles`).
4. `SetRotation()` calls `_telemetry.RefreshAutopilotTelemetry()` first and reframes every telemetry
   direction vector into `HorizonNorth`'s coordinate system before use — mixing raw vectors across
   coordinate systems un-reframed was the root cause of an early Normal/Radial-swap bug (Normal and
   RadialIn/Out come from a frame drastically rotated relative to the horizon frame, unlike
   Prograde/Retrograde whose source frame happens to sit close to it).
5. `KillRot` and `Maneuver` don't use the pointing pipeline at all:
   - `KillRot` captures `_vessel.ControlTransform.Rotation` once at engage time and holds it every
     tick — mirrors vanilla `StabilityAssist`.
   - `Maneuver` points at `_telemetry.ManeuverDirection` through the normal pipeline, guarded by
     `HasManeuver`.
6. Fed to `SAS.LockRotation` on an adaptive refresh interval (`RefreshInterval_short/mid/long`,
   keyed off angle-to-target).
7. `Hold` (SPEC → HOLD) goes through the same H/P/R trim path as the pointing modes, but its "look"
   is a one-time snapshot instead of a value recomputed every tick: `SetHold()` captures
   `vessel.ControlTransform.Rotation` reframed into the game's non-rotating universe inertial frame
   (`GameManager.Instance.Game.UniverseModel.inertialReferenceFrame`), **not**
   `ControlTransform.Rotation`'s own frame directly. That distinction is the whole point of the
   mode: `ControlTransform`'s own frame is body/celestial-relative (reparented on SOI change), so
   holding a captured `localRotation` against it (what `KillRot` does) would silently drift as the
   reference body rotates/orbits — reframing into the universe inertial frame first is what makes
   Hold genuinely fixed in space rather than `KillRot` under another name.
8. **Attitude slew-rate limiting** (`AdvanceCommandedRotation`/`AttitudeSlewMaxRate`, default
   60°/s) smooths the commanded setpoint so a near-antipodal mode switch (e.g. Prograde →
   Retrograde) doesn't hand Redux's `SAS.LockRotation` a raw 180°-flipped target in one tick — that
   used to trigger hard oscillation, since a single-tick near-antipodal target change is the
   classic ill-conditioned case for quaternion attitude control. Each tick, `_commandedRotation` is
   anchored to the vessel's *own current attitude* (not its own previous value — anchoring to
   itself let the setpoint outrun the real, inertia-limited vessel) and moved up to
   `AttitudeSlewMaxRate` toward the true target via `KSP.Sim.QuaternionD.RotateTowards`. The
   anchor's coordinate frame is **not** constant across modes (pointing modes use
   `HorizonNorth`'s frame; `KillRot`/`Maneuver`'s fallback use `ControlTransform`'s frame; `Hold`
   uses the universe inertial frame), so the reframe happens unconditionally every tick rather than
   being cached.

**Diagnostics:** `SASManager` logs through ReduxLib's per-class logger (`SASExtended|SASManager`):
`LogInfo` on every mode change with a ground-truth heading/pitch/roll/altitude/vertical-speed/
angular-velocity snapshot; a `LogDebug` line at the end of every tick (gated behind
`Settings.VerboseLoggingEnabled`) adding applied H/P/R, angle-to-target, `commandedAngleToTarget`/
`slewCapDeg`, and the raw direction vectors — so orientation/hover/slew-rate issues can be diagnosed
from `Player.log` directly rather than re-deriving everything through decompilation.

`SASManager` also auto-disengages (falls back to `OFF`, fires the `Disengaged` event so the UI
follows) on: active vessel change (switch/undock/revert/scene-exit), the maneuver node or target
disappearing while `Node`/a `TGT`-mode is active, and stock SAS being toggled externally (e.g. the
player pressing the stock `T` key or clicking the stock SAS button) — the last case deactivates our
control but deliberately does **not** call `Autopilot.Deactivate()` again, since the external change
already did that.

## Build / deploy

Standard template flow (CLAUDE.md → Conventions): build via **ThunderKit pipelines in the Unity
Editor** — `Assets/SASExtended/Pipelines/` has *Build for Editor*, *Build for Player*, *Deploy to
Zip File*. Claude can't drive the editor; ask the user to run pipelines. Output stages to
`Assets/Mods/__Testing/SASExtended/`. Before building, **UI Authoring Mode must be off** — see
`custom_uxml_controls.md` (pipelines refuse to build otherwise). SAS Extended is a runtime
autopilot mod and currently ships no PatchManager Lua patches.

## Status

**All modes implemented and in-game tested, working:** `OFF`, `KILL ROT`, `NODE`; ORB (all 6);
SURF (all 6); TGT (all 6, including PAR against a docking port); SPEC Star+/−, `HOLD`, `Hover`
(throttle + tilt, validated across low-gravity and high-gravity/high-TWR vessels — see
`hover_mode_fixes.md`). Heading/Pitch/Roll offsets including free-axis-when-disabled, per-mode
offset memory, window position/open-state persistence, the status readout, SAS mode color coding,
and auto-disengage on vessel change / lost node-target / external stock-SAS toggle are all
implemented and working.

**Flight Axes visuals — implemented, pending in-game verification.** The full set — split control axes
(fwd/up/right), commanded/target attitude arrows, orbital prograde/normal/radial-in, surface &
horizontal velocity arrows, and the CoM marker (`FlightAxesVisualizer`) — plus the
settings-panel/settings-button wiring is written but not yet built/run. Confirm on the next build:
(1) the two stock debug prefabs actually load from a mod context (internal debug assets — watch for
`logMissingKey` warnings in `Player.log`; CoM needs no prefab so it works regardless); (2) the attitude
arrows point along the nose as derived (engage e.g. Prograde + a Heading offset: the violet target arrow
jumps to the goal while the orange commanded arrow slews onto the white up/control arrow); (3) the direction
arrows point the right way (prograde/normal/radial-in/surface-velocity against the navball). The
settings-panel UXML (`#settings-container`, `#settings-button`, and the eleven `show-*` toggles in
`FlightAxesVisualizer.ToggleIds`) is authored by the user, not shipped in this codebase yet.

**Landing prediction visuals — implemented and in-game tested, working**, on branch
`feature/landing-prediction-visuals`. Trajectory line + impact marker render correctly for airless
bodies, including the near-zero-horizontal-velocity case and the near-radial (falling almost
straight down) regime that broke several earlier approaches — see `landing_prediction_fixes.md` for
the 10-round debugging log before touching `LandingPredictionManager.cs` again.

Build-constraint reminder: the Unity asmdef compiles at **C# 9.0** (Unity 6000.4.1f1) — no
file-scoped namespaces, no `with` on structs. See [[langversion-csharp9]].
