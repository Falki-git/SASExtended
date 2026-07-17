using System;
using System.Linq;
using KSP.UI.Binding;
using SASExtended.Managers;
using SASExtended.Models;
using SASExtended.UI.Controls;
using SASExtended.Utilities;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SASExtended.UI
{

/// <summary>
/// Controller for the SAS Extended window.
/// </summary>
public class MainWindowController : MonoBehaviour
{
    private static readonly ReduxLib.Logging.ILogger _LOGGER = ReduxLib.ReduxLib.GetLogger("SASExtended|MainWindowController");

    // The UIDocument component of the window game object
    private UIDocument _window;

    private VisualElement _root;

    private float _lastStatusUpdateTime = float.NegativeInfinity;
    private Label _statusLabel;

    // The backing field for the IsWindowOpen property
    private bool _isWindowOpen;

    // Guards the one-time SASManager.Disengaged subscription in Update() - see the comment there.
    private bool _subscribedToSasManager;

    private SideToggleControl _offToggle;
    private SideToggleControl _killrotToggle;
    private SideToggleControl _nodeToggle;

    private VisualElement _orbitContainer;
    private VisualElement _surfaceContainer;
    private VisualElement _targetContainer;
    private VisualElement _specialContainer;

    private VisualElement _attitudeControlsContainer;
    private VisualElement _hoverControlsContainer;

    private TabToggleControl _orbitTabToggle;
    private TabToggleControl _surfaceTabToggle;
    private TabToggleControl _targetTabToggle;
    private TabToggleControl _specialTabToggle;

    private SideToggleControl _progradeToggle;
    private SideToggleControl _normalToggle;
    private SideToggleControl _radialInToggle;
    private SideToggleControl _retrogradeToggle;
    private SideToggleControl _antinormalToggle;
    private SideToggleControl _radialOutToggle;

    private SideToggleControl _svelPlusToggle;
    private SideToggleControl _svelMinusToggle;
    private SideToggleControl _surfToggle;
    private SideToggleControl _hvelPlusToggle;
    private SideToggleControl _hvelMinusToggle;
    private SideToggleControl _upToggle;

    private SideToggleControl _targetPlusToggle;
    private SideToggleControl _relativeVelocityPlusToggle;
    private SideToggleControl _parPlusToggle;
    private SideToggleControl _targetMinusToggle;
    private SideToggleControl _relativeVelocityMinusToggle;
    private SideToggleControl _parMinusToggle;

    private SideToggleControl _starPlusToggle;
    private SideToggleControl _starMinusToggle;

    private SideToggleControl _holdToggle;
    private SideToggleControl _hoverToggle;

    private SideToggleControl _hoverVerticalVelocityToggle;
    private FloatField _hoverVerticalVelocityValue;
    private Button _hoverVerticalVelocityMinus;
    private Button _hoverVerticalVelocityPlus;
    private Button _hoverVerticalVelocityZero;
    private SideToggleControl _cancelHorizontalVelocityToggle;

    // Hover's own Roll row - shares the target angle (SASManager.Z) with the shared attitude panel's
    // Roll row (_zToggle/_zValue below), but drives its OWN enabled flag (SASManager.HoverRollEnabled,
    // not the shared ZEnabled - see its field comment for why) since Hover's panel replaces the
    // shared one entirely. Kept in sync at the panel-switch boundary - see UpdatePanelForMode.
    private SideToggleControl _hoverZToggle;
    private FloatField _hoverZValue;
    private Button _hoverZMinus;
    private Button _hoverZPlus;
    private Button _hoverZFirst;
    private Button _hoverZSecond;

    // Flight Axes visual toggles are wired in a loop over FlightAxesVisualizer.ToggleIds (see
    // WireFlightAxesToggles) - they're independent of the mutually-exclusive mode toggles above, so
    // they don't need individual fields or a place in _allModeToggles.

    // Header settings button and the panels it swaps between: clicking it hides the main view
    // (upper + middle containers) and shows the settings container that holds the Flight Axes toggles,
    // and back. All null-guarded so the window still builds while this UXML is being authored.
    private Button _settingsButton;
    private VisualElement _settingsButtonBackground;
    private VisualElement _settingsContainer;
    private VisualElement _upperContainer;
    private VisualElement _middleContainer;
    private VisualElement _footer;
    private bool _settingsOpen;

    private const string SettingsButtonCheckedClass = "settings-button__background--checked";

    // Every mutually-exclusive mode toggle (global modes + all direction buttons across all tabs),
    // used by ClearAllModeToggles/RegisterModeButton so only one is ever shown toggled on.
    private SideToggleControl[] _allModeToggles;

    private SideToggleControl _xToggle;
    private FloatField _xValue;
    private Button _xMinus;
    private Button _xPlus;
    private Button _xFirst;
    private Button _xSecond;
    private Button _xFirstSpecialButton;
    private Button _xSecondSpecialButton;
    private SideToggleControl _yToggle;
    private FloatField _yValue;
    private Button _yMinus;
    private Button _yPlus;
    private Button _yFirst;
    private Button _ySecond;
    private Button _yFirstSpecialButton;
    private Button _ySecondSpecialButton;
    private SideToggleControl _zToggle;
    private FloatField _zValue;
    private Button _zMinus;
    private Button _zPlus;
    private Button _zFirst;
    private Button _zSecond;
    private Button _zFirstSpecialButton;
    private Button _zSecondSpecialButton;


    /// <summary>
    /// The state of the window. Setting this value will open or close the window, and persists the
    /// new state as the player's chosen default (Settings.WindowIsOpen) so it can be restored the
    /// next time flight is entered. For a scene transition forcing the window open/closed - which
    /// must NOT overwrite that persisted choice - use <see cref="SetOpenWithoutPersisting"/> instead.
    /// </summary>
    public bool IsWindowOpen
    {
        get => _isWindowOpen;
        set
        {
            SetOpenWithoutPersisting(value);
            Settings.WindowIsOpen.Value = value;
            SASExtendedPlugin.Instance.SWConfiguration.Save();
        }
    }

    // Applies the open/closed visual state only - doesn't touch Settings.WindowIsOpen. Used by the
    // scene-transition logic (SASExtendedPlugin.OnGameStateChangedMessage) to hide the window when
    // leaving flight/Map3D and restore it when re-entering, without clobbering the player's last
    // explicitly-chosen open/closed preference.
    public void SetOpenWithoutPersisting(bool value)
    {
        _LOGGER.LogDebug($"IsWindowOpen -> {value}");
        _isWindowOpen = value;

        // Set the display style of the root element to show or hide the window
        _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
        // Alternatively, you can deactivate the window game object to close the window and stop it from updating,
        // which is useful if you perform expensive operations in the window update loop. However, this will also
        // mean you will have to re-register any event handlers on the window elements when re-enabled in OnEnable.
        // gameObject.SetActive(value);

        // Update the Flight AppBar button state
        GameObject.Find(SASExtendedPlugin.ToolbarFlightButtonID)
            ?.GetComponent<UIValue_WriteBool_Toggle>()
            ?.SetValue(value);
    }

    /// <summary>
    /// Runs when the window is first created, and every time the window is re-enabled.
    /// </summary>
    private void OnEnable()
    {
        // Get the UIDocument component from the game object
        _window = GetComponent<UIDocument>();

        // Get the root element of the window.
        // Since we're cloning the UXML tree from a VisualTreeAsset, the actual root element is a TemplateContainer,
        // so we need to get the first child of the TemplateContainer to get our actual root VisualElement.
        _root = _window.rootVisualElement[0];

        // Each section below wires one logical piece of the window (a tab's toggles, the H/P/R offset
        // rows, the hover panel, ...) and is run through WireSection rather than called directly - see
        // WireSection's own comment for why. Order matters for a few dependencies: WireTabs must run
        // before WireHoverControlsPanel (it resolves _hoverControlsContainer), and the toggle-wiring
        // sections must run before WireModeToggleRegistrations (it reads the fields they assign).
        WireSection(nameof(WireWindowChrome), WireWindowChrome);
        WireSection(nameof(WireGlobalModeToggles), WireGlobalModeToggles);
        WireSection(nameof(WireTabs), WireTabs);
        WireSection(nameof(WireOrbitToggles), WireOrbitToggles);
        WireSection(nameof(WireSurfaceToggles), WireSurfaceToggles);
        WireSection(nameof(WireTargetToggles), WireTargetToggles);
        WireSection(nameof(WireStarToggles), WireStarToggles);
        WireSection(nameof(WireHoldHoverToggles), WireHoldHoverToggles);
        WireSection(nameof(WireModeToggleRegistrations), WireModeToggleRegistrations);
        WireSection(nameof(WireOffsetRows), WireOffsetRows);
        WireSection(nameof(WireHoverControlsPanel), WireHoverControlsPanel);
        WireSection(nameof(WireInitialPanelState), WireInitialPanelState);
        WireSection(nameof(WireFlightAxesToggles), WireFlightAxesToggles);
        WireSection(nameof(WireLandingPredictionToggle), WireLandingPredictionToggle);
        WireSection(nameof(WireSettingsButton), WireSettingsButton);
        WireSection(nameof(WireCloseButton), WireCloseButton);
    }

    // Runs one logical piece of OnEnable's wiring in isolation. Require<T> below logs a specific error
    // when a UXML element is missing but still returns null - the very next line that dereferences it
    // (e.g. "_offToggle.SetEnabled(true)") then throws a plain NRE, same as the un-guarded _root.Q<>()
    // calls this replaced always would have. What changes is the blast radius: previously that
    // exception unwound all the way out of OnEnable, silently skipping every registration written
    // after it in source order (see the ~63-unguarded-Q<>-calls finding in
    // code_review_2026-07-17.md #3) - a single renamed UXML element could leave the whole window
    // half-wired. Catching per-section here means one missing/renamed element only takes out its own
    // section's wiring; every other section still runs.
    private void WireSection(string sectionName, Action wire)
    {
        try
        {
            wire();
        }
        catch (Exception ex)
        {
            _LOGGER.LogError($"Failed to wire '{sectionName}' - related controls may be missing or non-functional. {ex}");
        }
    }

    // Looks up a named UXML element under _root and logs a specific, actionable error if it's
    // missing, rather than leaving the caller to dereference a bare Q<T>() null with a generic NRE.
    // Still returns null on a miss - see WireSection for how the resulting exception is contained.
    private T Require<T>(string name) where T : VisualElement => Require<T>(_root, name);

    private T Require<T>(VisualElement container, string name) where T : VisualElement
    {
        var element = container.Q<T>(name);
        if (element == null)
            _LOGGER.LogError($"Expected UI element '{name}' ({typeof(T).Name}) not found in UXML.");
        return element;
    }

    private void WireWindowChrome()
    {
        var savedX = Settings.WindowPositionX.Value;
        var savedY = Settings.WindowPositionY.Value;
        if (savedX >= 0f && savedY >= 0f)
            _root.SetDefaultPosition(_ => new Vector2(savedX, savedY));
        else
            _root.CenterByDefault();

        _statusLabel = Require<Label>("status");
        _statusLabel.text = string.Empty;

        // Persist the position once a drag finishes (dragging is the only way it ever changes).
        _root.RegisterCallback<PointerUpEvent>(evt => SaveWindowPosition());
    }

    private void WireGlobalModeToggles()
    {
        _offToggle = Require<SideToggleControl>("off");
        _offToggle.SetEnabled(true);
        _offToggle.SwitchToggleState(false, false);
        _killrotToggle = Require<SideToggleControl>("killrot");
        _killrotToggle.SetEnabled(true);
        _killrotToggle.SwitchToggleState(false, false);
        _nodeToggle = Require<SideToggleControl>("node");
        _nodeToggle.SetEnabled(true);
        _nodeToggle.SwitchToggleState(false, false);
    }

    private void WireTabs()
    {
        _orbitTabToggle = Require<TabToggleControl>("orb-tab");
        _orbitTabToggle.RegisterCallback<ClickEvent>(OnOrbitTabClicked);
        _orbitContainer = Require<VisualElement>("orb-container");
        _surfaceTabToggle = Require<TabToggleControl>("surf-tab");
        _surfaceTabToggle.RegisterCallback<ClickEvent>(OnSurfaceTabClicked);
        _surfaceContainer = Require<VisualElement>("surf-container");
        _targetTabToggle = Require<TabToggleControl>("tgt-tab");
        _targetTabToggle.RegisterCallback<ClickEvent>(OnTargetTabClicked);
        _targetContainer = Require<VisualElement>("tgt-container");
        _specialTabToggle = Require<TabToggleControl>("spec-tab");
        _specialTabToggle.RegisterCallback<ClickEvent>(OnSpecialTabClicked);
        _specialContainer = Require<VisualElement>("spec-container");

        _attitudeControlsContainer = Require<VisualElement>("attitude-controls");
        _hoverControlsContainer = Require<VisualElement>("hover-controls");
    }

    private void WireOrbitToggles()
    {
        _progradeToggle = Require<SideToggleControl>("prograde");
        _normalToggle = Require<SideToggleControl>("normal");
        _radialInToggle = Require<SideToggleControl>("radialin");
        _retrogradeToggle = Require<SideToggleControl>("retrograde");
        _antinormalToggle = Require<SideToggleControl>("antinormal");
        _radialOutToggle = Require<SideToggleControl>("radialout");

        // Each ORB button's LED color family is intrinsic to that button (prograde is always the
        // prograde family, etc.) - assign it once here rather than on every click. The family class
        // stays on the LED permanently; SASExtended.uss only paints it while the LED is also checked
        // (see .side-toggle__led--checked.<family>-background), so unchecked/disabled buttons still
        // show the normal gray.
        _progradeToggle.SetSasColorMode(SasColorMode.Prograde);
        _retrogradeToggle.SetSasColorMode(SasColorMode.Prograde);
        _normalToggle.SetSasColorMode(SasColorMode.Normal);
        _antinormalToggle.SetSasColorMode(SasColorMode.Normal);
        _radialInToggle.SetSasColorMode(SasColorMode.Radial);
        _radialOutToggle.SetSasColorMode(SasColorMode.Radial);
    }

    private void WireSurfaceToggles()
    {
        _svelPlusToggle = Require<SideToggleControl>("svelplus");
        _svelMinusToggle = Require<SideToggleControl>("svelminus");
        _surfToggle = Require<SideToggleControl>("surf");
        _hvelPlusToggle = Require<SideToggleControl>("hvelplus");
        _hvelMinusToggle = Require<SideToggleControl>("hvelminus");
        _upToggle = Require<SideToggleControl>("up");
    }

    private void WireTargetToggles()
    {
        _targetPlusToggle = Require<SideToggleControl>("tgtplus");
        _relativeVelocityPlusToggle = Require<SideToggleControl>("rvelplus");
        _parPlusToggle = Require<SideToggleControl>("parplus");
        _targetMinusToggle = Require<SideToggleControl>("tgtminus");
        _relativeVelocityMinusToggle = Require<SideToggleControl>("rvelminus");
        _parMinusToggle = Require<SideToggleControl>("parminus");
    }

    private void WireStarToggles()
    {
        _starPlusToggle = Require<SideToggleControl>("starplus");
        _starMinusToggle = Require<SideToggleControl>("starminus");
    }

    private void WireHoldHoverToggles()
    {
        _holdToggle = Require<SideToggleControl>("hold");
        _hoverToggle = Require<SideToggleControl>("hover");
    }

    private void WireModeToggleRegistrations()
    {
        // Every mutually-exclusive mode toggle (global modes + all direction buttons across all tabs),
        // used by ClearAllModeToggles/RegisterModeButton so only one is ever shown toggled on. Nulls
        // (a toggle whose UXML element was missing - already logged by Require<T> in whichever
        // Wire*Toggles section assigned it) are filtered out rather than left in the array: they can't
        // meaningfully participate in mutual exclusion, and ClearAllModeToggles would otherwise NRE on
        // them every single mode switch.
        _allModeToggles = new[]
        {
            _offToggle, _killrotToggle, _nodeToggle,
            _progradeToggle, _normalToggle, _radialInToggle, _retrogradeToggle, _antinormalToggle, _radialOutToggle,
            _svelPlusToggle, _svelMinusToggle, _surfToggle, _hvelPlusToggle, _hvelMinusToggle, _upToggle,
            _targetPlusToggle, _relativeVelocityPlusToggle, _parPlusToggle, _targetMinusToggle, _relativeVelocityMinusToggle, _parMinusToggle,
            _starPlusToggle, _starMinusToggle,
            _holdToggle, _hoverToggle
        }.Where(toggle => toggle != null).ToArray();

        RegisterModeButton(_offToggle, () => SASManager.Instance.SetSASOff());
        RegisterModeButton(_killrotToggle, () => SASManager.Instance.SetSASKillrot());
        RegisterModeButton(_nodeToggle, () => SASManager.Instance.SetSASManeuver());

        RegisterModeButton(_progradeToggle, () => SASManager.Instance.SetOrbitPrograde());
        RegisterModeButton(_normalToggle, () => SASManager.Instance.SetOrbitNormal());
        // The "RAD +" button is named "radialin" in the (verbatim-ported) UXML, but per the MechJeb
        // SmartASS convention (Radial+ = Vector3d.up = radially outward), "+" means away from the
        // body - so this button must invoke SetOrbitRadialOut(), not RadialIn(). Same swap applies to
        // "radialout" below.
        RegisterModeButton(_radialInToggle, () => SASManager.Instance.SetOrbitRadialOut());
        RegisterModeButton(_retrogradeToggle, () => SASManager.Instance.SetOrbitRetrograde());
        RegisterModeButton(_antinormalToggle, () => SASManager.Instance.SetOrbitAntiNormal());
        RegisterModeButton(_radialOutToggle, () => SASManager.Instance.SetOrbitRadialIn());

        RegisterModeButton(_svelPlusToggle, () => SASManager.Instance.SetSurfaceSvelPlus());
        RegisterModeButton(_svelMinusToggle, () => SASManager.Instance.SetSurfaceSvelMinus());
        RegisterModeButton(_surfToggle, () => SASManager.Instance.SetSurfaceSurf());
        RegisterModeButton(_hvelPlusToggle, () => SASManager.Instance.SetSurfaceHvelPlus());
        RegisterModeButton(_hvelMinusToggle, () => SASManager.Instance.SetSurfaceHvelMinus());
        RegisterModeButton(_upToggle, () => SASManager.Instance.SetSurfaceUp());

        RegisterModeButton(_targetPlusToggle, () => SASManager.Instance.SetTargetPlus());
        RegisterModeButton(_relativeVelocityPlusToggle, () => SASManager.Instance.SetTargetRvelPlus());
        RegisterModeButton(_parPlusToggle, () => SASManager.Instance.SetTargetParPlus());
        RegisterModeButton(_targetMinusToggle, () => SASManager.Instance.SetTargetMinus());
        RegisterModeButton(_relativeVelocityMinusToggle, () => SASManager.Instance.SetTargetRvelMinus());
        RegisterModeButton(_parMinusToggle, () => SASManager.Instance.SetTargetParMinus());

        RegisterModeButton(_starPlusToggle, () => SASManager.Instance.SetSpecialStarPlus());
        RegisterModeButton(_starMinusToggle, () => SASManager.Instance.SetSpecialStarMinus());

        RegisterModeButton(_holdToggle, () => SASManager.Instance.SetHold());
        RegisterModeButton(_hoverToggle, () => SASManager.Instance.SetHover());
    }

    private void WireOffsetRows()
    {
        _xToggle = Require<SideToggleControl>("x-toggle");
        _xToggle.RegisterCallback<ClickEvent>(OnXToggleClicked);
        _xValue = Require<FloatField>("x-value");
        _xValue.RegisterValueChangedCallback(OnXChanged);
        _yToggle = Require<SideToggleControl>("y-toggle");
        _yToggle.RegisterCallback<ClickEvent>(OnYToggleClicked);
        _yValue = Require<FloatField>("y-value");
        _yValue.RegisterValueChangedCallback(OnYChanged);
        _zToggle = Require<SideToggleControl>("z-toggle");
        _zToggle.RegisterCallback<ClickEvent>(OnZToggleClicked);
        _zValue = Require<FloatField>("z-value");
        _zValue.RegisterValueChangedCallback(OnZChanged);

        _xMinus = Require<Button>("x-minus");
        _xMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value--;
        });
        _xPlus = Require<Button>("x-plus");
        _xPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value++;
        });
        _xFirst = Require<Button>("x-first");
        _xFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value = 0;
        });
        _xSecond = Require<Button>("x-second");
        _xSecond.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value = 90;
        });

        _yMinus = Require<Button>("y-minus");
        _yMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value--;
        });
        _yPlus = Require<Button>("y-plus");
        _yPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value++;
        });
        _yFirst = Require<Button>("y-first");
        _yFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value = 0;
        });
        _ySecond = Require<Button>("y-second");
        _ySecond.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value = 90;
        });

        _zMinus = Require<Button>("z-minus");
        _zMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value--;
        });
        _zPlus = Require<Button>("z-plus");
        _zPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value++;
        });
        _zFirst = Require<Button>("z-first");
        _zFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value = 0;
        });
        _zSecond = Require<Button>("z-second");
        _zSecond.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value = Mathf.Round((float)SASManager.Instance.CurrentRollOffset);
        });
    }

    private void WireHoverControlsPanel()
    {
        // Purely a label ("VER VEL" is always active in hover, unlike Heading/Pitch/Roll there's no
        // per-axis enable/disable concept for it) - keep it disabled so it can't be toggled off.
        _hoverVerticalVelocityToggle = Require<SideToggleControl>(_hoverControlsContainer, "ver-vel-toggle");
        _hoverVerticalVelocityToggle.SetEnabled(false);

        _hoverVerticalVelocityValue = Require<FloatField>(_hoverControlsContainer, "ver-vel-value");
        // Set before registering the callback below, so displaying the remembered value on window
        // creation doesn't immediately re-trigger a (harmless but pointless) save.
        _hoverVerticalVelocityValue.value = Settings.HoverVerticalVelocity.Value;
        _hoverVerticalVelocityValue.RegisterValueChangedCallback(evt =>
        {
            SASManager.Instance.HoverTargetVerticalSpeed = evt.newValue;
            Settings.HoverVerticalVelocity.Value = evt.newValue;
        });

        _hoverVerticalVelocityMinus = Require<Button>(_hoverControlsContainer, "ver-vel-minus");
        _hoverVerticalVelocityMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value--;
        });
        _hoverVerticalVelocityPlus = Require<Button>(_hoverControlsContainer, "ver-vel-plus");
        _hoverVerticalVelocityPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value++;
        });
        _hoverVerticalVelocityZero = Require<Button>(_hoverControlsContainer, "ver-vel-zero");
        _hoverVerticalVelocityZero.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value = 0;
        });

        _cancelHorizontalVelocityToggle = Require<SideToggleControl>(_hoverControlsContainer, "cancel-horizontal-velocity");
        _cancelHorizontalVelocityToggle.SetEnabled(true);
        _cancelHorizontalVelocityToggle.SwitchToggleState(true, false);
        _cancelHorizontalVelocityToggle.RegisterCallback<ClickEvent>(evt =>
        {
            SASManager.Instance.CancelHorizontalVelocity = _cancelHorizontalVelocityToggle.IsToggled;
        });

        _hoverZToggle = Require<SideToggleControl>(_hoverControlsContainer, "hover-z-toggle");
        _hoverZToggle.RegisterCallback<ClickEvent>(OnHoverZToggleClicked);
        _hoverZValue = Require<FloatField>(_hoverControlsContainer, "hover-z-value");
        _hoverZValue.RegisterValueChangedCallback(OnHoverZChanged);

        _hoverZMinus = Require<Button>(_hoverControlsContainer, "hover-z-minus");
        _hoverZMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverZValue.value--;
        });
        _hoverZPlus = Require<Button>(_hoverControlsContainer, "hover-z-plus");
        _hoverZPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverZValue.value++;
        });
        _hoverZFirst = Require<Button>(_hoverControlsContainer, "hover-z-first");
        _hoverZFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverZValue.value = 0;
        });
        _hoverZSecond = Require<Button>(_hoverControlsContainer, "hover-z-second");
        _hoverZSecond.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverZValue.value = Mathf.Round((float)SASManager.Instance.CurrentRollOffset);
        });
    }

    private void WireInitialPanelState()
    {
        // Match the containers' initial visibility/colors to the starting (non-hover, no-mode) state.
        UpdatePanelForMode();
        UpdateAttitudeColors();
        // SASManager.Instance is still null this early (see UpdatePanelForMode's comment) so this
        // starts NODE/TGT-mode buttons greyed out; the first Update() tick corrects them once real
        // HasManeuverNode/HasTarget values are available.
        UpdateNodeTargetAvailability();
    }

    private void WireCloseButton()
    {
        // Uses RegisterCallback<ClickEvent> rather than the `.clicked` action, matching every other
        // clickable element in this file (x/y/z-minus/plus/first, tab toggles, etc.) rather than
        // mixing two different click APIs.
        var closeButton = Require<Button>("close-button");
        closeButton.RegisterCallback<ClickEvent>(evt =>
        {
            _LOGGER.LogInfo("Close button clicked.");
            IsWindowOpen = false;
        });
    }

    private void SaveWindowPosition()
    {
        Settings.WindowPositionX.Value = _root.resolvedStyle.left;
        Settings.WindowPositionY.Value = _root.resolvedStyle.top;
        SASExtendedPlugin.Instance.SWConfiguration.Save();
    }

    // Gated on IsWindowOpen and the StatusLoggingEnabled config toggle, so the status readout does no
    // work at all while the window is closed or the feature is disabled. Both settings are read live
    // (not cached) so toggling either in the in-game Settings -> Mods menu takes effect immediately.
    private void Update()
    {
        // Deferred to here (rather than OnEnable) because SASManager.Instance is still null the first
        // time OnEnable runs - see the comment on UpdatePanelForMode. Runs unconditionally (ahead of
        // the IsWindowOpen gate below) so the subscription still happens even while the window starts
        // out closed. Only ever fires once - style.display toggling doesn't re-run OnEnable/Update's
        // subscription guard.
        if (!_subscribedToSasManager && SASManager.Instance != null)
        {
            SASManager.Instance.Disengaged += OnSasManagerDisengaged;
            _subscribedToSasManager = true;
        }

        if (!IsWindowOpen)
            return;

        // Auto-disengage on node/target loss is handled by SASManager itself (independent of
        // whether this window is open) - this just greys out the buttons, so it only needs to run
        // while the window is actually visible.
        UpdateNodeTargetAvailability();

        if (!Settings.StatusLoggingEnabled.Value)
            return;

        if (Time.time - _lastStatusUpdateTime < Settings.StatusRefreshInterval.Value)
            return;
        _lastStatusUpdateTime = Time.time;

        UpdateStatusLabel();
    }

    // NODE has no maneuver node to point at, and the six TGT-tab direction modes have no target,
    // when HasManeuverNode/HasTarget is false - grey those buttons
    // out rather than leaving them clickable with nothing to do. The TGT tab toggle itself is left
    // alone so the tab stays browsable even with no target selected. Auto-switching back to OFF when
    // the reference disappears mid-engage is handled in SASManager.Update (DisengageForLostReference).
    private void UpdateNodeTargetAvailability()
    {
        var sas = SASManager.Instance;
        bool hasManeuver = sas != null && sas.HasManeuverNode;
        bool hasTarget = sas != null && sas.HasTarget;

        SetToggleAvailability(_nodeToggle, hasManeuver);

        SetToggleAvailability(_targetPlusToggle, hasTarget);
        SetToggleAvailability(_relativeVelocityPlusToggle, hasTarget);
        SetToggleAvailability(_parPlusToggle, hasTarget);
        SetToggleAvailability(_targetMinusToggle, hasTarget);
        SetToggleAvailability(_relativeVelocityMinusToggle, hasTarget);
        SetToggleAvailability(_parMinusToggle, hasTarget);
    }

    // SideToggleControl.SetEnabled() unconditionally forces the toggle off as a side effect of
    // re-applying its disabled/unchecked visuals - calling it every frame regardless of a real change
    // would fight the toggle state RegisterModeButton/OnSasManagerDisengaged just set elsewhere. Only
    // call it on an actual availability change.
    private static void SetToggleAvailability(SideToggleControl toggle, bool available)
    {
        if (toggle.IsEnabled != available)
            toggle.SetEnabled(available);
    }

    // SASManager disengaged itself (vessel switch/undock/revert/scene-exit - see
    // SASManager.DisengageForVesselChange) rather than the player clicking a toggle - mirror what a
    // manual OFF click does (RegisterModeButton's else branch) so the window doesn't keep showing a
    // mode/offsets that are no longer actually engaged. Unlike that manual path, we don't know which
    // toggle was previously active, so ClearAllModeToggles runs unconditionally rather than relying on
    // every other toggle already being off.
    private void OnSasManagerDisengaged()
    {
        ClearAllModeToggles(_offToggle);
        _offToggle.SwitchToggleState(true, false);
        _xValue.value = 0;
        _yValue.value = 0;
        _zValue.value = 0;
        UpdateAttitudeColors();
    }

    // Status content depends on the active mode: KillRot and Hover
    // don't point anywhere, so an angle-to-target isn't meaningful for either and they get their own
    // bespoke readouts; every other mode (including Node/TGT PAR, even while their fallback holds
    // current attitude - see SASManager.SetRotation) shares the generic angle-to-target + free-axis
    // readout, since they all go through the same BuildPointingRotation/BuildTargetOrientationRotation
    // pipeline.
    private void UpdateStatusLabel()
    {
        var sas = SASManager.Instance;
        if (sas == null || sas.AttitudeMode == AttitudeMode.None)
        {
            _statusLabel.text = string.Empty;
            return;
        }

        switch (sas.AttitudeMode)
        {
            case AttitudeMode.KillRot:
                _statusLabel.text = $"ω: {sas.GetAngularVelocityDegPerSec():F1}°/s";
                break;

            case AttitudeMode.Hover:
                _statusLabel.text = $"V-SPD: {FormatSpeed(sas.GetHoverVerticalSpeed())}  H-SPD: {FormatSpeed(sas.GetHoverHorizontalSpeed())}";
                break;

            default:
                var freeAxes = "";
                if (!sas.XEnabled) freeAxes += "H";
                if (!sas.YEnabled) freeAxes += "P";
                if (!sas.ZEnabled) freeAxes += "R";
                _statusLabel.text = freeAxes.Length > 0
                    ? $"Angle to target: {sas.GetAngleToRotation():F1}°  Free: {freeAxes}"
                    : $"Angle to target: {sas.GetAngleToRotation():F1}°";
                break;
        }
    }

    // Speed readouts switch precision/unit by magnitude so the number stays readable at both hover
    // (single digits) and orbital (multi-km/s) scales: <100 m/s keeps one decimal, [100,1000) m/s drops
    // to whole numbers, >=1000 m/s switches to km/s. Sign is preserved as-is (only the magnitude drives
    // which bucket is picked), so negative speeds format the same way as positive ones.
    private static string FormatSpeed(double speedMs)
    {
        var abs = Math.Abs(speedMs);
        if (abs >= 1000.0)
            return $"{speedMs / 1000.0:F1} km/s";
        if (abs >= 100.0)
            return $"{speedMs:F0} m/s";
        return $"{speedMs:F1} m/s";
    }

    #region Mode buttons

    // Wires a mode toggle so clicking it engages its mode and clears every other mode toggle, or -
    // if clicking it just turned it off (it was the active mode) - falls back to OFF. Shared by every
    // global-mode and direction button across all four tabs so each button is a one-line registration
    // instead of a ~15-line copy-pasted handler.
    private void RegisterModeButton(SideToggleControl toggle, Action setMode)
    {
        // toggle is null when its UXML element failed to resolve - Require<T> already logged that in
        // whichever Wire*Toggles section assigned it (see WireModeToggleRegistrations). Nothing to wire.
        if (toggle == null)
            return;

        toggle.RegisterCallback<ClickEvent>(evt =>
        {
            // SideToggleControl's own ClickEvent handler already no-ops on a disabled toggle, but it
            // doesn't stop the event from propagating - without this guard a click on e.g. a greyed-out
            // NODE/TGT button (see UpdateNodeTargetAvailability) falls through to the "was off, engage
            // it" vs. "was on, turn off" branching below. A disabled toggle is always untoggled, so it
            // took the turn-off branch: SetSASOff() actually disengaged, while the real active toggle
            // was never cleared, leaving it visually checked with SAS Extended silently off.
            if (!toggle.IsEnabled)
                return;

            if (toggle.IsToggled)
            {
                ClearAllModeToggles(toggle);
                setMode();
                ApplyStoredOffsetsForCurrentMode();

                // OFF and KillRot have no control panel of their own (KillRot doesn't expose
                // Heading/Pitch/Roll offsets - it just holds current attitude) - only a mode with
                // real attitude controls should switch which panel (attitude vs. hover) is showing.
                // Disengaging (the else branch below), OFF, and KillRot all leave whichever panel was
                // already visible in place.
                if (toggle != _offToggle && toggle != _killrotToggle)
                    UpdatePanelForMode();
            }
            else
            {
                _offToggle.SwitchToggleState(true, false);
                SASManager.Instance.SetSASOff();
            }

            UpdateAttitudeColors();
        });
    }

    // Loads the just-engaged mode's remembered Heading/Pitch/Roll (Settings.AttitudeOffsets) directly
    // into SASManager.X/Y/Z, then pushes the same numbers into the FloatFields for display. Modes
    // without a stored entry (OFF, KillRot - see Settings.AttitudeOffsets) leave everything untouched,
    // since neither uses the generic offset mechanism at all. Hover only cares about Roll
    // (Heading/Pitch go unused) and has its own Roll widget (_hoverZValue) alongside the shared one.
    //
    // Deliberately does NOT set SASManager.X/Y/Z by assigning _xValue.value/etc. and relying on
    // OnXChanged/OnYChanged/OnZChanged to cascade the write, the way this used to work: Unity's
    // FloatField only raises its ChangeEvent when the new value differs from whatever it last
    // displayed, so that approach silently no-ops whenever a mode's remembered angle happens to
    // equal the previous mode's - leaving SASManager.Z stuck on a stale value from the mode just
    // left. This bit Roll specifically once it gained a second widget (_hoverZValue, for Hover's
    // own Roll row): editing Roll while in Hover updates _hoverZValue + Z, but _zValue's own cached
    // "last displayed" value doesn't move, so switching to a normal mode whose stored Roll happened
    // to coincide with THAT stale cache would silently fail to restore the real value into Z.
    // Writing SASManager.X/Y/Z here directly, then pushing to the fields via SetValueWithoutNotify
    // (so this doesn't also trigger OnXChanged/OnYChanged/OnZChanged and re-store the same value a
    // second time), sidesteps the whole class of bug.
    private void ApplyStoredOffsetsForCurrentMode()
    {
        if (!Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            return;

        SASManager.Instance.X = offsets.Heading.Value;
        SASManager.Instance.Y = offsets.Pitch.Value;
        SASManager.Instance.Z = offsets.Roll.Value;

        _xValue.SetValueWithoutNotify(offsets.Heading.Value);
        _yValue.SetValueWithoutNotify(offsets.Pitch.Value);
        _zValue.SetValueWithoutNotify(offsets.Roll.Value);
        _hoverZValue.SetValueWithoutNotify(offsets.Roll.Value);
    }

    private void ClearAllModeToggles(SideToggleControl except)
    {
        foreach (var toggle in _allModeToggles)
        {
            if (toggle != except)
                toggle.SwitchToggleState(false, false);
        }
    }

    // Hover is the only mode with its own control panel (vertical-speed target, horizontal-velocity
    // cancel toggle, and its own Roll row) in place of the shared Heading/Pitch/Roll attitude-controls
    // panel - swap which one is visible. Only called when a real mode is being engaged (see
    // RegisterModeButton) - turning a mode off (falling back to OFF) intentionally leaves the
    // previously-visible panel alone.
    private void UpdatePanelForMode()
    {
        // SASManager.Instance is still null the very first time this runs: SceneController.Initialize
        // creates this window (and synchronously runs OnEnable, which calls this) before
        // SASExtendedPlugin creates the SASManager GameObject, and SASManager only assigns Instance in
        // Start() (deferred to the next Unity lifecycle pass) even if that order were swapped. Treat
        // "not ready yet" as non-hover rather than crashing OnEnable partway through (which used to
        // silently skip every registration after this call, including the close button).
        bool isHover = SASManager.Instance != null && SASManager.Instance.IsHoverActive;
        _hoverControlsContainer.style.display = isHover ? DisplayStyle.Flex : DisplayStyle.None;
        _attitudeControlsContainer.style.display = isHover ? DisplayStyle.None : DisplayStyle.Flex;

        // Hover's Roll row and the shared attitude panel's Roll row each drive their OWN enabled flag
        // (HoverRollEnabled vs. the shared ZEnabled - see HoverRollEnabled's field comment for why).
        // Only one row is ever visible/interactive at a time, so keep whichever one is about to
        // become visible in sync with its backing flag (SwitchToggleState(..., false) so this
        // doesn't re-fire the click handler and write the value right back to itself). The target
        // angle itself doesn't need syncing here - ApplyStoredOffsetsForCurrentMode (called right
        // before this) already pushed it into both Roll widgets directly.
        if (SASManager.Instance != null)
        {
            var rollToggle = isHover ? _hoverZToggle : _zToggle;
            rollToggle.SwitchToggleState(isHover ? SASManager.Instance.HoverRollEnabled : SASManager.Instance.ZEnabled, false);
        }
    }

    // Heading/Pitch/Roll share the color of whichever ORB family is currently active, so they read as
    // "offsets from that direction" rather than a fixed neutral color. Modes outside the ORB tab (and
    // no mode at all) have no family, so the toggles fall back to plain gray. Unlike UpdatePanelForMode,
    // this runs on every mode change (including OFF) since it doesn't affect which panel is visible.
    private void UpdateAttitudeColors()
    {
        // See UpdatePanelForMode for why SASManager.Instance can still be null here.
        SasColorMode attitudeColorMode;
        switch (SASManager.Instance == null ? AttitudeMode.None : SASManager.Instance.AttitudeMode)
        {
            case AttitudeMode.OrbitPrograde:
            case AttitudeMode.OrbitRetrograde:
                attitudeColorMode = SasColorMode.Prograde;
                break;
            case AttitudeMode.OrbitNormal:
            case AttitudeMode.OrbitAntiNormal:
                attitudeColorMode = SasColorMode.Normal;
                break;
            case AttitudeMode.OrbitRadialIn:
            case AttitudeMode.OrbitRadialOut:
                attitudeColorMode = SasColorMode.Radial;
                break;
            default:
                attitudeColorMode = SasColorMode.None;
                break;
        }

        _xToggle.SetSasColorMode(attitudeColorMode);
        _yToggle.SetSasColorMode(attitudeColorMode);
        _zToggle.SetSasColorMode(attitudeColorMode);
    }

    #endregion


    #region Tabs

    private void OnOrbitTabClicked(ClickEvent evt)
    {
        _orbitContainer.style.display = DisplayStyle.Flex;
        _surfaceContainer.style.display = DisplayStyle.None;
        _targetContainer.style.display = DisplayStyle.None;
        _specialContainer.style.display = DisplayStyle.None;
        _orbitTabToggle.SwitchToggleState(true, false);
        _surfaceTabToggle.SwitchToggleState(false, false);
        _targetTabToggle.SwitchToggleState(false, false);
        _specialTabToggle.SwitchToggleState(false, false);
    }

    private void OnSurfaceTabClicked(ClickEvent evt)
    {
        _orbitContainer.style.display = DisplayStyle.None;
        _surfaceContainer.style.display = DisplayStyle.Flex;
        _targetContainer.style.display = DisplayStyle.None;
        _specialContainer.style.display = DisplayStyle.None;
        _orbitTabToggle.SwitchToggleState(false, false);
        _surfaceTabToggle.SwitchToggleState(true, false);
        _targetTabToggle.SwitchToggleState(false, false);
        _specialTabToggle.SwitchToggleState(false, false);
    }

    private void OnTargetTabClicked(ClickEvent evt)
    {
        _orbitContainer.style.display = DisplayStyle.None;
        _surfaceContainer.style.display = DisplayStyle.None;
        _targetContainer.style.display = DisplayStyle.Flex;
        _specialContainer.style.display = DisplayStyle.None;
        _orbitTabToggle.SwitchToggleState(false, false);
        _surfaceTabToggle.SwitchToggleState(false, false);
        _targetTabToggle.SwitchToggleState(true, false);
        _specialTabToggle.SwitchToggleState(false, false);
    }

    private void OnSpecialTabClicked(ClickEvent evt)
    {
        _orbitContainer.style.display = DisplayStyle.None;
        _surfaceContainer.style.display = DisplayStyle.None;
        _targetContainer.style.display = DisplayStyle.None;
        _specialContainer.style.display = DisplayStyle.Flex;
        _orbitTabToggle.SwitchToggleState(false, false);
        _surfaceTabToggle.SwitchToggleState(false, false);
        _targetTabToggle.SwitchToggleState(false, false);
        _specialTabToggle.SwitchToggleState(true, false);
    }

    #endregion

    // Wires every Flight Axes visual toggle by iterating FlightAxesVisualizer.ToggleIds: the toggle's
    // element name IS its visual id, so each click just pushes (id, IsToggled) to the visualizer. Each
    // element is null-guarded so a not-yet-authored toggle doesn't break the rest of OnEnable. These are
    // plain independent on/off toggles, NOT part of the mutually-exclusive mode set (_allModeToggles),
    // so they're wired separately (and never touched by ClearAllModeToggles/OnSasManagerDisengaged).
    private void WireFlightAxesToggles()
    {
        foreach (var id in FlightAxesVisualizer.ToggleIds)
        {
            var toggle = _root.Q<SideToggleControl>(id);
            if (toggle == null)
            {
                _LOGGER.LogWarning($"Flight Axes toggle '{id}' not found in UXML - skipping wiring.");
                continue;
            }

            toggle.SetEnabled(true);
            toggle.SwitchToggleState(false, false);
            var visualId = id; // capture per-iteration for the closure
            toggle.RegisterCallback<ClickEvent>(evt =>
            {
                // SideToggleControl flips IsToggled in its own click handler (registered first, so it
                // has already run here); a disabled toggle no-ops that flip, so guard to avoid
                // re-applying a stale state.
                if (!toggle.IsEnabled)
                    return;
                FlightAxesVisualizer.Instance?.SetVisual(visualId, toggle.IsToggled);
            });
        }
    }

    // Wires the "Show landing predictions" toggle to LandingPredictionManager. Independent feature
    // toggle (like the Flight Axes visuals above), NOT part of the mutually-exclusive mode set -
    // never touched by ClearAllModeToggles/OnSasManagerDisengaged. Null-guarded so a not-yet-authored
    // UXML element doesn't break the rest of OnEnable.
    private void WireLandingPredictionToggle()
    {
        var toggle = _root.Q<SideToggleControl>(LandingPredictionManager.ToggleId);
        if (toggle == null)
        {
            _LOGGER.LogWarning($"Landing prediction toggle '{LandingPredictionManager.ToggleId}' not found in UXML - skipping wiring.");
            return;
        }

        toggle.SetEnabled(true);
        toggle.SwitchToggleState(Settings.ShowLandingPrediction.Value, false);
        toggle.RegisterCallback<ClickEvent>(evt =>
        {
            if (!toggle.IsEnabled)
                return;
            bool on = toggle.IsToggled;
            Settings.ShowLandingPrediction.Value = on;
            SASExtendedPlugin.Instance.SWConfiguration.Save();
            LandingPredictionManager.Instance?.SetEnabled(on);
        });
    }

    // Wires the header settings button. Clicking it swaps the window between the main view (upper +
    // middle containers) and the settings container that holds the Flight Axes toggles. Every element
    // is null-guarded so a not-yet-authored piece doesn't break the rest of OnEnable; the button starts
    // in the main (settings-closed) view.
    private void WireSettingsButton()
    {
        _settingsButton = _root.Q<Button>("settings-button");
        _settingsButtonBackground = _root.Q<VisualElement>("settings-button__background");
        _settingsContainer = _root.Q<VisualElement>("settings-container");
        _upperContainer = _root.Q<VisualElement>("upper-container");
        _middleContainer = _root.Q<VisualElement>("middle-container");
        _footer = _root.Q<VisualElement>("footer");

        if (_settingsButton == null)
        {
            _LOGGER.LogWarning("Settings button 'settings-button' not found in UXML - skipping wiring.");
            return;
        }

        _settingsOpen = false;
        ApplySettingsView();
        _settingsButton.RegisterCallback<ClickEvent>(evt =>
        {
            _settingsOpen = !_settingsOpen;
            ApplySettingsView();
        });
    }

    // Reflects _settingsOpen onto the header button's checked class and the main/settings panels. Each
    // element is null-guarded independently so a partially-authored UXML degrades gracefully.
    private void ApplySettingsView()
    {
        if (_settingsButtonBackground != null)
        {
            if (_settingsOpen)
                _settingsButtonBackground.AddToClassList(SettingsButtonCheckedClass);
            else
                _settingsButtonBackground.RemoveFromClassList(SettingsButtonCheckedClass);
        }

        if (_upperContainer != null)
            _upperContainer.style.display = _settingsOpen ? DisplayStyle.None : DisplayStyle.Flex;
        if (_middleContainer != null)
            _middleContainer.style.display = _settingsOpen ? DisplayStyle.None : DisplayStyle.Flex;
        if (_footer != null)
            _footer.style.display = _settingsOpen ? DisplayStyle.None : DisplayStyle.Flex;
        if (_settingsContainer != null)
            _settingsContainer.style.display = _settingsOpen ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void OnXToggleClicked(ClickEvent evt)
    {
        if (_xToggle.IsToggled)
        {
            SASManager.Instance.XEnabled = true;
        }
        else
        {
            SASManager.Instance.XEnabled = false;
        }
    }

    private void OnYToggleClicked(ClickEvent evt)
    {
        if (_yToggle.IsToggled)
        {
            SASManager.Instance.YEnabled = true;
        }
        else
        {
            SASManager.Instance.YEnabled = false;
        }
    }

    private void OnZToggleClicked(ClickEvent evt)
    {
        if (_zToggle.IsToggled)
        {
            SASManager.Instance.ZEnabled = true;
        }
        else
        {
            SASManager.Instance.ZEnabled = false;
        }
    }

    private void OnXChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.X = evt.newValue;
        if (Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            offsets.Heading.Value = evt.newValue;
    }

    private void OnYChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.Y = evt.newValue;
        if (Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            offsets.Pitch.Value = evt.newValue;
    }

    private void OnZChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.Z = evt.newValue;
        if (Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            offsets.Roll.Value = evt.newValue;
    }

    // Hover's own Roll row - see the field comment on _hoverZToggle. Drives its own
    // SASManager.HoverRollEnabled (NOT the shared ZEnabled) plus the shared Z/
    // Settings.AttitudeOffsets[Hover].Roll, same as OnZToggleClicked/OnZChanged.
    private void OnHoverZToggleClicked(ClickEvent evt)
    {
        SASManager.Instance.HoverRollEnabled = _hoverZToggle.IsToggled;
    }

    private void OnHoverZChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.Z = evt.newValue;
        if (Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            offsets.Roll.Value = evt.newValue;
    }
}
}
