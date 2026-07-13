# Custom UITK controls & UI Authoring Mode

How SAS Extended's custom UI Toolkit controls (`SideToggleControl`, `TabToggleControl`, under
`Assets/SASExtended/Code/UI/Controls/`) are built, why they can't just use Unity 6's normal
`[UxmlElement]` system, and how UI Builder editing is still possible despite that.

## The problem: `[UxmlElement]` can't load from a bundled UXML in a late-loaded mod

For a custom control that a **bundled UXML instantiates by tag** and is loaded by a **late-loaded
SpaceWarp mod assembly**, Unity 6's `[UxmlElement]`/`[UxmlAttribute]` system doesn't work: it
serializes the control into the `VisualTreeAsset` as native `[SerializeReference]
UxmlSerializedData`. The generated `Register()` that resolves this is
`[Conditional("UNITY_EDITOR")]`, and `UxmlSerializedDataRegistry` is editor-only — so at runtime,
loading the addressable `VisualTreeAsset` can't resolve the type from a late-loaded mod assembly.
There is no runtime hook to fix it.

Symptoms if this is hit:
- Log: `Unknown managed type referenced: <asm> <Type>/UxmlSerializedData` +
  `Should not occur! Internal logic error`
- Addressables: `Unable to load asset ... VisualTreeAsset`
- Downstream: `ArgumentOutOfRangeException` at `rootVisualElement[0]` (empty tree),
  `NullReferenceException` on every window toggle

`[UxmlElement]` is only safe for controls built purely in C# that are never embedded in bundled
UXML (e.g. OrbitalSurvey's controls).

## The fix: ship the legacy `UxmlFactory`/`UxmlTraits` system

Legacy pattern: `public new class UxmlFactory : UxmlFactory<T, UxmlTraits> {}` + `UxmlTraits`,
with factories registered via reflection at mod init (mirrors SW1.x's removed
`CustomControls.RegisterFromAssembly`):

```csharp
typeof(VisualElement).Assembly
    .GetType("UnityEngine.UIElements.VisualElementFactoryRegistry")
    .GetMethod("RegisterFactory", BindingFlags.Static | BindingFlags.NonPublic, ...,
        new[] { typeof(IUxmlFactory) }, ...)
    .Invoke(null, new object[] { new MyControl.UxmlFactory() });
```

See `SASExtendedPlugin.RegisterUxmlFactories`. This is the **only** form the shipped mod uses —
it's what actually loads in-game.

**Gotcha:** don't rely on `base.Init(ve, bag, cc)` in `UxmlTraits.Init` — in Unity 6 it's a no-op
that only logs the deprecation warning; it applies **no** attributes, not even
`name`/`class`/`style`. Read and apply those yourself (e.g. `UxmlStringAttributeDescription{ name
= "name" }`). Symptom if missed: UI renders fine (custom attrs work since you read them yourself),
but `root.Q<MyControl>("x")` returns null → NRE in the window controller's `OnEnable`.

(Source: Redux docs `1.3 Upgrading mods for Redux.md` at `E:\GitHub\KSP2\_KSP2 Redux docs`.)

## The cost: UI Builder gets no attribute editing for legacy controls — solved with dual-mode compiling

The legacy system loads fine in-game but Unity 6's UI Builder shows none of the custom attributes
(`Text`, `IsBig`, `IsSmall`, `IsEnabled`, `IsToggled`) in its inspector for it.

**Key insight:** the `.uxml` text file is identical under both systems — same tags, same attribute
names. Which form ends up inside the imported `VisualTreeAsset` is decided at **UXML import time**
by which face the control class exposes to the compiler at that moment. So `SideToggleControl` and
`TabToggleControl` compile in one of two faces, switched by the `SASX_UI_AUTHORING` scripting
define, and the UXML is reimported whenever the mode changes:

- **Default (shipping) face:** legacy `UxmlFactory`/`UxmlTraits` — the only form that loads in-game.
- **Authoring face** (`SASX_UI_AUTHORING` defined): Unity 6 `[UxmlElement]`/`[UxmlAttribute]` —
  full UI Builder attribute inspector, but **cannot ship**.

### How to use

- **To build/edit UI in UI Builder:** Unity menu → **Modding → SAS Extended UI Authoring Mode**
  (checkmark = on). Scripts recompile, the mod's `.uxml` reimports automatically, and UI Builder
  now shows the custom attributes.
- **To build the mod:** toggle the same menu item **off** first, letting the `.uxml` reimport back
  to the legacy form, then run the ThunderKit pipeline as usual.
- **Forgot to toggle off?** Every pipeline (Build for Editor / Build for Player / Deploy to Zip
  File) starts with the `EnsureLegacyUxmlImport` guard job, which **fails the build** with a clear
  message while authoring mode is on. A warning is also logged after every domain reload while the
  mode is on.

### Moving parts

| Piece | Where | Role |
|---|---|---|
| Dual-mode controls | `Assets/SASExtended/Code/UI/Controls/{SideToggleControl,TabToggleControl}.cs` | `#if SASX_UI_AUTHORING` → `[UxmlElement]` + `[UxmlAttribute]` wrapper properties; `#else` → the shipped legacy `UxmlFactory`/`UxmlTraits`. |
| Plugin guard | `SASExtendedPlugin.RegisterUxmlFactories` | Legacy branch registers factories as before; authoring branch compiles to a `LogError` so a mistakenly-shipped authoring build says why the UI is missing. |
| Mode toggle + import sync | `Assets/Utilities/Editor/UiAuthoringMode.cs` | Menu item toggles the define; a `[DidReloadScripts]` hook compares compile state against a marker in `Library/SASExtendedUxmlImportMode.txt` and force-reimports the mod's `.uxml` when they differ, so the imported assets always match the compiled face (survives editor restarts and Library wipes). |
| Pipeline guard | `Assets/Utilities/Editor/EnsureLegacyUxmlImport.cs` + first `Data` entry in the three pipeline assets under `Assets/SASExtended/Pipelines/` | Throws while authoring mode is on; otherwise force-reimports the `.uxml` one more time so the bundle is deterministic. |

### Invariants (do not break)

1. **Attribute names must match exactly** between the two faces (`Text`, `IsBig`, `IsSmall`,
   `IsLong`, `IsEnabled`, `IsToggled`, plus the standard `name`). Adding an attribute means adding
   it to *both* the `UxmlTraits` and the `[UxmlAttribute]` wrapper block.
2. **Declaration order in the authoring face:** `IsEnabled` before `IsToggled` — the
   `UxmlSerializedData` deserializer applies attributes in declaration order and `SetEnabled`
   resets the toggle state (this exact bug was hit during the original port).
3. **Never ship a build made in authoring mode.** The guards exist for this; don't remove them or
   reorder the guard job out of first position.
4. UI Builder re-saves normalize/reorder UXML attributes. The main `SASExtended.uxml` is a
   verbatim 1:1 port — review the git diff after any UI Builder session on it.
