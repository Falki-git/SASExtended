using System;
using UnityEngine.UIElements;

namespace SASExtended.UI.Controls
{
    // NOTE: this control uses the legacy UxmlFactory/UxmlTraits system on purpose, NOT the Unity 6
    // [UxmlElement]/UxmlSerializedData system. A mod assembly is loaded late by SpaceWarp, and the
    // new system serializes controls into the bundled VisualTreeAsset as native [SerializeReference],
    // which Unity's native serializer cannot resolve for a late-loaded assembly (there is no runtime
    // registry to fix it). The UxmlFactory system instead resolves controls by type-name string at
    // clone time via VisualElementFactoryRegistry, which the plugin populates at init (see
    // SASExtendedPlugin.RegisterUxmlFactories). Do not "upgrade" this to [UxmlElement].
    public class SideToggleControl : Button
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
                    control.SetEnabled(_isEnabled.GetValueFromBag(bag, cc));
                    control.SwitchToggleState(_isToggled.GetValueFromBag(bag, cc), false);
                }
            }
        }
    }
}
