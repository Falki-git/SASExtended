# Code review — performance, structure, crash-safety (2026-07-17)

Scope: all of `Assets/SASExtended/Code/**` (~3,700 lines, 16 C# files), reviewed against
`feature/landing-prediction-visuals` @ `8c19cc2`. Goal: 10 improvement recommendations covering
**performance**, **cleaner structure / separation of concerns**, and **potential crash scenarios**.

Every load-bearing claim below was verified against decompiled game/ReduxLib code (`ilspycmd`)
rather than inferred from names or comments — where that verification changed a conclusion, it is
called out inline.

> **Relationship to [`code_review_2026-07-14.md`](code_review_2026-07-14.md):** that pass covered
> robustness/edge cases. This one overlaps on exactly one item — `GetParentStar` (its #2, this
> doc's #2), which is **still unfixed**. Its top recommendation (a top-level try/catch on every
> `Update()`) remains the correct backstop for everything here and is *not* repeated below; treat
> the two documents as complementary.

## Contents

| # | Status | Recommendation | Category |
|---|---|---|---|
| 1 | DONE | Resolve the vessel once per tick instead of 63 times | Crash / perf |
| 2 | DONE | `GetParentStar` returns null → per-tick NRE | Crash |
| 3 | DONE | ~63 unguarded `_root.Q<…>()` calls in `OnEnable` | Crash |
| 4 | DONE | Event + singleton lifecycle asymmetry | Crash |
| 5 | NOT STARTED | Unchecked async prefab-load callback | Crash |
| 6 | NOT STARTED | A full config file write on every click in the window | Perf |
| 7 | DONE | Per-frame waste in `LandingPredictionManager` | Perf |
| 8 | NOT STARTED | Replace the ~300-line `SetRotation` switch with a registry | Structure |
| 9 | NOT STARTED | Extract the (now four times) duplicated offset row | Structure |
| 10 | NOT STARTED | Move the landing predictor's math into `PureMath/` | Structure |

---

## Crash scenarios

### 1. Resolve the vessel once per tick instead of 63 times
`Assets/SASExtended/Code/Managers/SASManager.cs:147-150`

```csharp
private VesselComponent _vessel => GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
private TelemetryComponent _telemetry => _vessel.SimulationObject.Telemetry;   // NOT null-safe
```

`_vessel` is null-safe; `_telemetry` is not, and it re-resolves `_vessel` on every access. There
are 63 such accesses per tick, and each one is an independent resolution — so a vessel that goes
away mid-tick (scene change, vessel switch, recovery) produces a **TOCTOU** failure: an early
guard passes, a later access NREs. `HasManeuverNode` (line 66) shows the shape in one line:

```csharp
public bool HasManeuverNode => _vessel != null && _telemetry.HasManeuver;   // resolves _vessel TWICE
```

The null check and the use it guards are looking at two different resolutions.

**Recommendation:** resolve the vessel and telemetry **once at the top of the tick**, null-check
once, and thread them through as parameters. This is the same fix that makes #8 natural.

> **Verified, and scoped down:** I initially expected 63 lookups/tick to be a real performance
> cost. Decompiling `GetActiveSimVessel` → `GetSimVessel` → `IsSimVesselValid` shows it is just a
> null-check chain — cheap. **This is a correctness fix first**; the perf gain is a rounding error.
> Don't sell it as an optimization.

### 2. `GetParentStar` returns null, and the caller doesn't guard it
`Assets/SASExtended/Code/Managers/SASManager.cs:1090-1106` — *unchanged since the 07-14 review*

```csharp
var body = vessel.mainBody;
try { while (!body.IsStar) { body = body.referenceBody; } }
catch (Exception ex) { _LOGGER.LogError($"Unable to fetch parent star ... How is this possible?!"); }
return body;   // returns NULL after the catch
```

**Confirmed by decompiling `KSP.Sim.impl.CelestialBodyComponent`:** `referenceBody` returns `null`
when `Orbiter == null` — i.e. at the root of the body tree. So the loop dereferences null, the
`catch` swallows the NRE, and the method hands back `null`. The `SpecialStarPlus`/`SpecialStarMinus`
cases in `SetRotation` then do `sunBody.Position` — an **uncaught NRE every tick**, forever, since
nothing resets the state that caused it.

The log message ("How is this possible?!") is the tell: the author didn't have a theory for this
branch, so the handler doesn't actually handle anything.

**Recommendation:** `bool TryGetParentStar(VesselComponent, out CelestialBodyComponent)` + a
`DisengageForLostReference()` path, and **cache the star at engage time** rather than walking the
body tree every tick — the parent star cannot change while a mode is engaged.

> **Implemented:** `GetParentStar` replaced with `static bool TryGetParentStar(VesselComponent, out
> CelestialBodyComponent)` (no try/catch — the `while` guards `body != null` directly, since the
> confirmed failure is a null `referenceBody`, not an exception). `SetMode` resolves it once at
> engage time via a new `IsStarMode(mode)` check, refusing to engage (logged warning, no state
> change) if the star can't be found; the result is cached in `_engagedParentStar` and reused by
> both `SpecialStarPlus`/`SpecialStarMinus` cases in `SetRotation` instead of a per-tick tree walk.
> `Update()` gained a defensive `IsStarMode(AttitudeMode) && _engagedParentStar == null` check
> mirroring the existing Maneuver/Target lost-reference guards, and `ResetPerVesselState` clears the
> cached star on disengage.

### 3. ~63 unguarded `_root.Q<…>()` calls in `OnEnable`
`Assets/SASExtended/Code/UI/MainWindowController.cs:209+`

```csharp
_offToggle = _root.Q<SideToggleControl>("off");
_offToggle.SetEnabled(true);          // NRE if "off" isn't in the UXML
```

Every lookup is immediately dereferenced. One renamed or missing element throws **mid-`OnEnable`**,
which silently skips *all remaining registration* — the window comes up looking fine, with
everything below the throw dead. This is a failure mode the file itself already documents, and the
newer wiring (`WireSettingsButton`, `WireFlightAxesToggles`, `WireLandingPredictionToggle`) *is*
null-guarded — so the codebase already agrees with this recommendation, just inconsistently.

**Recommendation:** a `Require<T>(name)` helper that logs a specific error and returns null, plus
splitting `OnEnable` so one failed section can't take out the rest.

> **Note (new since the review was drafted):** the Roll-lock commit `efffa50` added six more
> unguarded lookups (`hover-z-toggle`, `hover-z-value`, `hover-z-minus/plus/first/second` at
> `MainWindowController.cs:438-454`), each immediately `.RegisterCallback`'d. The count went from
> ~50 to 63. New code is still being written in the unguarded style, which makes the helper more
> valuable, not less.

> **Implemented:** added `Require<T>(name)` (and a `Require<T>(container, name)` overload for
> `_hoverControlsContainer` lookups), which logs a specific "element not found" error instead of a
> bare `Q<T>()` returning null for the next line to NRE on. `OnEnable` itself now only sets up
> `_window`/`_root` and then calls a sequence of `WireSection(name, action)` invocations, one per
> logical group (`WireGlobalModeToggles`, `WireTabs`, `WireOrbitToggles`, `WireOffsetRows`,
> `WireHoverControlsPanel`, etc. — 16 sections total, all former OnEnable content moved into them
> verbatim). `WireSection` wraps each in try/catch and logs which named section failed, so a missing
> element still throws (same NRE as before) but only aborts its own section instead of unwinding out
> of `OnEnable` and skipping every registration written after it. `_allModeToggles` now filters out
> nulls (`.Where(t => t != null)`) so a toggle that failed to resolve doesn't NRE
> `ClearAllModeToggles` on every subsequent mode switch, and `RegisterModeButton` no-ops on a null
> toggle for the same reason. The three call sites that already null-guarded themselves
> (`WireFlightAxesToggles`, `WireLandingPredictionToggle`, `WireSettingsButton`) were left as-is and
> are now just invoked through the same `WireSection` wrapper for consistency.

### 4. Event + singleton lifecycle asymmetry
`MainWindowController.cs:504-507`, `SASManager.cs:17`

```csharp
if (!_subscribedToSasManager && SASManager.Instance != null)
{ SASManager.Instance.Disengaged += OnSasManagerDisengaged; _subscribedToSasManager = true; }
```

Subscribed from `Update()`, **never unsubscribed** — the event holds a strong reference to a
destroyed MonoBehaviour, and the handler runs against a dead object. And:

```csharp
public static SASManager Instance { get; set; }   // public setter, and no OnDestroy at all
```

`FlightAxesVisualizer` and `LandingPredictionManager` both correctly do `if (Instance == this)
Instance = null;` in `OnDestroy`. `SASManager` — the one whose `Instance` is read from a **Harmony
patch on the game's own input handler** (`FlightInputHandlerThrottlePatch`), i.e. the one place a
stale instance is least recoverable — does not.

**Recommendation:** unsubscribe in `OnDisable`; give `SASManager` the same `OnDestroy` its two
siblings already have; make the setter private.

> **Implemented:** `SASManager.Instance` setter is now `private set`; added an `OnDestroy` matching
> `FlightAxesVisualizer`/`LandingPredictionManager`'s (`if (Instance == this) Instance = null;`).
> `MainWindowController` replaced the `_subscribedToSasManager` bool with a `_subscribedSasManager`
> field that tracks the actual subscribed instance (not just a flag), so the new `OnDisable`
> unsubscribes from the exact instance it subscribed to in `Update()`, and clears the field so a later
> re-enable resubscribes instead of staying permanently unsubscribed.

### 5. Unchecked async prefab-load callback
`Assets/SASExtended/Code/Managers/FlightAxesVisualizer.cs:239-243`

```csharp
GameManager.Instance.Assets.Load<GameObject>(AxesPrefabKey, obj =>
{ _axesPrefab = obj.GetComponent<DebugShapesAxesComponent>(); ... }, logMissingKey: true);
```

`logMissingKey: true` says the author knew the key can be missing — but `obj` is dereferenced
unchecked, so the "handled" case throws inside an async callback, where the stack trace is least
useful. Guard `obj`, and disable the feature rather than throwing.

---

## Performance

### 6. A full config file write on every click anywhere in the window
`MainWindowController.cs:206-207, 487-492`

```csharp
// Persist the position once a drag finishes (dragging is the only way it ever changes).
_root.RegisterCallback<PointerUpEvent>(evt => SaveWindowPosition());
```

```csharp
private void SaveWindowPosition()
{
    Settings.WindowPositionX.Value = _root.resolvedStyle.left;
    Settings.WindowPositionY.Value = _root.resolvedStyle.top;
    SASExtendedPlugin.Instance.SWConfiguration.Save();
}
```

**The comment states a false premise.** The callback is registered on `_root` in the **bubbling**
phase, so it fires on *every* pointer-up anywhere inside the window — every mode toggle, every
nudge button, every field commit — not only at the end of a drag. Each one calls
`SWConfiguration.Save()`, which **decompiling `ReduxLib.Configuration.JsonConfigFile.Save()`
confirms is a synchronous `File.WriteAllText` of the entire config file**. A synchronous whole-file
disk write on the main thread, during flight, on every click. Rapid nudge-button clicking is the
worst case.

**Recommendation:** compare against the last-saved position and return early if unchanged; that
alone reduces this to the intent the comment already describes. Registering on the drag manipulator
rather than on `_root` is the cleaner fix.

*(This is the cheapest fix in the document and the one most likely to be felt in-game.)*

### 7. Per-frame waste in `LandingPredictionManager` — while disabled by default
`Assets/SASExtended/Code/Managers/LandingPredictionManager.cs:146-149, 509-527`

```csharp
if (!_enabled || _builtVessel == null || !InFlightView)
{
    LogNoPrediction($"gated off (enabled={_enabled}, vessel={_builtVessel != null}, inFlightView={InFlightView}).");
    DestroyVisuals();
}
```

The interpolated string is **built every frame** — the throttle gate is *inside* `LogNoPrediction`,
so it suppresses the write but not the allocation. This runs on the feature's **disabled-by-default
path**, meaning every player who never turns landing prediction on pays for it. `InFlightView` is
also evaluated twice, and `DestroyVisuals()` is called every frame while off.

And on the render path:

```csharp
var widthKeys = new Keyframe[_trajectorySampleCount];   // 201
...
_line.widthCurve = new AnimationCurve(widthKeys);       // every render frame
```

Two heap allocations per frame feeding steady GC pressure, for a curve that only changes when the
sample count or camera distance changes.

**Recommendation:** hoist the gate outside the string (`if (ShouldLog) LogNoPrediction($"…")`, or
pass the values and format lazily); track a `_visualsDestroyed` flag; cache the `AnimationCurve`
and rebuild only on change.

> **Implemented:** added `ShouldLogNoPrediction` (the same condition `LogNoPrediction` already
> applied internally, now exposed) and guarded the `Update()` gated-off branch's interpolated
> string with it, so it's built only when something will actually be logged - the disabled-by-
> default path now allocates nothing per frame. `InFlightView` is read into a local once and reused
> in both the condition and the log string instead of being evaluated twice. Added a
> `_visualsDestroyed` bool so `DestroyVisuals()` no-ops once already torn down instead of re-running
> its teardown every frame while gated off or while a recompute has no valid impact; `CreateVisuals`
> clears it. For the render path, added a persistent `_widthCurve`/`_widthCurveKeyCount` pair:
> `UpdateVisualPositions` now only reallocates the `Keyframe[]`/`AnimationCurve` when
> `_trajectorySampleCount` actually changes (i.e. right after a recompute, not every frame), and
> otherwise updates the existing curve's keys in place via `AnimationCurve.MoveKey` - widths
> genuinely do need recomputing every frame (camera-to-vertex distance changes continuously), but
> the curve object and its backing array no longer do. `DestroyVisuals` clears the cached curve too,
> so a torn-down-and-recreated line always gets it reassigned rather than risking a stale reference
> if the key count happens to coincide.

---

## Structure & separation of concerns

### 8. Replace the ~300-line `SetRotation` switch with a direction registry
`Assets/SASExtended/Code/Managers/SASManager.cs:361-630`

Twenty-four cases, of which **twenty** share one shape:

```
reframe → BuildPointingRotation(dir, upwards, out effX, out effY, out effZ) → loggedTarget = dir
```

Six pairs differ *only* by a `Vector.negate`, and the Hvel± projection is duplicated verbatim
between its two cases. The four genuine special cases (`KillRot`, `Hold`, `Hover`, `Maneuver`) are
buried at the bottom (lines 611-630) where the shape actually differs.

**Recommendation:** an `AttitudeMode → Func<TelemetryComponent, ICoordinateSystem, Vector>`
registry; the twenty regular modes become one-line table entries sharing a single call path, and
the four special cases stay explicit and become *visibly* special. Cuts roughly 200 lines.

**This is the codebase's own idiom** — `FlightAxesVisualizer._arrowSpecs` and `ToggleIds` are
exactly this pattern. The recommendation is to apply an established local convention to the one
file that predates it, not to import a new one.

### 9. Extract the (now four times) duplicated offset row
`Assets/SASExtended/Code/UI/MainWindowController.cs:399-460` and the H/P/R rows above

The Heading / Pitch / Roll rows are near-verbatim triplicates — toggle, `FloatField`, ± nudge
buttons, two preset buttons, ~200 lines — differing only in element-name prefix and which
`SASManager` field they write. The four tab handlers are likewise identical modulo one argument.

> **Note (new since the review was drafted):** commit `efffa50` added Hover's own Roll row as a
> **fourth** copy of the same block (`_hoverZToggle`/`_hoverZValue`/`_hoverZMinus`/`_hoverZPlus`/
> `_hoverZFirst`/`_hoverZSecond`, wired at `MainWindowController.cs:438-460`). That commit's own
> message documents a stale-value bug caused by *"Roll now having two separate widgets (shared +
> Hover's own) that could each go stale relative to the other"* — i.e. the duplication has already
> produced a real bug, not just line count. This raises the priority of #9 from cleanup to
> bug-prevention.

**Recommendation:** a `BindOffsetRow(container, prefix, getter, setter, enabledFlag)` helper.
Hover's row differs only in which enabled-flag it drives (`HoverRollEnabled` vs the shared
`ZEnabled`) — which is exactly a parameter.

