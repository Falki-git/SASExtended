# Custom UI Toolkit controls in mod-shipped UXML fail to load under KSP2 Redux (Unity 6)

**Author:** Falki (SAS Extended)
**Date:** 2026-07-07
**Stack:** KSP2 Redux · SpaceWarp2 ≥ 2.0.0 · ReduxLib · UitkForKsp2 · Unity **6000.4.1f1** · ThunderKit + Addressables · mod assembly compiled at C# **LangVersion 9.0**

---

## TL;DR

A SpaceWarp mod that ships a **UXML loaded via Addressables** which **instantiates custom UI Toolkit
controls by tag** cannot use the Unity 6 `[UxmlElement]` / `UxmlSerializedData` system: the
VisualTreeAsset fails to load at runtime with a **native** `Unknown managed type referenced …
/UxmlSerializedData` error, because Unity's native `[SerializeReference]` deserializer can't resolve a
type that lives in a **late-loaded** (SpaceWarp) mod assembly, and there is **no runtime registry** to
fix it.

The only working approach today is the **deprecated** `UxmlFactory` / `UxmlTraits` system, with the
mod **manually registering its factories at init via reflection** into the internal
`VisualElementFactoryRegistry` — i.e. re-implementing what SpaceWarp 1.x's (now-removed)
`CustomControls.RegisterFromAssembly` used to do.

This works at runtime but has an editor cost: **UI Builder cannot read/edit `UxmlTraits` attributes**,
so authors must set those attributes as UXML text.

**Ask to the Redux team:** provide a supported way for mods to register custom UITK controls at
runtime — at minimum, reinstate a `CustomControls.RegisterFromAssembly`-equivalent in UitkForKsp2 for
the `UxmlFactory` path; ideally, a hook to register mod-side `UxmlSerializedData` so `[UxmlElement]`
becomes usable in mods.

---

## 1. Scenario that triggers it

- A mod extends `Redux.ExtraModTypes.KerbalMod` (assembly loaded late, at mod-init time).
- The mod ships a `.uxml` window built into its Addressables bundle and loads it with
  `Assets.LoadAssetAsync<VisualTreeAsset>(...)`.
- That UXML **instantiates custom controls by tag**, e.g.:

  ```xml
  <SASExtended.UI.Controls.SideToggleControl Text="KILL\nROT" IsEnabled="true" name="killrot" />
  ```

  where `SideToggleControl : UnityEngine.UIElements.Button` lives in the mod's own assembly.

- The controls were authored with the **Unity 6 pattern**:

  ```csharp
  [UxmlElement]
  public partial class SideToggleControl : Button
  {
      [UxmlAttribute("Text")] public string TextValue { get; set; }
      // ...
  }
  ```

> Note: mods whose custom controls are **built in C# and never embedded in a bundled UXML** (e.g.
> Orbital Survey) do **not** hit this — they never exercise the serialized-control load path.

## 2. Symptoms (Player.log)

```
Unknown managed type referenced: SASExtended  SASExtended.UI.Controls.SideToggleControl/UxmlSerializedData
Should not occur! Internal logic error: please report bug.
[System] Exception passed from Unity.Addressables! [Unable to load asset of type
         UnityEngine.UIElements.VisualTreeAsset from location Assets/SASExtended/UI/SASExtended.uxml.]

ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
  at UnityEngine.UIElements.VisualElement.get_Item (System.Int32 key)
  at SASExtended.UI.MainWindowController.OnEnable ()      // _root = _window.rootVisualElement[0];

[SASExtended] System.NullReferenceException: Object reference not set to an instance of an object
  at SASExtended.UI.MainWindowController.set_IsWindowOpen (System.Boolean value)   // _root is null
  at SASExtended.UI.SceneController.ToggleUI (System.Boolean state)
```

**One root cause, three visible errors:** the VisualTreeAsset never loads → the cloned tree is empty →
`rootVisualElement[0]` throws in `OnEnable` → `_root` stays null → every window toggle (including the
app-bar button click) throws `NullReferenceException`. To the player, the app-bar button appears but
"nothing happens."

## 3. Root-cause analysis

### 3.1 It is not a stale build or a missing type
The deployed mod assembly **does** contain the source-generated serialization companions:

```
Class SASExtended.UI.Controls.SideToggleControl.UxmlSerializedData
Class SASExtended.UI.Controls.TabToggleControl.UxmlSerializedData
```

So the type exists in a loaded assembly. This is a **type-resolution failure at asset-load time**, not
a compile/deploy problem.

### 3.2 There is no runtime registry for `UxmlSerializedData`
The generated registration hook is **editor-only**:

```csharp
[RegisterUxmlCache]
[Conditional("UNITY_EDITOR")]          // <-- stripped in player builds
public static void Register()
{
    UxmlDescriptionCache.RegisterType(typeof(UxmlSerializedData), /* … */);
}
```

