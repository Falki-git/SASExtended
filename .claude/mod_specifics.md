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
throttle**. It points the vessel thrust-axis up, blended against horizontal surface velocity to null it
(unless the user has toggled `CancelHorizontalVelocity` off), and modulates throttle to hold
`HoverTargetVerticalSpeed` (a signed m/s setpoint - **not** an altitude lock; see below for why). Both
`HoverTargetVerticalSpeed` and `CancelHorizontalVelocity` are user-set via the hover-controls UI and
**persist across engage/disengage** - `SetHover()` deliberately does not reset them back to a default
(0 / on) on every engage, so the player's last setting sticks. The throttle law
(`SASManager.UpdateHoverThrottle`) is a full P+I+D controller on vertical-speed error; the integral
self-tunes to the vessel's hover throttle so **no per-vessel thrust/mass/TWR model is needed**. Gains
are public tunables (`Hover*` fields on `SASManager`).

### Hover mode — instability fixes

Hover mode went through an 11-round debugging cycle on branch `hover-fix` (2026-07-09/2026-07-10),
fixing throttle bang-bang, tilt-cap, and gravity-compensation bugs across low-gravity (Minmus) and
high-gravity/high-TWR (Kerbin, Tylo) test cases, plus removing an unsafe "escape valve" subsystem.
**Full round-by-round diagnosis and fix log:** [`hover_mode_fixes.md`](hover_mode_fixes.md). The
only still-pending item: a UI toggle for whether to cancel horizontal velocity at all (no UXML
control wired up yet).

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
7. `Hold` (SPEC → HOLD, added 2026-07-11 per `.claude/enhancement_roadmap.md` item 1's "inertial
   hold" recommendation) *does* go through the same H/P/R trim path as the pointing modes
   (`ApplyOffsets` — factored out of `BuildPointingRotation` so both share it), but its "look" is a
   one-time snapshot instead of a value recomputed from telemetry every tick: `SetHold()` captures
   `vessel.ControlTransform.Rotation`, reframed into the game's actual non-rotating universe frame
   (`GameManager.Instance.Game.UniverseModel.inertialReferenceFrame.inertialReferenceFrame` —
   confirmed via decompile to be the same frame `VesselComponent.ParentToInertialReferenceFrame()`
   uses), **not** `ControlTransform.Rotation`'s own coordinateSystem directly. That distinction is
   the whole point of the mode: `ControlTransform`'s own frame is body/celestial-relative (it's
   reparented on SOI change — `base.transform.parent = newReferenceBody.transform.celestialFrame`),
   so holding a captured `localRotation` against it (which is what `KillRot` does) would silently
   drift as the reference body rotates/orbits — reframing into the universe inertial frame first is
   what makes Hold genuinely fixed in space instead of just `KillRot` under another name. The
   snapshot is pre-multiplied by `QuaternionD.Euler(-90,0,0)` at capture time so it cancels exactly
   against `ApplyOffsets`'s trailing `Euler(90,0,0)` (both are pure-X rotations, so they commute)
   and reduces to the captured attitude unchanged when all three offsets are 0.

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
