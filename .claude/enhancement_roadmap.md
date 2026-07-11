# Enhancement roadmap — SAS Extended

Top-10 recommendations (2026-07-10) for features to introduce and refactors to optimize/stabilize existing features. Derived from a review of `mod_specifics.md`, the hover debugging history. Already solved recommendations are deleted from the file.
(`hover_mode_fixes.md`), and the full mod source under `Assets/SASExtended/Code/`. Ordered roughly
by value-for-effort within each section.


## Features

### 1. Two-way sync with stock SAS

If the player clicks a stock SAS mode button (or toggles SAS off with `T`), the mod keeps
re-issuing `LockRotation` on its interval and the two fight each other; conversely the window
doesn't reflect externally-changed state. Subscribe to `SASEnabledMessage` /
`SASDisabledMessage` / `SASModeChangedMessage` (already catalogued in `mod_specifics.md`) to
disengage or update the UI. Removes a whole class of confusing in-flight behavior.

### 2. MechJeb-parity "ADV" tab

Smart A.S.S. is the stated north star; its OBT/SURF/TGT rows are done, leaving ADV —
arbitrary reference-frame + direction combinations (see `MechJebModuleSmartASS.cs` in the
local MechJeb2 mirror). Natural next feature milestone after in-game verification of
SURF/TGT/SPEC.


## Refactors — optimize and stabilize


### 3. Extract the pure math into a testable layer + delete dead code

The offset math was validated via a standalone quaternion simulation and the hover law took
11 in-game rounds — both because nothing is testable outside the Unity editor. Pull
`BuildPointingRotation` / `GetCurrentOffsetAngles` / the hover P+I+D step into pure functions
with an edit-mode test asmdef so the next control-law change is verifiable before a build.
While in there, remove dead code: the unused `GetEulerAngleDifference` + `_dif`, the TEMP
`Horizon` mode / `SetHorizon`, leftover commented-out scraps, and the never-registered OAB
button poke in `MainWindowController.IsWindowOpen`.
