using System;
using UnityEngine.UIElements;

namespace SASExtended.UI.Controls
{
    // DUAL-MODE UXML SUPPORT — which face this control exposes is decided by the SASX_UI_AUTHORING
    // scripting define (toggled via the "Modding/SAS Extended UI Authoring Mode" menu item):
    //
    // - Default (shipping) face: legacy UxmlFactory/UxmlTraits, registered at runtime by
    //   SASExtendedPlugin.RegisterUxmlFactories. This is the ONLY form that can load from a bundled
    //   VisualTreeAsset in-game: the Unity 6 [UxmlElement] system serializes controls into the asset
    //   as native [SerializeReference] UxmlSerializedData, which cannot be resolved for a late-loaded
    //   mod assembly (no runtime registry exists — see .claude/custom_uxml_controls.md).
    // - Authoring face (SASX_UI_AUTHORING defined): Unity 6 [UxmlElement]/[UxmlAttribute], giving
    //   UI Builder its full attribute inspector while designing UI. The UXML text is identical either
    //   way; only the IMPORTED VisualTreeAsset differs, so the editor tooling (UiAuthoringMode)
    //   reimports the UXML whenever the mode changes, and the pipeline guard (EnsureLegacyUxmlImport)
    //   blocks mod builds while authoring mode is on. Never ship a build made in authoring mode.
#if SASX_UI_AUTHORING
    [UxmlElement]
#endif
    public partial class SideToggleControl : Button
    {
        public const string UssClassName = "side-toggle";
        public const string UssClassName_Connector = UssClassName + "__connector";
        public const string UssClassName_Container = UssClassName + "__container";
        public const string UssClassName_Led = UssClassName + "__led";
        public const string UssClassName_Text = UssClassName + "__text";

        public const string UssHover = UssClassName_Container + "--hover";
        public const string UssActive = UssClassName_Container + "--active";

        public const string UssLedChecked = UssClassName_Led + "--checked";
        public const string UssLedUnchecked = UssClassName_Led + "--unchecked";
        public const string UssLedDisabled = UssClassName_Led + "--disabled";
        public const string UssTextDisabled = UssClassName_Text + "--disabled";

        public const string UssClassName_Big = UssClassName + "--big";
        public const string UssClassName_Big_Container = UssClassName_Big + "__container";
        public const string UssClassName_Big_Led = UssClassName_Big + "__led";
        public const string UssClassName_Big_Text = UssClassName_Big + "__text";

        public const string UssClassName_Small = UssClassName + "--small";
        public const string UssClassName_Small_Container = UssClassName_Small + "__container";
        public const string UssClassName_Small_Led = UssClassName_Small + "__led";
        public const string UssClassName_Small_Text = UssClassName_Small + "__text";
        
        public const string UssClassName_Long = UssClassName + "--long";
        
        public const string UssClassName_Prograde = "prograde-background";
        public const string UssClassName_Normal = "normal-background";
        public const string UssClassName_Radial = "radial-background";

        public bool IsToggled { get; private set; }
        public bool IsEnabled { get; private set; }

        private bool _isBig;
        public bool IsBig
        {
            get => _isBig;
            set
            {
                SetBigToggle(value);
                _isBig = value;
            }
        }


        private bool _isSmall;
        public bool IsSmall
        {
            get => _isSmall;
            set
            {
                SetSmallToggle(value);
                _isSmall = value;
            }
        }
        
        private bool _isLong;
        public bool IsLong
        {
            get => _isLong;
            set
            {
                SetLongToggle(value);
                _isLong = value;
            }
        }

        private VisualElement _connector;
        private VisualElement _container;
        private VisualElement _led;
        private Label _text;

        private void SetBigToggle(bool value)
        {
            if (value)
            {
                RemoveFromClassList(UssClassName);
                _container.RemoveFromClassList(UssClassName_Container);
                _led.RemoveFromClassList(UssClassName_Led);
                _text.RemoveFromClassList(UssClassName_Text);
                RemoveFromClassList(UssClassName_Small);
                _container.RemoveFromClassList(UssClassName_Small_Container);
                _led.RemoveFromClassList(UssClassName_Small_Led);
                _text.RemoveFromClassList(UssClassName_Small_Text);

                AddToClassList(UssClassName_Big);
                _container.AddToClassList(UssClassName_Big_Container);
                _led.AddToClassList(UssClassName_Big_Led);
                _text.AddToClassList(UssClassName_Big_Text);
            }
            else
            {
                RemoveFromClassList(UssClassName_Big);
                _container.RemoveFromClassList(UssClassName_Big_Container);
                _led.RemoveFromClassList(UssClassName_Big_Led);
                _text.RemoveFromClassList(UssClassName_Big_Text);
                AddToClassList(UssClassName);
                _container.AddToClassList(UssClassName_Container);
                _led.AddToClassList(UssClassName_Led);
                _text.AddToClassList(UssClassName_Text);
            }
        }

