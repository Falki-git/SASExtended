using System;
using KSP.UI.Binding;
using SASExtended.Managers;
using SASExtended.UI.Controls;
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
    // The UIDocument component of the window game object
    private UIDocument _window;

    private VisualElement _root;

    // The backing field for the IsWindowOpen property
    private bool _isWindowOpen;

    private SideToggleControl _offToggle;
    private SideToggleControl _killrotToggle;
    private SideToggleControl _nodeToggle;

    private VisualElement _orbitContainer;
    private VisualElement _surfaceContainer;
    private VisualElement _targetContainer;
    private VisualElement _specialContainer;

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

    private SideToggleControl _hoverToggle;

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
    /// The state of the window. Setting this value will open or close the window.
    /// </summary>
    public bool IsWindowOpen
    {
        get => _isWindowOpen;
        set
        {
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

            // Update the OAB AppBar button state
            GameObject.Find(SASExtendedPlugin.ToolbarOabButtonID)
                ?.GetComponent<UIValue_WriteBool_Toggle>()
                ?.SetValue(value);
        }
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
        _root.CenterByDefault();

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

        _progradeToggle = _root.Q<SideToggleControl>("prograde");
        _normalToggle = _root.Q<SideToggleControl>("normal");
        _radialInToggle = _root.Q<SideToggleControl>("radialin");
        _retrogradeToggle = _root.Q<SideToggleControl>("retrograde");
        _antinormalToggle = _root.Q<SideToggleControl>("antinormal");
        _radialOutToggle = _root.Q<SideToggleControl>("radialout");

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
            _hoverToggle
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


        // Get the close button from the window
        var closeButton = _root.Q<Button>("close-button");
        // Add a click event handler to the close button
        closeButton.clicked += () => IsWindowOpen = false;
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
            if (toggle.IsToggled)
            {
                ClearAllModeToggles(toggle);
                setMode();
            }
            else
            {
                _offToggle.SwitchToggleState(true, false);
                SASManager.Instance.SetSASOff();
            }
        });
    }

    private void ClearAllModeToggles(SideToggleControl except)
    {
        foreach (var toggle in _allModeToggles)
        {
            if (toggle != except)
                toggle.SwitchToggleState(false, false);
        }
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
    }

    private void OnYChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.Y = evt.newValue;
    }
    private void OnZChanged(ChangeEvent<float> evt)
    {
        SASManager.Instance.Z = evt.newValue;
    }
}
}
