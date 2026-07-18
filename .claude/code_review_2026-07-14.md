# Code review — robustness pass (2026-07-14)

Scope: `Assets/SASExtended/Code/**` as of the working tree (includes the in-progress Landing
Prediction feature). Focus: correctness edge cases, unguarded exceptions, and reuse gaps that
make the mod more error-prone at runtime. General code quality is high — this mod already
anticipates far more edge cases than most (vessel switches, lost targets/nodes, external SAS
changes, UT rewinds on quickload). The items below are the gaps still open.

## High priority

### 1. No top-level exception guard on any `Update()` loop
`SASManager`, `FlightAxesVisualizer`, and `LandingPredictionManager` all drive their whole state
machine from a bare `MonoBehaviour.Update()` with no try/catch. If *any* unanticipated null/edge
case throws (see #2 and #5 below for two concrete ones), Unity will log the exception and retry
next frame — forever, since nothing resets the state that caused it. That's a permanent per-frame
`Player.log` flood for the rest of the session, not a one-time failure.

**Recommendation:** wrap each `Update()` body in a try/catch that logs once (or throttled) and
falls back to a safe state (e.g. `SetSASOff()`/disable the feature) rather than leaving the
exception to repeat every frame. This is the single highest-leverage change here — it converts any
future edge case nobody thought of from "log spam / possible soft-lock" into "feature gracefully
turns itself off."

### 2. `SASManager.GetParentStar` can return `null`, and the caller doesn't guard it
`Assets/SASExtended/Code/Managers/SASManager.cs:1073-1090`

```csharp
private CelestialBodyComponent GetParentStar(VesselComponent vessel)
{
    var body = vessel.mainBody;
    try { while (!body.IsStar) body = body.referenceBody; }
    catch (Exception ex) { _LOGGER.LogError(...); }
    return body;
}
```

If `vessel.mainBody` is null, or the `referenceBody` chain doesn't terminate in a star, the
`while` throws, gets caught, and the method returns `body` — possibly `null`. The caller
(`AttitudeMode.SpecialStarPlus`/`Minus` in `SetRotation`, lines ~476-489) does
`sunBody.Position` unguarded — an uncaught NRE inside `SetRotation()`, which (per #1) means
Star+/Star− would spam-fail every frame if this is ever hit rather than just failing that one
mode-engage.

**Recommendation:** have `GetParentStar` return `null` explicitly on failure and have both
Star+/Star− cases check for it and fall back to holding current attitude (the same pattern already
used for `Maneuver`/`TargetPar` when their reference is missing), instead of trusting a non-null
result.

### 3. `FlightAxesVisualizer.LoadPrefabs` doesn't null-check the loaded asset
`Assets/SASExtended/Code/Managers/FlightAxesVisualizer.cs:239-249`

```csharp
GameManager.Instance.Assets.Load<GameObject>(AxesPrefabKey, obj =>
{
    _axesPrefab = obj.GetComponent<DebugShapesAxesComponent>();
    RebuildForCurrentVessel();
}, logMissingKey: true);
```

`mod_specifics.md` itself flags this as an open risk ("confirm the two stock debug prefabs
actually load from a mod context... watch for `logMissingKey` warnings"). If the load fails,
`logMissingKey: true` logs it, but there's no guarantee the callback isn't still invoked with
`obj == null` — in which case `obj.GetComponent(...)` NREs inside the async callback, and (per #1)
that exception has no backstop.

**Recommendation:** add `if (obj == null) { _LOGGER.LogError(...); return; }` at the top of both
callbacks (Axes and Arrow) before calling `.GetComponent`.

### 4. `SASManager.ApplyOffsets` duplicates `AttitudeMath.ResolveAppliedOffsets` instead of calling it
`Assets/SASExtended/Code/Managers/SASManager.cs:735-751` vs.
`Assets/SASExtended/Code/PureMath/AttitudeMath.cs:30-47`

`AttitudeMath.ResolveAppliedOffsets` exists specifically so this enable/disable-per-axis logic can
be unit tested (`AttitudeMathTests.ResolveAppliedOffsets_*`), but `SASManager.ApplyOffsets`
re-implements the same branching inline instead of calling it. Net effect: the tests exercise a
code path that isn't actually the one running in-game, so a future edit to one copy (e.g. fixing a
bug in `ApplyOffsets`) wouldn't be caught by the existing tests, and vice versa.

**Recommendation:**
```csharp
private Rotation ApplyOffsets(Rotation look, out double appliedX, out double appliedY, out double appliedZ)
{
    var current = Rotation.Reframed(_vessel.ControlTransform.Rotation, look.coordinateSystem);
    AttitudeMath.ResolveAppliedOffsets(
        XEnabled, X, YEnabled, Y, ZEnabled, Z,
        look.localRotation, current.localRotation,
        out appliedX, out appliedY, out appliedZ);
    var rotation = look;
    rotation.localRotation = AttitudeMath.ComposePointingRotation(look.localRotation, appliedX, appliedY, appliedZ);
    return rotation;
}
```
(This also lets `GetCurrentOffsetAngles` stay private/internal-only if nothing else needs it.)

### 5. Landing prediction: eager string-building on the hot/common path
`Assets/SASExtended/Code/Managers/LandingPredictionManager.cs:111-116`

```csharp
if (!_enabled || _builtVessel == null || !InFlightView)
{
    LogNoPrediction($"gated off (enabled={_enabled}, vessel={_builtVessel != null}, inFlightView={InFlightView}).");
    DestroyVisuals();
    return;
}
```

`LogNoPrediction` checks `Settings.VerboseLoggingEnabled.Value` *inside* itself, but C# evaluates
the interpolated string argument before the call — so this string is allocated **every single
frame**, for the entire session, whenever the toggle is off (its default state). This is exactly
the anti-pattern the rest of the codebase deliberately avoids elsewhere — see `SASManager`'s own
comment on why `Settings.VerboseLoggingEnabled.Value` is checked *before* building the
`[SetRotation]`/`[Hover/attitude]` log strings, and `RecomputeTrajectory`'s debug log lower in this
same file, which does it correctly.

**Recommendation:** guard before interpolating:
```csharp
if (!_enabled || _builtVessel == null || !InFlightView)
{
    if (Settings.VerboseLoggingEnabled.Value)
        LogNoPrediction($"gated off (enabled={_enabled}, vessel={_builtVessel != null}, inFlightView={InFlightView}).");
    DestroyVisuals();
    return;
}
```
While there, consider skipping the `DestroyVisuals()` call too when nothing was ever created
(it's a no-op today thanks to internal null checks, but it's four null checks run every frame for
most of the mod's lifetime for no reason).

### 6. Landing prediction: fixed march-step count can miss a narrow terrain crossing
`Assets/SASExtended/Code/Managers/LandingPredictionManager.cs:171-224`

The forward march always uses `MarchSteps = 200` steps spread across `horizon` (up to a full
orbital period for closed orbits, or 6h for hyperbolic/parabolic ones). For a highly eccentric
orbit where the vessel only dips below terrain briefly near periapsis, 200 evenly-spaced samples
across a long period can step clean over that narrow window — both samples land above terrain, no
crossing is detected, and the algorithm silently reports "no prediction" even though the orbit
does hit the ground. Not a crash, but a silently-wrong answer for exactly the eccentric-orbit case
this feature would be most useful for diagnosing.

**Recommendation (lower urgency — correctness nuance, not a stability risk):** bias the march step
size toward the periapsis passage (e.g. non-uniform sampling denser near periapsis), or at minimum
note the limitation in the plan doc so it's a known gap rather than a surprise bug report later.

### 7. Landing prediction: terrain queried without confirming it accounts for future body rotation
`Assets/SASExtended/Code/Managers/LandingPredictionManager.cs:264-269`

```csharp
var position = orbit.GetTruePositionAtUT(ut);
body.GetAltitudeFromTerrain(position, out var terrainAltitude, out _);
```

`position` is the vessel's predicted position at a *future* UT, but it's unclear (without
decompiling `GetAltitudeFromTerrain`) whether that call resolves terrain height using the body's
rotation state *at that future UT* or its *current* rotation. If it's the latter, predictions on
any rotating body would be systematically wrong at longer horizons (worse near the equator / on
fast rotators), since the ground point actually under the vessel when it arrives has rotated away
from where this call samples it.

**Recommendation:** verify this specifically during the "pending in-game verification" pass already
called out in `mod_specifics.md`/the landing-prediction plan — e.g. compare a predicted impact
lat/lon against the actual impact point after time-warping a vessel down on a fast-rotating body.
If it does turn out to ignore future rotation, this becomes a real must-fix rather than a
nice-to-verify.

## Medium priority — consistency / defensive-style gaps

### 8. Inconsistent `GameManager.Instance` null-guarding
Most accessors in this codebase correctly chain `GameManager.Instance?.Game?...` (e.g.
`SASManager._vessel`, `LandingPredictionManager.ActiveVessel/PhysicsSpace/InFlightView`,
`FlightAxesVisualizer.ActiveVessel/PhysicsSpace`). Two spots don't:

- `SASManager.cs:128` — `private static double _UT => GameManager.Instance.Game?.UniverseModel?.UniverseTime ?? 0;` (guards `.Game` onward but not `Instance` itself)
- `SASManager.SetHold()` (line 947) — `GameManager.Instance.Game.UniverseModel.inertialReferenceFrame.inertialReferenceFrame` has no `?.` anywhere in the chain.

Both are low-probability (a live vessel/autopilot already implies `GameManager` is up), but
inconsistent with the defensive style used everywhere else in the file, and an uncaught NRE here
falls into the same "no backstop" gap as #1.

**Recommendation:** add `?.` consistently, or centralize a single `GameManager.Instance?.Game`
accessor other properties key off of, so there's one place to harden instead of N call sites.

### 9. `MainWindowController.OnEnable()`'s "runs every time the window is re-enabled" comment is stale
`Assets/SASExtended/Code/UI/MainWindowController.cs:172-174`

The doc comment says `OnEnable` "runs when the window is first created, and every time the window
is re-enabled," but `SetOpenWithoutPersisting` only toggles `style.display` (the commented-out
`gameObject.SetActive(value)` alternative is explicitly *not* used) — so in the current
implementation `OnEnable` only ever fires once. That's fine today, but every one of the ~40
`RegisterCallback<...>` registrations in this method has no matching unregister, so if a future
change switches to the `SetActive`-based hiding the comment already gestures at, every callback
would double-register on the second `OnEnable`, silently duplicating every click's side effects
(double mode-engage, double window-position save, etc.).

**Recommendation:** either fix the comment to say "runs once, when the window is first created,"
or (if `SetActive`-based hiding is ever adopted for the performance reason the comment mentions)
add an idempotency guard / unregister-on-disable at that time. No action needed today beyond
correcting the comment so it doesn't mislead the next change in this area.

## Minor / polish

- `MainWindowController.OnXToggleClicked/OnYToggleClicked/OnZToggleClicked` (lines 866-900) are
  each a 6-line if/else that reduces to `SASManager.Instance.XEnabled = _xToggle.IsToggled;`. Purely
  a simplification, no behavior change.
- **Test coverage gap:** `AttitudeMath`/`HoverThrottleMath` were deliberately extracted into
  `PureMath` specifically so the control laws could be edit-mode tested instead of only verified
  live in-game (the 11-round Hover debugging cycle in `hover_mode_fixes.md` is the stated reason
  why). The new Landing Prediction march+bisect algorithm (`RecomputeTrajectory`) is exactly this
  kind of logic — a numeric root-find over a scalar function of time — but it's untested and
  embedded directly in the `MonoBehaviour`, coupled to `KSP.Sim` orbit/body types. Consider
  extracting the march/bisect loop into a `PureMath`-style function
  (`Func<double,double> altitudeAt(double ut)` in, `impactUT` out) so it can be verified against
  synthetic altitude curves (e.g. a simple descending ramp, a curve with a narrow dip to catch
  issue #6 above) the same way `HoverThrottleMathTests` catches throttle-law regressions without
  needing a live build.

## Not flagged as bugs, but worth a deliberate look during the pending in-game verification pass

Both are already called out as open items in `mod_specifics.md` and aren't new findings from this
review — just re-surfacing them here since they're squarely in "robustness" territory:
- Confirm `DebugArrow.prefab`/`DebugAxes.prefab` actually resolve from a mod context (ties into #3
  above — if they don't, the null-guard becomes load-bearing rather than defensive).
- Confirm the Flight Axes attitude arrows and Landing Prediction trajectory both behave sensibly
  across a save/quickload (UT rewind) the same way `SASManager.Update()`'s `elapsed < 0` handling
  was specifically added for — neither `FlightAxesVisualizer` nor `LandingPredictionManager`
  currently has an analogous guard, though neither obviously needs one (they don't accumulate a
  `_lastRefreshTime`-style bookmark across ticks the way `SASManager` does).