        private void SetSmallToggle(bool value)
        {
            if (value)
            {
                RemoveFromClassList(UssClassName);
                _container.RemoveFromClassList(UssClassName_Container);
                _led.RemoveFromClassList(UssClassName_Led);
                _text.RemoveFromClassList(UssClassName_Text);
                RemoveFromClassList(UssClassName_Big);
                _container.RemoveFromClassList(UssClassName_Big_Container);
                _led.RemoveFromClassList(UssClassName_Big_Led);
                _text.RemoveFromClassList(UssClassName_Big_Text);

                AddToClassList(UssClassName_Small);
                _container.AddToClassList(UssClassName_Small_Container);
                _led.AddToClassList(UssClassName_Small_Led);
                _text.AddToClassList(UssClassName_Small_Text);
            }
            else
            {
                RemoveFromClassList(UssClassName_Small);
                _container.RemoveFromClassList(UssClassName_Small_Container);
                _led.RemoveFromClassList(UssClassName_Small_Led);
                _text.RemoveFromClassList(UssClassName_Small_Text);
                AddToClassList(UssClassName);
                _container.AddToClassList(UssClassName_Container);
                _led.AddToClassList(UssClassName_Led);
                _text.AddToClassList(UssClassName_Text);
            }
        }
        
        private void SetLongToggle(bool value)
        {
            if (value)
            {
                AddToClassList(UssClassName_Long);
            }
            else
            {
                RemoveFromClassList(UssClassName_Long);
            }
        }

        public string TextValue
        {
            get => _text.text;
            set => _text.text = value;
        }

        public SideToggleControl()
        {
            AddToClassList(UssClassName);

            _connector = new VisualElement()
            {
                name = "connector"
            };
            _connector.AddToClassList(UssClassName_Connector);
            hierarchy.Add(_connector);

            _container = new VisualElement()
            {
                name = "container"
            };
            _container.AddToClassList(UssClassName_Container);
            hierarchy.Add(_container);

            _led = new VisualElement()
            {
                name = "led"
            };
            _led.AddToClassList(UssClassName_Led);
            _container.Add(_led);

            _text = new Label()
            {
                name = "text"
            };
            _text.AddToClassList(UssClassName_Text);
            // Parse "\n" (and other escape sequences) in the label text — the Unity 6 replacement for
            // the old UITK's implicit escape parsing, so authored labels like "KILL\nROT" wrap.
            _text.parseEscapeSequences = true;
            _container.Add(_text);

            hierarchy.Add((_container));

            RegisterCallback<PointerUpEvent>(OnPointerUpEvent);
            RegisterCallback<PointerDownEvent>(OnPointerDownEvent,TrickleDown.TrickleDown);
            RegisterCallback<ClickEvent>(OnClickEvent);
            RegisterCallback<PointerEnterEvent>(OnPointerEnterEvent);
            RegisterCallback<PointerLeaveEvent>(OnPointerLeaveEvent);

            // set as toggled off when first built
            SwitchToggleState(false, false);

            // set as disabled when first built
            SetEnabled(false);

            //RegisterCallback<WheelEvent>((evt) => _LOGGER.LogDebug($"(inside) WheelEvent {TextValue}"));
        }

        private void OnPointerEnterEvent(PointerEnterEvent _)
        {
            if (!IsEnabled)
                return;

            _container.AddToClassList(UssHover);
        }

        private void OnPointerLeaveEvent(PointerLeaveEvent _)
        {
            if (!IsEnabled)
                return;

            _container.RemoveFromClassList(UssHover);
            _container.RemoveFromClassList(UssActive);
        }

        private void OnPointerDownEvent(PointerDownEvent _)
        {
            if (!IsEnabled)
                return;

            _container.RemoveFromClassList(UssHover);
            _container.AddToClassList(UssActive);
        }

        private void OnPointerUpEvent(PointerUpEvent _)
        {
            if (!IsEnabled)
                return;

            _container.RemoveFromClassList(UssActive);
        }

        private void OnClickEvent(ClickEvent _)
        {
            if (!IsEnabled)
                return;

            SwitchToggleState(!IsToggled);
        }

        public void SwitchToggleState(bool state, bool playSound = true)
        {
            IsToggled = state;
            if (IsToggled)
            {
                _led.RemoveFromClassList(UssLedUnchecked);
                _led.AddToClassList(UssLedChecked);
                _text.RemoveFromClassList(UssTextDisabled);
                // if (playSound && Settings.PlayUiSounds.Value) { KSPAudioEventManager.onPartManagerVisibilityChanged(true); }
            }
            else
            {
                _led.RemoveFromClassList(UssLedChecked);
                _led.AddToClassList(UssLedUnchecked);
                _text.AddToClassList(UssTextDisabled);
                // if (playSound && Settings.PlayUiSounds.Value) { KSPAudioEventManager.onPartManagerVisibilityChanged(false); }
            }
        }

