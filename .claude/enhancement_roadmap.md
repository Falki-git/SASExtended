# Enhancement roadmap — SAS Extended

Top-10 recommendations (2026-07-10) for features to introduce and refactors to optimize/stabilize
existing features. Derived from a review of `mod_specifics.md`, the hover debugging history
(`hover_mode_fixes.md`), and the full mod source under `Assets/SASExtended/Code/`. Ordered roughly
by value-for-effort within each section.

**Suggested execution order:** 7 (stability) → 1 (the pending feature) → 3 (cheap UX) → 2, rest
opportunistic. Items 1, 3, and 5 touch UXML, so they could share one UI pass to respect the
verbatim-port constraint (new controls added, nothing existing restyled — see
[`mod_specifics.md`](mod_specifics.md) "Porting from the legacy prototype").

## Features

### (DONE) 1. Finish the Hover UI — vertical-speed setpoint + horizontal-cancel toggle

`HoverTargetVerticalSpeed` is already the live setpoint the throttle law holds
(`SASManager.cs`), but nothing writes to it except the reset-to-0 on engage, and the
horizontal-cancel toggle is the one explicitly still-open item from the 11-round hover
debugging cycle. This turns Hover from "hold still" into an actual controlled-descent/landing
aid — the highest-value feature, and the control law is already built for it.

### 2. Two-way sync with stock SAS

If the player clicks a stock SAS mode button (or toggles SAS off with `T`), the mod keeps
re-issuing `LockRotation` on its interval and the two fight each other; conversely the window
doesn't reflect externally-changed state. Subscribe to `SASEnabledMessage` /
`SASDisabledMessage` / `SASModeChangedMessage` (already catalogued in `mod_specifics.md`) to
disengage or update the UI. Removes a whole class of confusing in-flight behavior.

### 3. Honest UI feedback for fallback states

`Maneuver` with no node and `TargetPar`/`Target` modes with no target silently hold current
attitude (fallback branches in `SASManager.SetRotation` / `BuildTargetOrientationRotation`)
while the button stays lit as if tracking — the player can't tell "pointing at node" from
"there was no node." Grey out / badge the buttons using `HasManeuver` / `HasTargetObject`, or
show a status line. Cheap, big usability win.

### 4. Status readout in the window

Redux's reworked `VesselSAS` exposes public live telemetry (`angDelta`, `torque`, `sasResp`,
etc. — see the Redux-vs-vanilla diff in `mod_specifics.md`), and the mod already computes
angle-to-target and measured H/P/R every tick. A small readout row (angle-to-target, actual
vs. commanded H/P/R, hover vertical speed) would surface what currently only exists in
`Player.log` — and would have shortened several of the hover debugging rounds.

### 5. MechJeb-parity "ADV" tab

Smart A.S.S. is the stated north star; its OBT/SURF/TGT rows are done, leaving ADV —
arbitrary reference-frame + direction combinations (see `MechJebModuleSmartASS.cs` in the
local MechJeb2 mirror). Natural next feature milestone after in-game verification of
SURF/TGT/SPEC.

### (DONE) 6. Persist settings via `SWConfiguration`	

All hover gains, refresh intervals, and offsets are hardcoded public fields; window position
and last H/P/R values reset every session. `KerbalMod` already provides `SWConfiguration`
(IConfigFile) — expose the tunables there so they can be adjusted without a rebuild, and
remember offsets/window position across sessions.

## Refactors — optimize and stabilize

### 7. Vessel-lifecycle hardening in `SASManager`

`Update` guards `_vessel == null`, but `SetMode` dereferences `_vessel.Autopilot` unguarded
(NRE if clicked with no active vessel); `_killRotTarget` and Hover's `_throttleIntegral` are
captured for one vessel and silently apply to whatever vessel becomes active after a
switch/undock/revert; nothing resets `AttitudeMode` on scene exit. Subscribe to
vessel-change/game-state messages and disengage cleanly. **Probably the biggest latent-bug
reservoir in the codebase.**

### 8. Compute telemetry vectors on demand, not all-up-front

`SetRotation` reframes ~15 direction vectors plus walks the body tree for the parent star
(`GetParentStar`) every tick, at up to 50 Hz, even when the mode needs exactly one of them.
Move the vector selection into the mode branches — or a mode → vector-selector table, which
would also collapse the 20-case switch and the 20 one-line `SetXxx` wrappers. Cuts per-tick
work by ~90% and shrinks the file substantially.

### 9. Gate debug-log string building

Both `[SetRotation]` and `[Hover/attitude]` build large interpolated strings every tick
regardless of whether debug logging is enabled — allocation and formatting cost in the hot
loop. Wrap them in a level check or a config-backed "diagnostics" flag (pairs naturally with
item 6).

### 10. Extract the pure math into a testable layer + delete dead code

The offset math was validated via a standalone quaternion simulation and the hover law took
11 in-game rounds — both because nothing is testable outside the Unity editor. Pull
`BuildPointingRotation` / `GetCurrentOffsetAngles` / the hover P+I+D step into pure functions
with an edit-mode test asmdef so the next control-law change is verifiable before a build.
While in there, remove dead code: the unused `GetEulerAngleDifference` + `_dif`, the TEMP
`Horizon` mode / `SetHorizon`, leftover commented-out scraps, and the never-registered OAB
button poke in `MainWindowController.IsWindowOpen`.
