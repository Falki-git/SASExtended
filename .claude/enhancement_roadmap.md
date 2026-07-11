# Enhancement roadmap — SAS Extended

Top-10 recommendations (2026-07-10) for features to introduce and refactors to optimize/stabilize existing features. Derived from a review of `mod_specifics.md`, the hover debugging history. Already solved recommendations are deleted from the file.
(`hover_mode_fixes.md`), and the full mod source under `Assets/SASExtended/Code/`. Ordered roughly
by value-for-effort within each section.


## Features

### 1. MechJeb-parity "ADV" tab

Smart A.S.S. is the stated north star; its OBT/SURF/TGT rows are done, leaving ADV —
arbitrary reference-frame + direction combinations (see `MechJebModuleSmartASS.cs` in the
local MechJeb2 mirror). Natural next feature milestone after in-game verification of
SURF/TGT/SPEC.

**Analysis (2026-07-11).** MechJeb's ADV is two independent dropdowns — a 14-value
`AttitudeReference` (INERTIAL, ORBIT, ORBIT_HORIZONTAL, SURFACE_NORTH, SURFACE_VELOCITY,
TARGET, RELATIVE_VELOCITY, TARGET_ORIENTATION, MANEUVER_NODE(_COT), SUN,
SURFACE_HORIZONTAL, plus two "COT" center-of-thrust variants) times a 6-value `Direction`
(the frame's own ±X/±Y/±Z) — composed as `referenceRotation * LookRotation(axis, upHint)`.
Full parity means building and shipping all 84 combinations behind a new dropdown UITK
control that doesn't exist in this codebase yet (today's UI is only toggle-button grids).
That's a lot of surface area, most of which duplicates a mode this mod already has under a
different name (TARGET_ORIENTATION ≈ PAR, SUN ≈ Star, SURFACE_VELOCITY/ORBIT as *frames*
still resolve to the same vectors already used to build the 24 existing hardcoded modes).
Recommendation: don't chase the full matrix — feature parity isn't the goal, and most of
the 84 combinations are dead weight nobody will click. Do this instead:

- **Add exactly one genuinely new capability: an inertial hold.** None of the 24 existing
  modes hold a fixed direction in world/inertial space — they're all defined relative to a
  frame that itself rotates with the orbit (prograde, surface north, target, etc.), and
  stock `KillRot` only zeroes angular velocity rather than holding a heading over time. A
  single "HOLD" mode/button that snapshots the vessel's current facing as an inertial
  quaternion and locks to it is the one piece of ADV's `INERTIAL` reference that isn't
  already reachable another way, and it's useful in its own right (solar panels/antenna
  through an eclipse, holding attitude across a time warp). This can be one button, not a
  dropdown — skip the reference/direction picker UI entirely for v1.
- **Skip the generic reference×direction dropdown UI for now.** It requires a new UITK
  control with no precedent here (`DropdownField` or a hand-rolled combo box), and a design
  decision this mod hasn't made yet: live-apply on selection change (consistent with the
  rest of the window's no-EXECUTE-button philosophy) vs. MechJeb's explicit EXECUTE step
  (arguably safer for a picker where a wrong intermediate selection could snap the vessel).
  Worth a proper design pass on its own if/when a real use case shows up for a frame the
  existing 24 modes don't cover — don't build it speculatively.
- Net effect: closes the one real gap (no inertial-hold option) with a small, low-risk
  addition, and treats true free-form reference/direction selection as optional future
  scope rather than a roadmap commitment.

**Update (2026-07-11).** The testable pure-math layer (`AttitudeMath`/`HoverThrottleMath` +
edit-mode tests) is now in place, so any new ADV/HOLD logic should be written directly into
that tested layer (`Assets/SASExtended/Code/PureMath/`) instead of bolted onto the
`SASManager.SetRotation` switch.
