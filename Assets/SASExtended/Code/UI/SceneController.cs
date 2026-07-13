using UitkForKsp2.API;
using UnityEngine.UIElements;

namespace SASExtended.UI
{
    public class SceneController
    {
        public static SceneController Instance { get; } = new SceneController();
        public UIDocument MainGui { get; set; }
        public MainWindowController MainWindowController { get; set; }

        private SceneController() { }

        // Mirror of WindowOptions.Default with this mod's overrides (WindowId, non-clamped moving).
        // Built with an object initializer rather than a `with` expression, since the Unity asmdef
        // compiles at C# 9 (struct `with` requires C# 10).
        private readonly WindowOptions _windowOptions = new WindowOptions
        {
            WindowId = "SASExtended",
            Parent = null,
            IsHidingEnabled = true,
            UseStockScale = true,
            DisableGameInputForTextFields = true,
            BringToFrontOnPointerDown = true,
            BlockGameInput = true,
            MoveOptions = new MoveOptions
            {
                IsMovingEnabled = true,
                CheckScreenBounds = false
            },
            ResizeOptions = ResizeOptions.Default
        };

        /// <summary>
        /// Builds the window from the already-loaded UXML asset.
        /// In Redux, addressables are loaded through the mod's <c>Assets</c> accessor, so the plugin
        /// loads the <see cref="VisualTreeAsset"/> and hands it here (the old SW1.x static AssetManager
        /// is gone).
        /// </summary>
        public void Initialize(VisualTreeAsset windowUxml)
        {
            // Create the window
            var mainWindow = Window.Create(_windowOptions, windowUxml);
            // Add a controller for the UI to the window's game object
            MainWindowController = mainWindow.gameObject.AddComponent<MainWindowController>();
        }

        // User-driven (AppBar button, close button): persists the new state as the player's chosen
        // default so it's restored the next time flight is entered.
        public void ToggleUI(bool state)
        {
            MainWindowController.IsWindowOpen = state;
        }

        // Scene-driven (SASExtendedPlugin.OnGameStateChangedMessage, and the initial hide-on-boot):
        // applies visibility without touching the persisted "last state" - see
        // MainWindowController.SetOpenWithoutPersisting for why that distinction matters.
        public void SetVisible(bool state)
        {
            MainWindowController.SetOpenWithoutPersisting(state);
        }
    }
}
