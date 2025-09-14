using SpaceWarp.API.Assets;
using UitkForKsp2.API;
using UnityEngine.UIElements;

namespace SASExtended.UI;

public class SceneController
{
    public static SceneController Instance { get; } = new();
    public UIDocument MainGui { get; set; }
    public MainWindowController MainWindowController { get; set; }

    private SceneController() => InitializeUi();

    private readonly WindowOptions _windowOptions = WindowOptions.Default with
    {
        WindowId = "SASExtended",
        IsHidingEnabled = true,
        MoveOptions = new MoveOptions
        {
            IsMovingEnabled = true,
            CheckScreenBounds = false
        },
        DisableGameInputForTextFields = true
    };


    private void InitializeUi()
    {
        // Load the UI from the asset bundle
        var myFirstWindowUxml = AssetManager.GetAsset<VisualTreeAsset>(
            // The case-insensitive path to the asset in the bundle is composed of:
            // - The mod GUID:
            $"{SASExtendedPlugin.ModGuid}/" +
            // - The name of the asset bundle:
            "SASExtended_ui/" +
            // - The path to the asset in your Unity project (without the "Assets/" part)
            //"ui/myfirstwindow/myfirstwindow.uxml"
            "ui/sasextended.uxml"
        );

        // Create the window
        var mainWindow = Window.Create(_windowOptions, myFirstWindowUxml);
        // Add a controller for the UI to the window's game object
        MainWindowController = mainWindow.gameObject.AddComponent<MainWindowController>();
    }

    public void ToggleUI(bool state)
    {
        MainWindowController.IsWindowOpen = state;
    }
}