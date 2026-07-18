using System;
using System.Reflection;
using JetBrains.Annotations;
using KSP.Game;
using KSP.Messages;
using Redux.ExtraModTypes;
using SASExtended.Managers;
using SASExtended.UI;
using SASExtended.UI.Controls;
using SASExtended.Utilities;
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
            Instance = this;

            // Bind every SWConfiguration entry as early as possible - the game snapshots each mod's
            // config (GameInstance.InitializeSettingsMenuManager) very early in its own bootstrap to
            // build the Settings -> Mods page, and a mod with no bound sections/keys yet at that point
            // is silently skipped for the rest of the session. See Utilities/Settings.cs.
            Settings.Initialize();

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
        /// for UI Builder authoring — see <c>.claude/custom_uxml_controls.md</c>.)
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

            // Start hidden - this runs once at game boot, well before any flight scene is entered, so
            // there's nothing to restore yet. Use the non-persisting setter: unlike the AppBar
            // callback above, this isn't the player choosing to close the window, so it must not
            // overwrite Settings.WindowIsOpen (see OnGameStateChangedMessage, which is what actually
            // restores/hides the window using that persisted value once flight is entered/left).
            SceneController.Instance.SetVisible(false);

            // Create the SAS control loop (a MonoBehaviour that recomputes and applies the target
            // orientation every frame). Parent it to this mod so it lives for the session.
            var providers = new GameObject("SASExtended_Providers");
            providers.transform.parent = transform;
            providers.AddComponent<SASManager>();
            // Draws the optional in-world flight-axis visuals (control axes, commanded-attitude arrow,
            // CoM marker) toggled from the window. Lives alongside SASManager for the session.
            providers.AddComponent<FlightAxesVisualizer>();
            // Draws the optional flight-view landing prediction (trajectory line + ground marker),
            // toggled from the window. Airless bodies only for now - see
            // .claude/landing_prediction_plan.md.
            providers.AddComponent<LandingPredictionManager>();

            // Apply Harmony patches in this assembly (the hover throttle override on FlightInputHandler).
            CreateHarmonyAndPatchAll();

            // Hide the window when leaving flight/Map3D, and restore it to the player's last
            // explicitly-chosen open/closed state when (re-)entering. Map3D is just the map view while
            // still in flight, so switching between the two isn't a "leaving flight" transition.
            Messages.PersistentSubscribe<GameStateChangedMessage>(OnGameStateChangedMessage);
        }

        private static bool IsFlightState(GameState state) => state == GameState.FlightView || state == GameState.Map3DView;

        private void OnGameStateChangedMessage(MessageCenterMessage message)
        {
            var msg = (GameStateChangedMessage)message;

            bool wasInFlight = IsFlightState(msg.PreviousState);
            bool isInFlight = IsFlightState(msg.CurrentState);

            if (wasInFlight == isInFlight)
                return;

            SceneController.Instance.SetVisible(isInFlight && Settings.WindowIsOpen.Value);
        }

        /// <summary>
        /// Runs after all mods have done their 2nd stage initialization.
        /// </summary>
        public override void OnPostInitialized() { }
    }
}
