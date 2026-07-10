using System;
using System.Reflection;
using JetBrains.Annotations;
using Redux.ExtraModTypes;
using SASExtended.Managers;
using SASExtended.UI;
using SASExtended.UI.Controls;
using SpaceWarp2.UI.API.Appbar;
using UnityEngine;
using UnityEngine.UIElements;

namespace SASExtended
{
    public class SASExtendedPlugin : KerbalMod
    {
        // Useful in case some other mod wants to use this mod as a dependency
        [PublicAPI] public const string ModGuid = "SASExtended";
        [PublicAPI] public const string ModName = "SAS Extended";

        /// Singleton instance of the plugin class
        [PublicAPI] public static SASExtendedPlugin Instance { get; set; }

        // AppBar button IDs
        internal const string ToolbarFlightButtonID = "BTN-SASExtendedFlight";
        internal const string ToolbarOabButtonID = "BTN-SASExtendedOAB";
        internal const string ToolbarKscButtonID = "BTN-SASExtendedKSC";

        // Addressable keys for this mod's UI assets.
        // NOTE: these must match the addressable addresses assigned to the assets in the Unity editor
        // (Addressables Groups window / KSP2 Unity Tools). The Redux SDK addresses assets under the
        // "{ModGuid}/{bundle}/{lowercased path under Assets}" scheme; confirm/adjust if the build uses
        // a different address.
        private const string WindowUxmlAddress = "Assets/SASExtended/UI/SASExtended.uxml";
        private const string FlightIconAddress = "Assets/SASExtended/UI/Images/Icons/retrograde.png";

        private static readonly ReduxLib.Logging.ILogger _logger =
            ReduxLib.ReduxLib.GetLogger("SASExtended|SASExtendedPlugin");

        /// <summary>
        /// Runs before the game is loaded, as early as possible.
        /// </summary>
        public override void OnPreInitialized()
        {
            RegisterUxmlFactories();
        }

        /// <summary>
        /// Registers this mod's custom UITK control factories so a bundled UXML that instantiates them
        /// can be built.
        ///
        /// UI Toolkit only auto-registers <c>UxmlFactory</c> controls for assemblies present at game
        /// startup. A SpaceWarp mod assembly is loaded late, so its factories are never registered and
        /// cloning the window UXML would fail to create the custom elements. We register them manually
        /// by invoking the internal <c>VisualElementFactoryRegistry.RegisterFactory</c> via reflection —
        /// this is what SW1.x's (now-removed) <c>CustomControls.RegisterFromAssembly</c> used to do.
        ///
        /// (The Unity 6 <c>[UxmlElement]</c>/<c>UxmlSerializedData</c> path can't be used here: it
        /// serializes the controls into the VisualTreeAsset as native <c>[SerializeReference]</c>, which
        /// Unity's native serializer cannot resolve for a late-loaded mod assembly, and there is no
        /// runtime registry to fix it. So the controls ship with the legacy UxmlFactory system; the
        /// <c>[UxmlElement]</c> face exists only behind the editor-side <c>SASX_UI_AUTHORING</c> define
        /// for UI Builder authoring — see <c>.claude/ui_authoring_mode.md</c>.)
        /// </summary>
        private static void RegisterUxmlFactories()
        {
#if SASX_UI_AUTHORING
            // This assembly was compiled in UI Authoring Mode (SASX_UI_AUTHORING): the custom controls
            // expose Unity 6 UxmlSerializedData instead of legacy factories, and a UXML bundled in
            // this state cannot load in-game. Authoring mode is design-time-only — the ThunderKit
            // pipelines refuse to build while it is on (see EnsureLegacyUxmlImport), so if this log
            // line ever appears in-game, someone bypassed the pipeline guard.
            _logger.LogError(
                "SASExtended was built with SASX_UI_AUTHORING set; custom UI controls will not load " +
                "in-game. Turn off 'Modding/SAS Extended UI Authoring Mode' in the Unity editor and rebuild.");
#else
            try
            {
                var registryType = typeof(VisualElement).Assembly
                    .GetType("UnityEngine.UIElements.VisualElementFactoryRegistry");
                var register = registryType?.GetMethod(
                    "RegisterFactory",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(IUxmlFactory) },
                    null);

                if (register == null)
                {
                    _logger.LogError(
                        "VisualElementFactoryRegistry.RegisterFactory not found; custom UI controls will not load.");
                    return;
                }

                register.Invoke(null, new object[] { new SideToggleControl.UxmlFactory() });
                register.Invoke(null, new object[] { new TabToggleControl.UxmlFactory() });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to register custom UXML factories: {ex}");
            }
#endif
        }

        /// <summary>
        /// Runs after the game is loaded and after this mod's assets are loaded.
        /// </summary>
        public override void OnInitialized()
        {
            Instance = this;

            // Load the UI from the mod's addressables.
            var windowUxml = Assets.LoadAssetAsync<VisualTreeAsset>(WindowUxmlAddress).WaitForCompletion();

            // Build the window and its controller.
            SceneController.Instance.Initialize(windowUxml);

            // Register the Flight AppBar button; toggling it shows/hides the window.
            Appbar.RegisterAppButton(
                ModName,
                ToolbarFlightButtonID,
                Assets.LoadAssetAsync<Texture2D>(FlightIconAddress).WaitForCompletion(),
                isOpen => SceneController.Instance.ToggleUI(isOpen)
            );

            // Start hidden; the AppBar button opens the window.
            SceneController.Instance.ToggleUI(false);

            // Create the SAS control loop (a MonoBehaviour that recomputes and applies the target
            // orientation every frame). Parent it to this mod so it lives for the session.
            var providers = new GameObject("SASExtended_Providers");
            providers.transform.parent = transform;
            providers.AddComponent<SASManager>();

            // Apply Harmony patches in this assembly (the hover throttle override on FlightInputHandler).
            CreateHarmonyAndPatchAll();
        }

        /// <summary>
        /// Runs after all mods have done their 2nd stage initialization.
        /// </summary>
        public override void OnPostInitialized() { }
    }
}
