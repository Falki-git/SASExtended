# Landing prediction visuals — implementation plan

Flight-view landing-prediction visuals for SAS Extended, modelled on KSP1 MechJeb2's
*Landing Guidance → Show Landing predictions*. Written to be implemented directly.

## Goal

When the player enables a single toggle, show in the **flight view**:

1. A **trajectory line** leading from the vessel along its predicted coast path down to the ground.
2. A **landing marker** on the ground at the predicted impact point.

MechJeb reference visuals: a red trajectory line + a ground marker (see the design screenshots in
the request). We reproduce the *idea*, not MechJeb's rendering tech.

## Locked scope decisions (agreed with the user)

- **Airless bodies only** (Phase 1). Pure ballistic/Keplerian coast to terrain impact. No drag/
  parachute integrator. On a body with an atmosphere (periapsis inside the atmosphere) we simply
  **show nothing** — mirrors MechJeb's `NO_REENTRY` display outcome. Atmospheric descent is a
  **future, separate task** (would need a `ReentrySimulation`-style integrator).
- **Always-on-top rendering** (no depth occlusion / no horizon culling). `_ZTest = Always`,
  `renderQueue = Overlay`. This is what Orbital Survey does and it lets us drop MechJeb's
  `IsOccluded` math entirely.
- **Flight view only.** Not map view. (Map view would need `Map3DSpaceProvider` instead of
  `PhysicsSpace`; explicitly out of scope.)
- **Coast prediction** = "where I land if I cut thrust now." While hovering/burning it just keeps
  updating live. Same semantics as MechJeb (which predicts the unpowered trajectory).

## North-star mapping (MechJeb → KSP2)

| MechJeb piece | KSP2 / SAS Extended equivalent |
|---|---|
| `ReentrySimulation` (threaded drag integrator) | **Keplerian coast-to-impact** off the vessel's existing `PatchedConicsOrbit`. No thread, no integrator needed for airless bodies. |
| `GLUtils.DrawPath` (`GL.LINES`, screen-projected, manual horizon cull) | Unity **`LineRenderer`** in flight world space, points refreshed every frame. |
| `GLUtils.DrawGroundMarker` (`GL.TRIANGLES` tri-blade) | A **`DebugShapesSphereMarker`** placed at the impact point each frame (v1). |
| "Show landing predictions" toggle | One `SideToggleControl` in a new UXML panel, wired in `MainWindowController`. |

**Why we do not port `GLUtils`:** KSP2 renders through **HDRP (SRP)**; `GL.LINES`/`OnPostRender`
do not work the way they did in KSP1's built-in pipeline. `LineRenderer` + the game's debug-shape
components integrate with SRP automatically.

## In-repo precedents to copy (READ THESE FIRST)

This is the most important section — almost everything needed already exists in two nearby mods.

### A. `Assets/SASExtended/Code/Managers/FlightAxesVisualizer.cs` (THIS repo)
SAS Extended already draws flight-view world visuals. Copy its conventions wholesale:
- MonoBehaviour with `public static Instance`, `Awake`/`OnDestroy`, ReduxLib per-class logger
  (`"SASExtended|LandingPredictionVisualizer"`).
- Lives on the existing persistent `SASExtended_Providers` GameObject (spawned in
  `SASExtendedPlugin.OnInitialized`) — add our component there next to `SASManager` and
  `FlightAxesVisualizer`.
- Sim→Unity conversion accessor, verbatim:
  ```csharp
  private static IPhysicsSpaceProvider PhysicsSpace =>
      GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;
  private static VesselComponent ActiveVessel =>
      GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
  ```
- **`PhysicsSpace.PositionToPhysics(Position) → Vector3`** is THE sim→flight-scene-world bridge.
  `FlightAxesVisualizer` uses it to drive its CoM sphere marker each frame:
  ```csharp
  _comMarker.SetCenter(PhysicsSpace.PositionToPhysics(_builtVessel.CenterOfMass));
  ```
  Our marker + every trajectory line point use exactly this call, refreshed **every frame**
  (floating origin snaps — sim Positions are stable, their Unity projection is not).
- **Rebuild-on-vessel-change**: each `Update`, compare `ActiveVessel` to a cached `_builtVessel`;
  on change (switch/undock/revert/flight-exit → null) tear down and rebuild. Copy this pattern.
