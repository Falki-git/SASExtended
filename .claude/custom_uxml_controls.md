# Custom UITK controls: use legacy UxmlFactory, not [UxmlElement]

For a custom control that a **bundled UXML instantiates by tag** and is loaded by a
**late-loaded SpaceWarp mod assembly**, use the legacy `UxmlFactory`/`UxmlTraits`
system, registered manually at mod init. Do **not** use Unity 6's
`[UxmlElement]`/`[UxmlAttribute]` here.

## Why `[UxmlElement]` fails

It serializes the control into the `VisualTreeAsset` as native
`[SerializeReference] UxmlSerializedData`. The generated `Register()` that resolves
this is `[Conditional("UNITY_EDITOR")]`, and `UxmlSerializedDataRegistry` is
editor-only — so at runtime, loading the addressable VisualTreeAsset can't resolve
the type from a late-loaded mod assembly. There is no runtime hook to fix it.

Symptoms:
- Log: `Unknown managed type referenced: <asm> <Type>/UxmlSerializedData` +
  `Should not occur! Internal logic error`
- Addressables: `Unable to load asset ... VisualTreeAsset`
- Downstream: `ArgumentOutOfRangeException` at `rootVisualElement[0]` (empty tree),
  `NullReferenceException` on every window toggle

`[UxmlElement]` is only safe for controls built purely in C# that are never embedded
in bundled UXML (e.g. OrbitalSurvey's controls).

## The fix

Legacy pattern: `public new class UxmlFactory : UxmlFactory<T, UxmlTraits> {}` +
`UxmlTraits`, with factories registered via reflection at mod init (mirrors SW1.x's
removed `CustomControls.RegisterFromAssembly`):

```csharp
typeof(VisualElement).Assembly
    .GetType("UnityEngine.UIElements.VisualElementFactoryRegistry")
    .GetMethod("RegisterFactory", BindingFlags.Static | BindingFlags.NonPublic, ...,
        new[] { typeof(IUxmlFactory) }, ...)
    .Invoke(null, new object[] { new MyControl.UxmlFactory() });
```

See `SASExtendedPlugin.RegisterUxmlFactories`.

## Gotcha: `UxmlTraits.Init`

Don't rely on `base.Init(ve, bag, cc)` — in Unity 6 it's a no-op that only logs the
deprecation warning; it applies **no** attributes, not even `name`/`class`/`style`.
Read and apply those yourself (e.g. `UxmlStringAttributeDescription{ name = "name" }`).

Symptom if missed: UI renders fine (custom attrs work since you read them yourself),
but `root.Q<MyControl>("x")` returns null → NRE in the window controller's `OnEnable`.

Source: Redux docs `1.3 Upgrading mods for Redux.md` at `E:\GitHub\KSP2\_KSP2 Redux docs`.