### 10. Move the landing predictor's math into `PureMath/`
`LandingPredictionManager.cs` — `IntegrateStep`, `Acceleration`, `Derotate`, `MarchToImpact`

These are already `static` and nearly pure; the only game coupling is `body.GetAltitudeFromTerrain`.

**Recommendation:** move them to `Assets/SASExtended/Code/PureMath/` with the terrain sampler
injected as `Func<Vector3d, double>`, so they can be tested alongside the existing
`AttitudeMathTests` / `HoverThrottleMathTests`. **The infrastructure already exists** — `PureMath/`
(`AttitudeMath.cs`, `HoverThrottleMath.cs`) and `Tests/EditMode/` are both in the repo; this is
extending a pattern, not introducing one.

This is the **highest-value test target in the mod**: `.claude/landing_prediction_fixes.md`
documents a **ten-round** in-game debugging cycle for this math. Every one of those rounds required
launching the game. The RK4 integration, coarse/fine bracketing, and bisection are all verifiable at
the desk.

---

## Highest leverage, if only three

1. **#6** — a synchronous whole-file disk write on every click, with the code's own comment
   asserting it can't happen. Trivial fix, real in-game stutter.
2. **#2** — a confirmed silent null becoming an uncaught per-tick NRE. Still open since 07-14.
3. **#8** — the 300-line switch, refactored using the codebase's own `_arrowSpecs` precedent.

## Deliberately not raised

- `_line.SetPositions(_linePositionsBuffer)` passes a 201-length buffer while `positionCount` is
  often smaller. I could not confirm Unity's exact behaviour on a length mismatch, and chose not to
  raise a finding I couldn't verify.
- The `startAlt <= 0.0 → sampleCount = 1` path and the `foundFine == false` coarse fallback both
  **stay within array bounds** (`sampleCount` maxes at 201 = `TrajectorySampleCount`). Traced,
  confirmed safe, dismissed.