`UxmlSerializedDataRegistry` is **not present in the runtime** `UnityEngine.UIElementsModule.dll`
(6000.4.1f1) — it is an editor-only type. So there is no managed runtime registry a mod could populate,
and calling `Register()` at runtime would be a no-op anyway (it only feeds the editor's UI-Builder
description cache).

### 3.3 The failure is in native serialization, before any managed hook
The string `Unknown managed type referenced` and `Should not occur! Internal logic error: please report
bug.` **do not appear anywhere in the managed `UnityEngine.UIElementsModule.dll`** — they are emitted by
the **native** engine serializer. In Unity 6, a VisualTreeAsset stores each `[UxmlElement]` element as a
native **`[SerializeReference] UxmlSerializedData`**. When the Addressables bundle is loaded, the native
`[SerializeReference]` deserializer must resolve `SideToggleControl/UxmlSerializedData` — and it cannot
resolve a type that lives in an assembly **loaded late** (SpaceWarp loads mod assemblies after the
engine's serialization type system is established). Hence the native "unknown managed type."

**Consequence:** because this is native serialization failing *before* any managed UI Toolkit code runs,
there is no managed API a mod can call to make `[UxmlElement]` controls load. The `[UxmlElement]` path is
fundamentally incompatible with *late-loaded* mod assemblies that embed the controls in *bundled* UXML.

## 4. Working approach (legacy `UxmlFactory` + manual registration)

The legacy `UxmlFactory` / `UxmlTraits` system stores elements **by type-name string** (no
`[SerializeReference]`) and resolves them **at clone time** via the **runtime**
`VisualElementFactoryRegistry` — which sidesteps the native-serialization failure entirely.

`UxmlFactory`, `UxmlTraits`, `IUxmlFactory`, and the `Uxml*AttributeDescription` types are **deprecated
but fully functional** in 6000.4.1f1 — the obsolete attribute is a **warning, not an error**:

```csharp
[Obsolete("UxmlFactory<TCreatedType, TTraits> is deprecated and will be removed. Use UxmlElementAttribute instead.", false)]
//                                                                                                              ^ isError = false
```
Unity even hard-codes `internal const bool UxmlFactoryObsoleteIsError = false;` and
`UxmlTraitsObsoleteIsError = false;`.

**Step 1 — declare controls with `UxmlFactory`/`UxmlTraits`** (not `[UxmlElement]`):

```csharp
public class SideToggleControl : Button
{
    public string TextValue { get => _text.text; set => _text.text = value; }
    // …
    public new class UxmlFactory : UxmlFactory<SideToggleControl, UxmlTraits> { }
    public new class UxmlTraits : VisualElement.UxmlTraits
    {
        UxmlStringAttributeDescription _name = new UxmlStringAttributeDescription { name = "Text", defaultValue = "Toggle" };
        UxmlBoolAttributeDescription _isEnabled = new UxmlBoolAttributeDescription { name = "IsEnabled" };
        // …
        public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
        {
            // Do NOT call base.Init() — see §4.1. Apply every attribute we need ourselves,
            // INCLUDING the standard `name` (used by the window controller's Q<>() lookups).
            ve.name = _elementName.GetValueFromBag(bag, cc);   // _elementName = { name = "name" }
            if (ve is SideToggleControl c)
            {
                c.TextValue = _name.GetValueFromBag(bag, cc);
                c.SetEnabled(_isEnabled.GetValueFromBag(bag, cc));
                // …
            }
        }
    }
}
```

### 4.1 Critical: Unity 6's base `UxmlTraits.Init` is a no-op — apply `name`/attributes yourself

This is the subtle part that breaks a naive `UxmlFactory` port. In Unity 6 (6000.4.1f1), the base
`UnityEngine.UIElements.UxmlTraits.Init` **applies no attributes at all** — it only logs the deprecation
warning:

```csharp
public virtual void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
{
    Debug.LogWarningFormat("Control {0} uses the deprecated UxmlTraits API. Its attributes were ignored
        on import, which may cause visual errors or missing data. …", ve.GetType().FullName);
}
```

Consequences if a control's `UxmlTraits.Init` calls `base.Init(...)` and relies on it for standard
attributes:

- The control's **custom** attributes still work (you read those yourself via `GetValueFromBag`), so the
  UI **renders correctly** — text, styling, LEDs all look right. This masks the bug.
- But **`name` (and `class`, `style`, `picking-mode`, …) are never applied.** A window controller that
  wires elements with `root.Q<MyControl>("someName")` gets **null**, and the first
  `null.SomeMethod()` throws `NullReferenceException` in `OnEnable`, aborting the rest of the wiring — so
  the window shows but its buttons don't do anything.
- The "Its attributes were ignored on import" message is **not** an import-time event — it is this
  no-op `Init` firing **once per control instance** at clone time.

**Fix:** don't call `base.Init`; read and apply the standard attributes you depend on. At minimum apply
`name`:

```csharp
UxmlStringAttributeDescription _elementName = new UxmlStringAttributeDescription { name = "name", defaultValue = "" };
// … in Init(), instead of base.Init(ve, bag, cc):
ve.name = _elementName.GetValueFromBag(bag, cc);
```

Skipping `base.Init` also suppresses the per-instance deprecation warning.

**Step 2 — register the factories at mod init**, before the UXML is loaded/cloned. UI Toolkit only
auto-registers `UxmlFactory` controls for assemblies present at **game startup**; a late-loaded mod
assembly is missed, so we register manually. `VisualElementFactoryRegistry.RegisterFactory` is
`internal`, so it is invoked via reflection:

```csharp
// In KerbalMod.OnPreInitialized()
private static void RegisterUxmlFactories()
{
    var registryType = typeof(VisualElement).Assembly
        .GetType("UnityEngine.UIElements.VisualElementFactoryRegistry");
    var register = registryType?.GetMethod(
        "RegisterFactory",
        BindingFlags.Static | BindingFlags.NonPublic,
        null, new[] { typeof(IUxmlFactory) }, null);

    register.Invoke(null, new object[] { new SideToggleControl.UxmlFactory() });
    register.Invoke(null, new object[] { new TabToggleControl.UxmlFactory() });
}
```

This is exactly what SpaceWarp 1.x's `UitkForKsp2.API.CustomControls.RegisterFromAssembly(...)` did —
that helper has been **removed** in the current UitkForKsp2, so mods must currently reflect into the
internal API themselves.

> Editor note: after switching a control back to `UxmlFactory`, the `.uxml` must be **re-imported** so
> it re-serializes the legacy (string-based) way instead of as `UxmlSerializedData`, and the
> Addressables bundle rebuilt.

## 5. The remaining trade-off (UI Builder)

With `UxmlTraits`, **UI Builder cannot read or edit the control's attributes** — its Attributes panel
shows:

> *"Attributes for this control failed to load because it uses UxmlTraits, a deprecated API. To make
> attributes readable and editable, update the control to use UxmlAttribute."*

This is editor-only (the control still renders in the canvas and works at runtime); attributes must be
set as **UXML text**. There is currently no way to get **both** UI-Builder attribute editing **and**
mod-runtime loading:

| Control declaration | UI Builder can edit attributes | Loads at runtime in a late-loaded mod |
|---|:---:|:---:|
| `[UxmlElement]` / `UxmlSerializedData` | ✅ | ❌ (native `[SerializeReference]` can't resolve the type) |
| `UxmlFactory` / `UxmlTraits` | ❌ | ✅ (factory resolved at clone time from a runtime registry) |

## 6. Requests to the Redux team

1. **Reinstate a `CustomControls.RegisterFromAssembly` equivalent in UitkForKsp2** (short term). It was
   the exact tool for this, and its removal is why mods must now reflect into Unity's internal
   `VisualElementFactoryRegistry`. A thin public wrapper would remove the reflection and make the
   `UxmlFactory` path first-class for mods again.
2. **Investigate a supported path for `[UxmlElement]` in mods** (longer term) — e.g. a mechanism to
   register a late-loaded assembly's `UxmlSerializedData` types with the native serializer / UXML system
   so bundled UXML that embeds mod controls can load. Without this, mods are stuck on the deprecated
   `UxmlFactory` API that Unity says "will be removed."
3. **Document the recommended pattern** for custom UITK controls in mods (which system to use, that
   factories must be registered at init, and the UI-Builder limitation), so future mod authors don't
   rediscover this from native serialization errors.

## 7. Verification notes

Confirmed via the shipped `Player.log`, the deployed mod DLL, and decompilation of
`UnityEngine.UIElementsModule.dll` (Unity **6000.4.1f1** editor install):

- Deployed mod assembly **contains** `…SideToggleControl.UxmlSerializedData` / `…TabToggleControl.UxmlSerializedData`.
- Generated `UxmlSerializedData.Register()` is `[RegisterUxmlCache][Conditional("UNITY_EDITOR")]`.
- **No** `UxmlSerializedDataRegistry` in the runtime `UnityEngine.UIElementsModule.dll`.
- `Unknown managed type referenced` / `Should not occur! Internal logic error` strings are **absent**
  from the managed module (i.e. native-origin).
- `VisualElementFactoryRegistry` **is** present at runtime; `RegisterFactory(IUxmlFactory)` is
  `protected static` (internal class).
- `UxmlFactory<,>`, `UxmlTraits`, `IUxmlFactory`, `Uxml*AttributeDescription` are present and marked
  `[Obsolete(…, isError: false)]`.
- `UnityEngine.UIElements.UxmlTraits.Init(ve, bag, cc)` decompiles to a **single `Debug.LogWarningFormat`
  call and nothing else** — it applies no attributes (confirms §4.1 and that the "ignored on import"
  message is a per-instance clone-time log, not an importer event).

*Status: the `UxmlFactory` workaround plus manual factory registration loads the bundled UXML and the
window renders correctly in-game (confirmed). The `base.Init` no-op issue in §4.1 (missing `name` →
`NullReferenceException` in the window controller) was diagnosed from the same build and fixed by applying
`name` manually; final in-game confirmation that controls are fully interactive was pending at time of
writing.*