        public new void SetEnabled(bool state)
        {
            SwitchToggleState(false, false);

            IsEnabled = state;
            if (IsEnabled)
            {
                _led.RemoveFromClassList(UssLedDisabled);
                _text.RemoveFromClassList(UssTextDisabled);
                _led.AddToClassList(UssLedUnchecked);
            }
            else
            {
                _led.RemoveFromClassList(UssLedUnchecked);
                _led.RemoveFromClassList(UssLedChecked);
                _led.AddToClassList(UssLedDisabled);
                _text.AddToClassList(UssTextDisabled);
            }
        }

        public void SetSasColorMode(SasColorMode mode = SasColorMode.None)
        {
            _led.RemoveFromClassList(UssClassName_Prograde);
            _led.RemoveFromClassList(UssClassName_Normal);
            _led.RemoveFromClassList(UssClassName_Radial);
            
            switch (mode)
            {
                case SasColorMode.None: break;
                case SasColorMode.Prograde: _led.AddToClassList(UssClassName_Prograde); break;
                case SasColorMode.Normal: _led.AddToClassList(UssClassName_Normal); break;
                case SasColorMode.Radial: _led.AddToClassList(UssClassName_Radial); break;
            }
        }

#if SASX_UI_AUTHORING
        // Authoring-only UXML attribute surface for UI Builder (see the class comment). The attribute
        // names must match the legacy UxmlTraits attribute names exactly so the same UXML text works
        // in both modes. Declaration order matters: the UxmlSerializedData deserializer applies
        // attributes in declaration order and SetEnabled resets the toggle state — so IsEnabled must
        // be declared before IsToggled.
        [UxmlAttribute("Text")]
        public string UxmlText { get => TextValue; set => TextValue = value; }

        [UxmlAttribute("IsBig")]
        public bool UxmlIsBig { get => IsBig; set => IsBig = value; }

        [UxmlAttribute("IsSmall")]
        public bool UxmlIsSmall { get => IsSmall; set => IsSmall = value; }
        
        [UxmlAttribute("IsLong")]
        public bool UxmlIsLong { get => IsLong; set => IsLong = value; }

        [UxmlAttribute("IsEnabled")]
        public bool UxmlIsEnabled { get => IsEnabled; set => SetEnabled(value); }

        [UxmlAttribute("IsToggled")]
        public bool UxmlIsToggled { get => IsToggled; set => SwitchToggleState(value, false); }
#else
        public new class UxmlFactory : UxmlFactory<SideToggleControl, UxmlTraits> { }
        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            // Standard "name" attribute. In Unity 6 the base UxmlTraits.Init no longer applies it, so
            // we read and apply it ourselves (the window controller looks controls up by name).
            UxmlStringAttributeDescription _elementName = new UxmlStringAttributeDescription
            { name = "name", defaultValue = "" };

            UxmlStringAttributeDescription _name = new UxmlStringAttributeDescription
            { name = "Text", defaultValue = "Toggle" };

            UxmlBoolAttributeDescription _isBig = new UxmlBoolAttributeDescription
            {  name = "IsBig", defaultValue = false };

            UxmlBoolAttributeDescription _isSmall = new UxmlBoolAttributeDescription
            { name = "IsSmall", defaultValue = false };

            UxmlBoolAttributeDescription _isLong = new UxmlBoolAttributeDescription
            { name = "IsLong", defaultValue = false };

            UxmlBoolAttributeDescription _isEnabled = new UxmlBoolAttributeDescription
            { name = "IsEnabled", defaultValue = false };

            UxmlBoolAttributeDescription _isToggled = new UxmlBoolAttributeDescription
            { name = "IsToggled", defaultValue = false };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                // Do NOT call base.Init(): in Unity 6 UnityEngine.UIElements.UxmlTraits.Init is a no-op
                // that only logs the "deprecated UxmlTraits" warning and applies NO attributes (not even
                // `name`/`class`). We apply everything we depend on ourselves. Skipping base.Init also
                // suppresses that per-instance warning.
                ve.name = _elementName.GetValueFromBag(bag, cc);

                if (ve is SideToggleControl control)
                {
                    control.TextValue = _name.GetValueFromBag(bag, cc);
                    control.IsBig = _isBig.GetValueFromBag(bag, cc);
                    control.IsSmall = _isSmall.GetValueFromBag(bag, cc);
                    control.IsLong = _isLong.GetValueFromBag(bag, cc);
                    control.SetEnabled(_isEnabled.GetValueFromBag(bag, cc));
                    control.SwitchToggleState(_isToggled.GetValueFromBag(bag, cc), false);
                }
            }
        }
#endif
    }

    public enum SasColorMode
    {
        None,
        Prograde,
        Normal,
        Radial
    }
}
