using System;
using System.Threading.Tasks;
using ThunderKit.Core.Attributes;
using ThunderKit.Core.Pipelines;

namespace Utilities.Editor
{
    /// <summary>
    /// Pipeline guard for the SAS Extended dual-mode custom UITK controls. Fails the build outright
    /// if the project is still compiled in UI Authoring Mode (whose UXML import state cannot load
    /// in-game — see <see cref="UiAuthoringMode"/>), and otherwise force-reimports the mod's .uxml
    /// files so the bundled VisualTreeAssets are guaranteed to carry the legacy (UxmlFactory) form
    /// regardless of any stale import state. Runs as the first job in all three SASExtended
    /// pipelines.
    /// </summary>
    [PipelineSupport(typeof(Pipeline))]
    public class EnsureLegacyUxmlImport : PipelineJob
    {
        public override Task Execute(Pipeline pipeline)
        {
            if (UiAuthoringMode.IsAuthoringMode)
            {
                throw new InvalidOperationException(
                    $"UI Authoring Mode ({UiAuthoringMode.Define}) is ON — a mod built now would embed " +
                    "UxmlSerializedData in its UXML that cannot load in-game. Turn it off via " +
                    "'Modding/SAS Extended UI Authoring Mode' (this reimports the UXML automatically) " +
                    "and run the pipeline again.");
            }

            // Belt-and-braces: the post-reload sync in UiAuthoringMode normally keeps the import
            // state current, but reimporting here makes the build deterministic even if that sync
            // was skipped (e.g. the Library marker file was deleted by hand).
            UiAuthoringMode.ReimportModUxml();
            return Task.CompletedTask;
        }
    }
}
