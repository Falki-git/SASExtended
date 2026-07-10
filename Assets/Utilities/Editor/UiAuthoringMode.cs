using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Editor-side switch for the SAS Extended custom UITK controls' dual-mode UXML support.
    ///
    /// The controls (SideToggleControl/TabToggleControl) compile in one of two faces:
    /// - default ("legacy"): UxmlFactory/UxmlTraits — the only form that can load from a bundled
    ///   VisualTreeAsset in-game (see .claude/custom_uxml_controls.md), but Unity 6's UI Builder
    ///   offers no attribute editing for it;
    /// - authoring (SASX_UI_AUTHORING defined): Unity 6 [UxmlElement]/[UxmlAttribute] — full UI
    ///   Builder attribute inspector, but a VisualTreeAsset imported in this state embeds
    ///   [SerializeReference] UxmlSerializedData the game cannot resolve for a late-loaded mod
    ///   assembly, so it must never be shipped.
    ///
    /// Which form ends up inside the imported VisualTreeAsset is decided at UXML IMPORT time by the
    /// compile state — the .uxml text itself is identical in both modes. So this class (1) toggles
    /// the define from a menu item, and (2) force-reimports the mod's .uxml files after every domain
    /// reload that changed the mode, so the imported assets always match the compiled face.
    /// EnsureLegacyUxmlImport additionally makes the ThunderKit pipelines fail while authoring mode
    /// is on.
    /// </summary>
    public static class UiAuthoringMode
    {
        public const string Define = "SASX_UI_AUTHORING";

        private const string MenuPath = "Modding/SAS Extended UI Authoring Mode";
        private const string UxmlSearchFolder = "Assets/SASExtended";

        // Lives in Library/ on purpose: per-project, unversioned, and wiped together with the import
        // cache whose state it mirrors (a fresh Library reimports everything anyway).
        private static readonly string MarkerPath = Path.Combine("Library", "SASExtendedUxmlImportMode.txt");

        public static bool IsAuthoringMode =>
#if SASX_UI_AUTHORING
            true;
#else
            false;
#endif

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            var defines = new List<string>(
                PlayerSettings.GetScriptingDefineSymbols(target)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

            if (!defines.Remove(Define))
                defines.Add(Define);

            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
            // Scripts recompile now; SyncImportStateAfterReload picks the change up after the domain
            // reload and reimports the UXML so the imported assets match the new compile state.
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, IsAuthoringMode);
            return true;
        }

        [DidReloadScripts]
        private static void SyncImportStateAfterReload()
        {
            // Asset imports shouldn't run while the editor is still finishing the reload; defer a tick.
            EditorApplication.delayCall += () =>
            {
                string current = IsAuthoringMode ? "authoring" : "legacy";
                string last = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;

                if (last != current)
                {
                    ReimportModUxml();
                    File.WriteAllText(MarkerPath, current);
                }

                if (IsAuthoringMode)
                {
                    Debug.LogWarning(
                        "[SASExtended] UI Authoring Mode is ON (" + Define + "): UI Builder attribute " +
                        "editing works, but a mod built now cannot load its UI in-game. Turn it off via " +
                        "'" + MenuPath + "' before building (the ThunderKit pipelines refuse to run " +
                        "while it is on).");
                }
            };
        }

        /// <summary>
        /// Force-reimports every .uxml under the mod folder so the imported VisualTreeAssets are
        /// regenerated against the currently compiled control face (legacy attribute bags vs.
        /// UxmlSerializedData).
        /// </summary>
        public static void ReimportModUxml()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { UxmlSearchFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Debug.Log(
                    $"[SASExtended] Reimported {path} in " +
                    $"{(IsAuthoringMode ? "authoring (UxmlSerializedData)" : "legacy (UxmlFactory)")} mode.");
            }
        }
    }
}
