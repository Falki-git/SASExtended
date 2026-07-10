# UI Authoring Mode — dual-mode custom UITK controls

Solves the custom-control dilemma (see `custom_uxml_controls.md`, on branch `hover-fix` at time of
writing): Unity 6's `[UxmlElement]`/`[UxmlAttribute]` system gives UI Builder attribute editing but
produces bundles the game cannot load (native `[SerializeReference] UxmlSerializedData` is
unresolvable for a late-loaded mod assembly), while the legacy `UxmlFactory`/`UxmlTraits` system
loads fine in-game but gets no attribute editing in Unity 6's UI Builder.

**Key insight:** the `.uxml` text file is identical under both systems — same tags, same attribute
names. Which form ends up inside the imported `VisualTreeAsset` is decided at **UXML import time**
by which face the control class exposes to the compiler at that moment. So the controls compile in
one of two faces, switched by the `SASX_UI_AUTHORING` scripting define, and the UXML is reimported
whenever the mode changes.

## How to use

- **To build/edit UI in UI Builder:** Unity menu → **Modding → SAS Extended UI Authoring Mode**
  (checkmark = on). Scripts recompile, the mod's `.uxml` reimport automatically, and UI Builder now
  shows the custom attributes (`Text`, `IsBig`, `IsSmall`, `IsEnabled`, `IsToggled`) in its
  inspector.
- **To build the mod:** toggle the same menu item **off** first. The `.uxml` reimport back to the
  legacy form happens automatically after the recompile. Then run the ThunderKit pipeline as usual.
- **Forgot to toggle off?** Every pipeline (Build for Editor / Build for Player / Deploy to Zip
  File) starts with the `EnsureLegacyUxmlImport` guard job, which **fails the build** with a clear
  message while authoring mode is on. A warning is also logged after every domain reload while the
  mode is on.

## Moving parts

| Piece | Where | Role |
|---|---|---|
| Dual-mode controls | `Assets/SASExtended/Code/UI/Controls/{SideToggleControl,TabToggleControl}.cs` | `#if SASX_UI_AUTHORING` → `[UxmlElement]` + `[UxmlAttribute]` wrapper properties; `#else` → the shipped legacy `UxmlFactory`/`UxmlTraits`. |
| Plugin guard | `SASExtendedPlugin.RegisterUxmlFactories` | Legacy branch registers factories as before; authoring branch compiles to a `LogError` so a mistakenly-shipped authoring build says why the UI is missing. |
| Mode toggle + import sync | `Assets/Utilities/Editor/UiAuthoringMode.cs` | Menu item toggles the define; a `[DidReloadScripts]` hook compares compile state against a marker in `Library/SASExtendedUxmlImportMode.txt` and force-reimports the mod's `.uxml` when they differ, so the imported assets always match the compiled face (survives editor restarts and Library wipes). |
| Pipeline guard | `Assets/Utilities/Editor/EnsureLegacyUxmlImport.cs` + first `Data` entry in the three pipeline assets under `Assets/SASExtended/Pipelines/` | Throws while authoring mode is on; otherwise force-reimports the `.uxml` one more time so the bundle is deterministic. |

## Invariants (do not break)

1. **Attribute names must match exactly** between the two faces (`Text`, `IsBig`, `IsSmall`,
   `IsEnabled`, `IsToggled`, plus the standard `name`). Adding an attribute means adding it to
   *both* the `UxmlTraits` and the `[UxmlAttribute]` wrapper block.
2. **Declaration order in the authoring face:** `IsEnabled` before `IsToggled` — the
   `UxmlSerializedData` deserializer applies attributes in declaration order and `SetEnabled`
   resets the toggle state (this exact bug was hit during the original port).
3. **Never ship a build made in authoring mode.** The guards exist for this; don't remove them or
   reorder the guard job out of first position.
4. UI Builder re-saves normalize/reorder UXML attributes. The main `SASExtended.uxml` is a
   verbatim 1:1 port — review the git diff after any UI Builder session on it.

## First-run checklist (Unity editor)

1. Open the project; Unity imports the two new editor scripts (pre-authored GUIDs in their `.meta`
   files are referenced by the pipeline assets, so the guard job should appear as the first entry
   when inspecting each pipeline).
2. Toggle **Modding → SAS Extended UI Authoring Mode** on; confirm UI Builder shows the custom
   attributes on e.g. a `SideToggleControl` and that the console logs the `.uxml` reimport.
3. Toggle it off; confirm the reimport log appears again, then run **Build for Editor** and verify
   the window still loads in play mode (legacy path unchanged).
4. Sanity-negative: with authoring mode on, run any pipeline and confirm it fails with the
   `EnsureLegacyUxmlImport` message.
