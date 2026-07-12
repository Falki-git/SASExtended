using System;
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

        var savedX = Settings.WindowPositionX.Value;
        var savedY = Settings.WindowPositionY.Value;
        if (savedX >= 0f && savedY >= 0f)
            _root.SetDefaultPosition(_ => new Vector2(savedX, savedY));
        else
            _root.CenterByDefault();

        _statusLabel = _root.Q<Label>("status");
        _statusLabel.text = string.Empty;

        // Persist the position once a drag finishes (dragging is the only way it ever changes).
        _root.RegisterCallback<PointerUpEvent>(evt => SaveWindowPosition());

        _offToggle = _root.Q<SideToggleControl>("off");
        _offToggle.SetEnabled(true);
        _offToggle.SwitchToggleState(false, false);
        _killrotToggle = _root.Q<SideToggleControl>("killrot");
        _killrotToggle.SetEnabled(true);
        _killrotToggle.SwitchToggleState(false, false);
        _nodeToggle = _root.Q<SideToggleControl>("node");
        _nodeToggle.SetEnabled(true);
        _nodeToggle.SwitchToggleState(false, false);

        _orbitTabToggle = _root.Q<TabToggleControl>("orb-tab");
        _orbitTabToggle.RegisterCallback<ClickEvent>(OnOrbitTabClicked);
        _orbitContainer = _root.Q<VisualElement>("orb-container");
        _surfaceTabToggle = _root.Q<TabToggleControl>("surf-tab");
        _surfaceTabToggle.RegisterCallback<ClickEvent>(OnSurfaceTabClicked);
        _surfaceContainer = _root.Q<VisualElement>("surf-container");
        _targetTabToggle = _root.Q<TabToggleControl>("tgt-tab");
        _targetTabToggle.RegisterCallback<ClickEvent>(OnTargetTabClicked);
        _targetContainer = _root.Q<VisualElement>("tgt-container");
        _specialTabToggle = _root.Q<TabToggleControl>("spec-tab");
        _specialTabToggle.RegisterCallback<ClickEvent>(OnSpecialTabClicked);
        _specialContainer = _root.Q<VisualElement>("spec-container");

        _attitudeControlsContainer = _root.Q<VisualElement>("attitude-controls");
        _hoverControlsContainer = _root.Q<VisualElement>("hover-controls");

        _progradeToggle = _root.Q<SideToggleControl>("prograde");
        _normalToggle = _root.Q<SideToggleControl>("normal");
        _radialInToggle = _root.Q<SideToggleControl>("radialin");
        _retrogradeToggle = _root.Q<SideToggleControl>("retrograde");
        _antinormalToggle = _root.Q<SideToggleControl>("antinormal");
        _radialOutToggle = _root.Q<SideToggleControl>("radialout");

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

        _svelPlusToggle = _root.Q<SideToggleControl>("svelplus");
        _svelMinusToggle = _root.Q<SideToggleControl>("svelminus");
        _surfToggle = _root.Q<SideToggleControl>("surf");
        _hvelPlusToggle = _root.Q<SideToggleControl>("hvelplus");
        _hvelMinusToggle = _root.Q<SideToggleControl>("hvelminus");
        _upToggle = _root.Q<SideToggleControl>("up");

        _targetPlusToggle = _root.Q<SideToggleControl>("tgtplus");
        _relativeVelocityPlusToggle = _root.Q<SideToggleControl>("rvelplus");
        _parPlusToggle = _root.Q<SideToggleControl>("parplus");
        _targetMinusToggle = _root.Q<SideToggleControl>("tgtminus");
        _relativeVelocityMinusToggle = _root.Q<SideToggleControl>("rvelminus");
        _parMinusToggle = _root.Q<SideToggleControl>("parminus");

        _starPlusToggle = _root.Q<SideToggleControl>("starplus");
        _starMinusToggle = _root.Q<SideToggleControl>("starminus");

        _holdToggle = _root.Q<SideToggleControl>("hold");
        _hoverToggle = _root.Q<SideToggleControl>("hover");

        // Every mode toggle is mutually exclusive with every other one, across all tabs - build the
        // full set once here so ClearAllModeToggles/RegisterModeButton don't need per-button
        // boilerplate to know what else to switch off.
        _allModeToggles = new[]
        {
            _offToggle, _killrotToggle, _nodeToggle,
            _progradeToggle, _normalToggle, _radialInToggle, _retrogradeToggle, _antinormalToggle, _radialOutToggle,
            _svelPlusToggle, _svelMinusToggle, _surfToggle, _hvelPlusToggle, _hvelMinusToggle, _upToggle,
            _targetPlusToggle, _relativeVelocityPlusToggle, _parPlusToggle, _targetMinusToggle, _relativeVelocityMinusToggle, _parMinusToggle,
            _starPlusToggle, _starMinusToggle,
            _holdToggle, _hoverToggle
        };

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

        _xToggle = _root.Q<SideToggleControl>("x-toggle");
        _xToggle.RegisterCallback<ClickEvent>(OnXToggleClicked);
        _xValue = _root.Q<FloatField>("x-value");
        _xValue.RegisterValueChangedCallback(OnXChanged);
        _yToggle = _root.Q<SideToggleControl>("y-toggle");
        _yToggle.RegisterCallback<ClickEvent>(OnYToggleClicked);
        _yValue = _root.Q<FloatField>("y-value");
        _yValue.RegisterValueChangedCallback(OnYChanged);
        _zToggle = _root.Q<SideToggleControl>("z-toggle");
        _zToggle.RegisterCallback<ClickEvent>(OnZToggleClicked);
        _zValue = _root.Q<FloatField>("z-value");
        _zValue.RegisterValueChangedCallback(OnZChanged);

        _xMinus = _root.Q<Button>("x-minus");
        _xMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value--;
        });
        _xPlus = _root.Q<Button>("x-plus");
        _xPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value++;
        });
        _xFirst = _root.Q<Button>("x-first");
        _xFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value = 0;
        });
        _xSecond = _root.Q<Button>("x-second");
        _xSecond.RegisterCallback<ClickEvent>(evt =>
        {
            _xValue.value = 90;
        });

        _yMinus = _root.Q<Button>("y-minus");
        _yMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value--;
        });
        _yPlus = _root.Q<Button>("y-plus");
        _yPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value++;
        });
        _yFirst = _root.Q<Button>("y-first");
        _yFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value = 0;
        });
        _ySecond = _root.Q<Button>("y-second");
        _ySecond.RegisterCallback<ClickEvent>(evt =>
        {
            _yValue.value = 90;
        });

        _zMinus = _root.Q<Button>("z-minus");
        _zMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value--;
        });
        _zPlus = _root.Q<Button>("z-plus");
        _zPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value++;
        });
        _zFirst = _root.Q<Button>("z-first");
        _zFirst.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value = 0;
        });
        _zSecond = _root.Q<Button>("z-second");
        _zSecond.RegisterCallback<ClickEvent>(evt =>
        {
            _zValue.value = 180;
        });


        // Purely a label ("VER VEL" is always active in hover, unlike Heading/Pitch/Roll there's no
        // per-axis enable/disable concept for it) - keep it disabled so it can't be toggled off.
        _hoverVerticalVelocityToggle = _hoverControlsContainer.Q<SideToggleControl>("ver-vel-toggle");
        _hoverVerticalVelocityToggle.SetEnabled(false);

        _hoverVerticalVelocityValue = _hoverControlsContainer.Q<FloatField>("ver-vel-value");
        // Set before registering the callback below, so displaying the remembered value on window
        // creation doesn't immediately re-trigger a (harmless but pointless) save.
        _hoverVerticalVelocityValue.value = Settings.HoverVerticalVelocity.Value;
        _hoverVerticalVelocityValue.RegisterValueChangedCallback(evt =>
        {
            SASManager.Instance.HoverTargetVerticalSpeed = evt.newValue;
            Settings.HoverVerticalVelocity.Value = evt.newValue;
        });

        _hoverVerticalVelocityMinus = _hoverControlsContainer.Q<Button>("ver-vel-minus");
        _hoverVerticalVelocityMinus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value--;
        });
        _hoverVerticalVelocityPlus = _hoverControlsContainer.Q<Button>("ver-vel-plus");
        _hoverVerticalVelocityPlus.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value++;
        });
        _hoverVerticalVelocityZero = _hoverControlsContainer.Q<Button>("ver-vel-zero");
        _hoverVerticalVelocityZero.RegisterCallback<ClickEvent>(evt =>
        {
            _hoverVerticalVelocityValue.value = 0;
        });

        _cancelHorizontalVelocityToggle = _hoverControlsContainer.Q<SideToggleControl>("cancel-horizontal-velocity");
        _cancelHorizontalVelocityToggle.SetEnabled(true);
        _cancelHorizontalVelocityToggle.SwitchToggleState(true, false);
        _cancelHorizontalVelocityToggle.RegisterCallback<ClickEvent>(evt =>
        {
            SASManager.Instance.CancelHorizontalVelocity = _cancelHorizontalVelocityToggle.IsToggled;
        });

        // Match the containers' initial visibility/colors to the starting (non-hover, no-mode) state.
        UpdatePanelForMode();
        UpdateAttitudeColors();
        // SASManager.Instance is still null this early (see UpdatePanelForMode's comment) so this
        // starts NODE/TGT-mode buttons greyed out; the first Update() tick corrects them once real
        // HasManeuverNode/HasTarget values are available.
        UpdateNodeTargetAvailability();

        // Get the close button from the window. Uses RegisterCallback<ClickEvent> rather than the
        // `.clicked` action, matching every other clickable element in this file (x/y/z-minus/plus/
        // first, tab toggles, etc.) rather than mixing two different click APIs.
        var closeButton = _root.Q<Button>("close-button");
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

    // Loads the just-engaged mode's remembered Heading/Pitch/Roll (Settings.AttitudeOffsets) into the
    // x/y/z FloatFields, which cascades into SASManager.X/Y/Z via OnXChanged/OnYChanged/OnZChanged
    // below. Modes without a stored entry (OFF, KillRot, Hover - see Settings.AttitudeOffsets) leave
    // the fields untouched, since none of them use the generic offset mechanism.
    private void ApplyStoredOffsetsForCurrentMode()
    {
        if (!Settings.AttitudeOffsets.TryGetValue(SASManager.Instance.AttitudeMode, out var offsets))
            return;

        _xValue.value = offsets.Heading.Value;
        _yValue.value = offsets.Pitch.Value;
        _zValue.value = offsets.Roll.Value;
    }

    private void ClearAllModeToggles(SideToggleControl except)
    {
        foreach (var toggle in _allModeToggles)
        {
            if (toggle != except)
                toggle.SwitchToggleState(false, false);
        }
    }

    // Hover is the only mode with its own control panel (vertical-speed target + horizontal-velocity
    // cancel toggle) in place of the shared Heading/Pitch/Roll attitude-controls panel - swap which one
    // is visible. Only called when a real mode is being engaged (see RegisterModeButton) - turning a
    // mode off (falling back to OFF) intentionally leaves the previously-visible panel alone.
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
}
}
