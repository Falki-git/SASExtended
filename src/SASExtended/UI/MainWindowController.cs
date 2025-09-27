using KSP.UI.Binding;
using SASExtended.UI.Controls;
using SASExtended.Unity.Runtime;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SASExtended.UI;

/// <summary>
/// Controller for the MyFirstWindow UI.
/// </summary>
public class MainWindowController : MonoBehaviour
{
    // The UIDocument component of the window game object
    private UIDocument _window;

    // The elements of the window that we need to access
    private VisualElement _root;
    private TextField _nameTextfield;
    private Toggle _noonToggle;
    private Label _greetingLabel;

    // The backing field for the IsWindowOpen property
    private bool _isWindowOpen;

    private SideToggleControl _offToggle;
    private SideToggleControl _killrotToggle;
    private SideToggleControl _nodeToggle;

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

    private VisualElement _orbitContainer;
    private VisualElement _surfaceContainer;
    private VisualElement _targetContainer;
    private VisualElement _specialContainer;


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
        _killrotToggle = _root.Q<SideToggleControl>("killrot");
        _nodeToggle = _root.Q<SideToggleControl>("node");

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
        _progradeToggle.RegisterCallback<ClickEvent>(OnProgradeClicked);
        _normalToggle = _root.Q<SideToggleControl>("normal");
        _normalToggle.RegisterCallback<ClickEvent>(OnNormalClicked);
        _radialInToggle = _root.Q<SideToggleControl>("radialin");
        _radialInToggle.RegisterCallback<ClickEvent>(OnRadialInClicked);
        _retrogradeToggle = _root.Q<SideToggleControl>("retrograde");
        _retrogradeToggle.RegisterCallback<ClickEvent>(OnRetrogradeClicked);
        _antinormalToggle = _root.Q<SideToggleControl>("antinormal");
        _antinormalToggle.RegisterCallback<ClickEvent>(OnAntiNormalClicked);
        _radialOutToggle = _root.Q<SideToggleControl>("radialout");
        _radialOutToggle.RegisterCallback<ClickEvent>(OnRadialOutClicked);

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

        _offToggle.SetEnabled(true);
        _offToggle.SwitchToggleState(false, false);
        _offToggle.RegisterCallback<ClickEvent>(OnOffClicked);
        _killrotToggle.SetEnabled(true);
        _killrotToggle.SwitchToggleState(false, false);
        _killrotToggle.RegisterCallback<ClickEvent>(OnKillrotClicked);
        _nodeToggle.SetEnabled(true);
        _nodeToggle.SwitchToggleState(false, false);
        _nodeToggle.RegisterCallback<ClickEvent>(OnNodeClicked);



        // Get the text field from the window
        _nameTextfield = _root.Q<TextField>("name-textfield");
        // Get the toggle from the window
        _noonToggle = _root.Q<Toggle>("noon-toggle");
        // Get the greeting label from the window
        _greetingLabel = _root.Q<Label>("greeting-label");
        

        // Get the close button from the window
        var closeButton = _root.Q<Button>("close-button");
        // Add a click event handler to the close button
        closeButton.clicked += () => IsWindowOpen = false;
    }

    #region Orbit buttons

    private void OnProgradeClicked(ClickEvent evt)
    {
        if (_progradeToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            //_progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to prograde
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }

    private void OnNormalClicked(ClickEvent evt)
    {
        if (_normalToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            //_normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to normal
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }
    private void OnRadialInClicked(ClickEvent evt)
    {
        if (_radialInToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            //_radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to radial in
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }

    private void OnRetrogradeClicked(ClickEvent evt)
    {
        if (_retrogradeToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            //_retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to retrograde
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }

    private void OnAntiNormalClicked(ClickEvent evt)
    {
        if (_antinormalToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            //_antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to antionormal
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }

    private void OnRadialOutClicked(ClickEvent evt)
    {
        if (_radialOutToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            //_radialOutToggle.SwitchToggleState(false, false);

            // TODO activate SAS and set to radialout
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
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

    private void OnOffClicked(ClickEvent evt)
    {
        if (_offToggle.IsToggled)
        {
            //_offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);        
        }

        // TODO deactivate SAS
    }

    private void OnKillrotClicked(ClickEvent evt)
    {
        if (_killrotToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            //_killrotToggle.SwitchToggleState(false, false);
            _nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }

    private void OnNodeClicked(ClickEvent evt)
    {
        if (_nodeToggle.IsToggled)
        {
            _offToggle.SwitchToggleState(false, false);
            _killrotToggle.SwitchToggleState(false, false);
            //_nodeToggle.SwitchToggleState(false, false);

            _progradeToggle.SwitchToggleState(false, false);
            _normalToggle.SwitchToggleState(false, false);
            _radialInToggle.SwitchToggleState(false, false);
            _retrogradeToggle.SwitchToggleState(false, false);
            _antinormalToggle.SwitchToggleState(false, false);
            _radialOutToggle.SwitchToggleState(false, false);
        }
        else
        {
            _offToggle.SwitchToggleState(true, false);
            // TODO deactivate SAS
        }
    }
}