- **Loading a stock prefab** (used there for arrows/axes) via async asset callback:
  ```csharp
  GameManager.Instance.Assets.Load<GameObject>(key, obj => { ...; RebuildForCurrentVessel(); },
      logMissingKey: true);
  ```
  The **CoM sphere marker needs no prefab** — it is `go.AddComponent<DebugShapesSphereMarker>()`
  then `SetEnabled(true)`, positioned each frame via `SetCenter(...)`. Our landing marker copies
  this exactly (`DebugTools.Utils.DebugShapesSphereMarker`, default 0.5 m yellow — tune size).

### B. `E:\GitHub\KSP2\OrbitalSurveyRedux\Assets\OrbitalSurvey\Code\UI\GroundTrackingRenderer.cs`
Orbital Survey's map cone renderer. Copy its **`LineRenderer` + material mechanics** for the
trajectory polyline (it's map-view, so ignore its map-specific parenting/space-provider bits):
- SRP-safe material — **confirmed present in KSP2 runtime**:
  ```csharp
  var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
  var mat = new Material(shader);
  mat.color = color;
  mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); // always on top
  mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
  ```
- `LineRenderer` is `[DisallowMultipleComponent]` → one per GameObject.
- Pre-allocate the `Vector3[]` and use `lr.SetPositions(array)`; no per-frame LINQ/allocs.
- Cache the `Material` reference directly (not `Renderer.material`, which instantiates a copy).
- Explicitly `Destroy` the GameObject, `Mesh`, and `Material`s on teardown to avoid leaks.
- Throttle expensive recompute while keeping the cheap per-frame position refresh (OS refreshes
  orbital-plane normal every 1 s; we recompute the impact/samples every ~0.2 s — see below).

**Difference for our trajectory line vs OS:** use `useWorldSpace = true` and write world positions
straight from `PositionToPhysics` each frame (OS used local space only because its map space
provider hands back map-local coords). Do **not** parent line points to a moving transform.

## Confirmed KSP2 APIs (verified against `Packages/KSP2_x64/Assembly-CSharp.dll`)

- Current time: `GameManager.Instance.Game.UniverseModel.UniverseTime` (double UT).
- Active vessel: `Game.ViewController.GetActiveSimVessel()` → `VesselComponent`.
- Orbit: `vessel.Orbit` (`KSP.Sim.impl.PatchedConicsOrbit`):
  - `Position GetTruePositionAtUT(double UT)` — sample points for the line (returns a `Position`).
  - `double PeriapsisArl` — periapsis altitude above sea-level radius → early-out "does not reenter".
  - `double TrueAnomalyAtRadius(double R)`, `double GetUTforTrueAnomaly(double ta, double wrapAfterSeconds)` — first impact-time guess.
  - `double period`, `StartUT`, `EndUT`, `referenceBody`.
- Body (`vessel.mainBody`, `CelestialBodyComponent`):
  - `bool hasSolidSurface`, `double radius`, `double MaxTerrainHeight`, `double MinTerrainHeight`.
  - `void GetLatLonAltFromRadius(Position pos, out double lat, out double lon, out double altFromRadius)`.
  - `void GetAltitudeFromTerrain(Position position, out double terrainAltitude, out double sceneryOffset)`.
  - `double GetAltitudeFromRadius(Position position)`.
  - `Vector3d GetSurfaceNVector(double lat, double lon)` — surface normal (if marker orientation is wanted later).
- Sim→flight-scene-world: `Game.UniverseView.PhysicsSpace.PositionToPhysics(Position) → Vector3d`
  (also `PhysicsToVector`, `PhysicsSpaceTransform`).
- Atmosphere check: prefer a body atmosphere flag if present; otherwise treat "airless" as
  `body.hasSolidSurface && <no atmosphere>`. During implementation confirm the exact atmosphere
  accessor on `CelestialBodyComponent` (e.g. an `hasAtmosphere`/atmosphere-depth field) via
  `ilspycmd -t KSP.Sim.impl.CelestialBodyComponent`. If a body has an atmosphere, bail (Phase 1).

## Architecture / files

| File | Change |
|---|---|
| `Code/Managers/LandingPredictionManager.cs` | **New.** MonoBehaviour on `SASExtended_Providers`. Owns: enabled state, throttled impact+trajectory compute, the `LineRenderer` trajectory, the `DebugShapesSphereMarker` landing marker, rebuild-on-vessel-change, teardown. Modelled on `FlightAxesVisualizer`. |
| `Code/SASExtendedPlugin.cs` | **Edit** `OnInitialized`: `providers.AddComponent<LandingPredictionManager>();` alongside the existing `SASManager`/`FlightAxesVisualizer`. |
| `Code/UI/MainWindowController.cs` | **Edit.** Add a `WireLandingPredictionToggle()` (mirrors `WireFlightAxesToggles`) that finds the new `SideToggleControl` and routes clicks to `LandingPredictionManager.Instance.SetEnabled(bool)`. Reflect persisted state on enable. |
| `Code/Utilities/Settings.cs` | **Edit.** Add `ConfigValue<bool> ShowLandingPrediction`, bound in `Initialize()` (persisted, e.g. section `"Landing prediction"`). |
| `UI/SASExtended.uxml` | **User authors** the new panel + `SideToggleControl` (name e.g. `show-landing-prediction`). Verbatim-UXML rule applies — do not restyle. |

Keep the compute in the manager (it's mostly live-orbit API calls, frame-aware — not a good fit
for the `PureMath/` tested-in-isolation split). If any genuinely pure geometry emerges, put it in
`PureMath/` with edit-mode tests per the repo convention.

## Component detail

### 1. Compute — impact point + trajectory samples
Run on a throttle (~0.2 s, reuse the cadence idea from `Settings.StatusRefreshInterval`; store
`_nextComputeTime = Time.time + interval`). Cache results as **sim `Position`s** (frame-independent);
re-project to Unity every frame in the visual update.

Algorithm (airless):
1. Guard: enabled, in flight, `ActiveVessel != null`, not `LandedOrSplashed`, body `hasSolidSurface`
   and airless. Else hide visuals.
2. `orbit = vessel.Orbit`. If `orbit.PeriapsisArl > body.MaxTerrainHeight` → no impact (does not
   reenter) → hide visuals.
3. Find impact UT by **marching forward** from `now` along the orbit in adaptive UT steps: at each
   step `p = orbit.GetTruePositionAtUT(ut)`, `body.GetAltitudeFromTerrain(p, out terrainAlt, out _)`,
   track altitude-above-terrain; when it crosses ≤ 0, **bisect** the last bracket a handful of
   iterations for a precise impact UT + `Position`. (Marching + bisection is robust to terrain
   varying with lat/lon; `TrueAnomalyAtRadius(body.radius)` is only a coarse first guess.)
4. Sample the orbit `now → impactUT` into ~200–400 `Position`s → trajectory polyline (store array).
5. Store impact `Position`. Clamp the last trajectory sample to the impact point.

Edge cases to handle: already sub-orbital / periapsis behind us, timewarp (consider freezing or
hiding above some warp factor), no reentering patch.

### 2. Trajectory line (`LineRenderer`)
- One child GameObject with a `LineRenderer`, `useWorldSpace = true`, red, constant world width
  (meters — pick ~ a few meters; **not** OS's 0.012 which was map-local). Material via the OS recipe
  above (always-on-top).
- Each frame: `lr.positionCount = samples.Length;` fill a pre-alloc `Vector3[]` with
  `(Vector3)PhysicsSpace.PositionToPhysics(samples[i])`, then `lr.SetPositions(arr)`.
- Hide (disable GO) when compute produced no valid impact.

### 3. Landing marker (`DebugShapesSphereMarker`)
- v1: exactly like `FlightAxesVisualizer`'s CoM marker — `new GameObject("SASX_LandingMarker")`
  parented to the manager, `AddComponent<DebugShapesSphereMarker>()`, `SetEnabled(true)`, and each
  frame `marker.SetCenter(PhysicsSpace.PositionToPhysics(impactPosition))`. Tune radius/color
  (MechJeb marker is blue; pick something readable).
- Optional later polish (not v1): a flat ring/chevron via a second `LineRenderer`, oriented to the
  surface normal (`body.GetSurfaceNVector(lat,lon)` → `PhysicsSpace.PhysicsToVector`), for a more
  MechJeb-like pin. Note `DebugShapesDraw.Line` is only a single-segment immediate helper — there is
  **no** multi-point polyline debug component, so any custom marker shape uses `LineRenderer`/mesh.

### 4. UI toggle wiring
Mirror `MainWindowController.WireFlightAxesToggles` (see that method + `WireSettingsButton`):
- In `OnEnable`, `var t = _root.Q<SideToggleControl>("show-landing-prediction");` null-guard it
  (log a warning + skip if not authored yet — the UXML may not have it during authoring).
- `t.SetEnabled(true);` then set its visual state from `Settings.ShowLandingPrediction.Value`
  (`SwitchToggleState(value, false)`).
- `t.RegisterCallback<ClickEvent>(evt => { if (!t.IsEnabled) return; bool on = t.IsToggled;
  Settings.ShowLandingPrediction.Value = on; LandingPredictionManager.Instance?.SetEnabled(on); });`
- This is an independent feature toggle — **NOT** an `AttitudeMode`. Do **not** route it through
  `RegisterModeButton`/`ClearAllModeToggles`/`_allModeToggles`.

### 5. Settings persistence
Add to `Settings.cs` (pattern identical to existing binds):
```csharp
public static ConfigValue<bool> ShowLandingPrediction;
// in Initialize():
ShowLandingPrediction = new(Plugin.SWConfiguration.Bind(
    "Landing prediction", "Show landing predictions", false,
    "Show a predicted coast trajectory and ground impact marker in flight (airless bodies)."));
```
`LandingPredictionManager` reads this on startup so the visuals restore if the player left it on.

### 6. Lifecycle
- `SetEnabled(bool)`: store flag; if false, tear down visuals; if true, allow Update to build.
- Flight-only: the plugin already tracks flight via `GameStateChangedMessage` (FlightView/Map3DView).
  Tear down visuals when **leaving FlightView or entering Map3D** (PhysicsSpace/flight-scene objects
  are invalid there). Simplest: also gate `Update` on being in FlightView and destroy child GOs when
  not. Follow `FlightAxesVisualizer`'s rebuild/teardown discipline.
- `OnDestroy`/teardown: `Destroy` line GO + material, destroy marker GO. Null out `Instance`.

## Non-goals / deferred

- Atmospheric-body descent prediction (drag/parachutes) — future task.
- Map-view rendering.
- Depth-buffer occlusion / horizon culling (using always-on-top instead).
- Predicting the *powered* descent path or a targeted landing site / autoland (MechJeb's autopilot).
- Aerobrake nodes, error simulations, touchdown-speed readouts (MechJeb extras).

## Open details for the implementer to confirm

1. Exact atmosphere accessor on `CelestialBodyComponent` for the airless guard
   (`ilspycmd -t KSP.Sim.impl.CelestialBodyComponent`).
2. Flight-scene **layer** for the line GameObject (OS used `"Map"`; pick the flight equivalent so it
   renders in the flight camera — verify).
3. `DebugShapesSphereMarker` API surface (`SetEnabled`, `SetCenter`, radius/color fields) — confirm
   against `ilspycmd -t DebugTools.Utils.DebugShapesSphereMarker` and how `FlightAxesVisualizer`
   uses it.
4. Trajectory sample count / line width tuning for readability at Mun/Minmus scales.
5. Timewarp behavior (freeze vs hide) once tested in-game.

## Build / test

- Build via **ThunderKit pipelines in the Unity editor** (Build for Editor) — Claude can't drive the
  editor; ask the user to run pipelines. UI Authoring Mode must be off.
- Runtime log (mod's ReduxLib output + errors):
  `C:\Users\gfalk\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Player.log` (overwritten
  each run). Log a per-class debug line on compute (impact lat/lon/UT, sample count) gated behind
  `Settings.VerboseLoggingEnabled` to match the mod's diagnostics style.
- Test target: a sub-orbital / descending vessel over an airless body (Mun/Minmus). Verify the line
  follows the coast path and the marker sits at the impact point; verify it hides on atmospheric
  bodies, when landed, and when the orbit doesn't reenter.

Build constraint: Unity asmdef compiles at **C# 9.0** — no file-scoped namespaces, no `with` on
structs (see `[[langversion-csharp9]]`).
