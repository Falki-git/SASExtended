using AwesomeTechnologies.Utility;
using BepInEx;
using BepInEx.Logging;
using JetBrains.Annotations;
using KSP.Game;
using KSP.Game.Missions.Definitions;
using KSP.Sim;
using KSP.VolumeCloud;
using SASExtended.Managers;
using SASExtended.UI;
using SpaceWarp;
using SpaceWarp.API.Assets;
using SpaceWarp.API.Game.Waypoints;
using SpaceWarp.API.Mods;
using SpaceWarp.API.UI;
using SpaceWarp.API.UI.Appbar;
using System.Reflection;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SASExtended;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency(SpaceWarpPlugin.ModGuid, SpaceWarpPlugin.ModVer)]
public class SASExtendedPlugin : BaseSpaceWarpPlugin
{
    // Useful in case some other mod wants to use this mod a dependency
    [PublicAPI] public const string ModGuid = MyPluginInfo.PLUGIN_GUID;
    [PublicAPI] public const string ModName = MyPluginInfo.PLUGIN_NAME;
    [PublicAPI] public const string ModVer = MyPluginInfo.PLUGIN_VERSION;

    /// Singleton instance of the plugin class
    [PublicAPI] public static SASExtendedPlugin Instance { get; set; }

    // AppBar button IDs
    internal const string ToolbarFlightButtonID = "BTN-SASExtendedFlight";
    internal const string ToolbarOabButtonID = "BTN-SASExtendedOAB";
    internal const string ToolbarKscButtonID = "BTN-SASExtendedKSC";

    private static readonly ManualLogSource _logger = BepInEx.Logging.Logger.CreateLogSource("SASExtendedPlugin");

    /// <summary>
    /// Runs when the mod is first initialized.
    /// </summary>
    public override void OnInitialized()
    {
        base.OnInitialized();

        Instance = this;

        // Load all the other assemblies used by this mod
        LoadAssemblies();

        Appbar.RegisterAppButton(
            ModName,
            ToolbarFlightButtonID,
            AssetManager.GetAsset<Texture2D>($"{Info.Metadata.GUID}/sasextended_ui/images/icons/retrograde.png"),
            isOpen => SceneController.Instance.ToggleUI(isOpen)
            //SceneController.Instance.ToggleUI
        );

        

        // Register OAB AppBar Button
        //Appbar.RegisterOABAppButton(
        //    ModName,
        //    ToolbarOabButtonID,
        //    AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
        //    isOpen => myFirstWindowController.IsWindowOpen = isOpen
        //);

        /*
        // Register KSC AppBar Button
        Appbar.RegisterKSCAppButton(
            ModName,
            ToolbarKscButtonID,
            AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
            () => myFirstWindowController.IsWindowOpen = !myFirstWindowController.IsWindowOpen
        );

        */

        _logger.LogInfo("Initialization is about to begin");
        DebugUI.Instance.InitializeStyles();

        //SASManager.Instance.Initialize();

        SceneController.Instance.ToggleUI(true);

        var providers = new GameObject("SASExtended_Providers");
        providers.transform.parent = this.transform;
        providers.AddComponent<SASManager>();
    }

    /// <summary>
    /// Loads all the assemblies for the mod.
    /// </summary>
    private static void LoadAssemblies()
    {
        // Load the Unity project assembly
        var currentFolder = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.FullName;
        var unityAssembly = Assembly.LoadFrom(Path.Combine(currentFolder, "SASExtended.Unity.dll"));
        // Register any custom UI controls from the loaded assembly
        CustomControls.RegisterFromAssembly(unityAssembly);
    }

    private void Update()
    {
        if (/*Input.GetKey(KeyCode.LeftAlt) && */ Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S))
            DebugUI.Instance.IsDebugWindowOpen = !DebugUI.Instance.IsDebugWindowOpen;
    }

    private void OnGUI() => DebugUI.Instance.OnGUI();

}
