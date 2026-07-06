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
- **Top row (3 global modes):** `OFF`, `KILL ROT` (kill rotation / StabilityAssist damping),
  `NODE` (point at maneuver node — currently stubbed to a debug "Horizon" call in the prototype).
- **Tabs:** `ORB` · `SURF` · `TGT` · `SPEC`. Selecting a tab shows that category's direction
  buttons. (MechJeb's equivalent row is OBT/SURF/TGT/ADV.)
  - **ORB:** Prograde, Retrograde, Normal+, Normal−, Radial+, Radial−.
  - **SURF:** S VEL+, S VEL−, SURF, H VEL+, H VEL−, UP.
  - **TGT:** TGT+, TGT−, R VEL+, R VEL−, PAR+, PAR−.
  - **SPEC:** Star+, Star− (parent-star pointing; misc/experimental).
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

The green LED next to each axis is **context-dependent**:

- **Usual case — offset enable/disable:** toggle ON = apply the entered angle as an offset on
  that axis; toggle OFF = offset treated as 0, axis holds the base direction. (Legacy has
  `XEnabled/YEnabled/ZEnabled` fields intended for this but doesn't yet honor them in the math.)
- **"Free axis" case:** for base modes that have **no inherent full orientation** — chiefly
  **SURF** — the orientation is defined *entirely* by the enabled offsets. If the user enables
  only Heading and Pitch and leaves Roll off, Roll is left **free** (the vessel keeps whatever
  roll it naturally has) rather than forced to 0. This means SURF (and similar) cannot always use
  a single full `LockRotation`; a freed axis needs partial-axis control (e.g. command only the
  axes that are enabled). Design this carefully — it's the main subtlety separating SAS Extended
  from a naive full-orientation lock.

## v1 scope

**First working release = the ORB tab, fully functional** — the six orbital directions plus
working Heading/Pitch/Roll offsets, engaged/disengaged via OFF, with SAS actually holding the
computed orientation on a real vessel. SURF / TGT / SPEC and NODE (maneuver) are **roadmap**,
documented above but not required for v1. (The legacy prototype is furthest along on exactly the
ORB modes.)

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

## Offset math (design intent, not legacy-verbatim)

Concept from the prototype's ORB modes, to be re-derived cleanly:
1. Build a base orientation aligned to the local horizon: `Rotation.LookRotation(north, up)`, then
   correct axes with a `QuaternionD.Euler(90,0,0)` so "up-of-vessel" maps correctly.
2. Compute the base direction's **heading** and **pitch** on the horizon plane (signed angle vs.
   north around up; `asin` of the dot with up), and rotate the base orientation to face it.
3. Apply the user offsets in a fixed order — **Heading → Pitch → Roll** (order matters) — as
   `QuaternionD.AngleAxis` rotations about the appropriate local axes.
4. Feed the result to `SAS.LockRotation` each refresh. Use an **adaptive refresh interval**
   (shorter when far from target, longer when close) to avoid jitter — the prototype's
   `RefreshInterval_short/mid/long` keyed off `GetAngleToRotation()`.

Validate against actual in-game behavior — the prototype has known-wrong cases (e.g. Star modes
"doesn't work", NODE stubbed). Roll handling and the "free axis" SURF case need fresh design.

## Build / deploy

Standard template flow (CLAUDE.md → Conventions): build via **ThunderKit pipelines in the Unity
Editor** — `Assets/SASExtended/Pipelines/` has *Build for Editor*, *Build for Player*, *Deploy to
Zip File*. Claude can't drive the editor; ask the user to run pipelines. Output stages to
`Assets/Mods/__Testing/SASExtended/`. Game content changes (if any) go through **PatchManager Lua
patches** in `Copied/Patches/`, not a C# patch API — though SAS Extended is primarily a runtime
autopilot mod and may need none.

## Status

Fresh scaffold — `SASExtendedPlugin.cs` is still the template "Hello World"; `Definitions/` and
`Copied/` are empty. No code ported yet. **Next task: begin porting the legacy files above,
starting with the ORB-tab path (entry point → SASManager equivalent → window).**
