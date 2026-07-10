using ReduxLib.Configuration;

namespace SASExtended.Utilities
{
    // Centralizes every SWConfiguration binding and binds them all in Initialize(), called from
    // SASExtendedPlugin.OnPreInitialized() - as early in the mod lifecycle as possible. This matters
    // beyond tidiness: the game builds its Settings -> Mods page by taking a one-time snapshot of
    // every mod's SWConfiguration (GameInstance.InitializeSettingsMenuManager, which only includes a
    // mod whose config already has at least one bound section/key at that moment) very early during
    // its own bootstrap - binding lazily from a UI controller's OnEnable (as this mod previously did)
    // runs too late to make that snapshot, so the mod's settings silently never appear in-game for the
    // rest of the session. Pattern mirrors OrbitalSurvey's Utilities/Settings.cs.
    public static class Settings
    {
        public static SASExtendedPlugin Plugin => SASExtendedPlugin.Instance;

        // "Window" section
        public static ConfigValue<float> WindowPositionX;
        public static ConfigValue<float> WindowPositionY;
        public static ConfigValue<bool> WindowIsOpen;

        // "UI" section
        public static ConfigValue<float> StatusRefreshInterval;
        public static ConfigValue<bool> StatusLoggingEnabled;

        public static void Initialize()
        {
            // WINDOW
            WindowPositionX = new(Plugin.SWConfiguration.Bind(
                "Window",
                "Window horizontal position (px)",
                -1f,
                "Saved window horizontal position in pixels. -1 means the window has never been moved yet."
                ));

            WindowPositionY = new(Plugin.SWConfiguration.Bind(
                "Window",
                "Window vertical position (px)",
                -1f,
                "Saved window vertical position in pixels. -1 means the window has never been moved yet."
                ));

            WindowIsOpen = new(Plugin.SWConfiguration.Bind(
                "Window",
                "Window open by default",
                false,
                "Whether the SAS Extended window is shown automatically when entering flight."
                ));

            // UI
            StatusRefreshInterval = new(Plugin.SWConfiguration.Bind(
                "UI",
                "Status readout refresh interval (sec)",
                0.2f,
                "How often (in seconds) the status readout line refreshes while the window is open.",
                new RangeConstraint<float>(0.05f, 2f)
                ));

            StatusLoggingEnabled = new(Plugin.SWConfiguration.Bind(
                "UI",
                "Enable status readout",
                true,
                "Whether the status readout line is computed/updated at all. Disable to skip this work entirely."
                ));
        }
    }
}
