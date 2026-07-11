using System.Collections.Generic;
using ReduxLib.Configuration;
using SASExtended.Models;

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

        // Remembered Heading/Pitch/Roll triple per mode that uses the generic offset mechanism
        // (BuildPointingRotation/BuildTargetOrientationRotation), and the remembered Hover vertical
        // velocity below. Deliberately session-only (SessionValue, not ConfigValue/SWConfiguration) -
        // these should persist across mode/vessel switches within a play session but must NOT survive
        // a game restart, unlike the rest of this file. KillRot/None don't use offsets at all, and
        // Hover only exposes Roll (see mod_specifics.md), so neither is in here.
        public static readonly Dictionary<AttitudeMode, (SessionValue<float> Heading, SessionValue<float> Pitch, SessionValue<float> Roll)> AttitudeOffsets = new();

        private static readonly AttitudeMode[] OffsetModes =
        {
            AttitudeMode.Maneuver,
            AttitudeMode.OrbitPrograde, AttitudeMode.OrbitRetrograde,
            AttitudeMode.OrbitNormal, AttitudeMode.OrbitAntiNormal,
            AttitudeMode.OrbitRadialIn, AttitudeMode.OrbitRadialOut,
            AttitudeMode.SurfaceSvelPlus, AttitudeMode.SurfaceSvelMinus,
            AttitudeMode.SurfaceHvelPlus, AttitudeMode.SurfaceHvelMinus,
            AttitudeMode.SurfaceSurf, AttitudeMode.SurfaceUp,
            AttitudeMode.TargetPlus, AttitudeMode.TargetMinus,
            AttitudeMode.TargetRvelPlus, AttitudeMode.TargetRvelMinus,
            AttitudeMode.TargetParPlus, AttitudeMode.TargetParMinus,
            AttitudeMode.SpecialStarPlus, AttitudeMode.SpecialStarMinus
        };

        // Remembered Hover vertical velocity - session-only, see the comment on AttitudeOffsets above.
        public static SessionValue<float> HoverVerticalVelocity;

        // "Diagnostics" section
        public static ConfigValue<bool> VerboseLoggingEnabled;

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

            // ATTITUDE OFFSETS (session-only - see the field comment above)
            foreach (var mode in OffsetModes)
            {
                // SurfaceSurf needs a 90/90/-90 "level, nose-forward" default instead of the usual
                // 0/0/0 - BuildPointingRotation's LookRotation(north, upwards) doesn't land on a level
                // attitude at a zero offset the way every other mode's target vector does.
                var (defaultHeading, defaultPitch, defaultRoll) = mode == AttitudeMode.SurfaceSurf
                    ? (90f, 90f, -90f)
                    : (0f, 0f, 0f);

                AttitudeOffsets[mode] = (
                    new SessionValue<float>(defaultHeading),
                    new SessionValue<float>(defaultPitch),
                    new SessionValue<float>(defaultRoll));
            }

            // HOVER (session-only - see the field comment above)
            HoverVerticalVelocity = new SessionValue<float>(0f);

            // DIAGNOSTICS
            VerboseLoggingEnabled = new(Plugin.SWConfiguration.Bind(
                "Diagnostics",
                "Enable verbose SAS diagnostics logging",
                false,
                "Whether SetRotation/Hover build and emit their detailed per-tick debug log lines. " +
                "Leave off unless troubleshooting - building these strings costs time every tick even " +
                "when Debug-level logging is filtered out."
                ));
        }
    }
}
