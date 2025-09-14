using UnityEngine.UIElements;

namespace SASExtended.UI.Controls
{
    public class TabToggleControl : Button
    {
        public const string UssClassName = "tab-toggle";
        
        public const string UssClassName_Container = UssClassName + "__container";
        public const string UssClassName_Led = UssClassName + "__led";
        public const string UssClassName_Text = UssClassName + "__text";

        public const string UssContainerChecked = UssClassName_Container + "--checked";
        public const string UssContainerUnchecked = UssClassName_Container + "--unchecked";

        public const string UssHover = UssClassName_Container + "--hover";
        public const string UssActive = UssClassName_Container + "--active";

        public const string UssLedChecked = UssClassName_Led + "--checked";
        public const string UssLedUnchecked = UssClassName_Led + "--unchecked";
        public const string UssLedDisabled = UssClassName_Led + "--disabled";

        public const string UssTextChecked = UssClassName_Text + "--checked";
        public const string UssTextUnchecked = UssClassName_Text + "--unchecked";
        public const string UssTextDisabled = UssClassName_Text + "--disabled";

        public bool IsToggled { get; private set; }
        public bool IsEnabled { get; private set; }

        private VisualElement _container;
        private VisualElement _led;
        private Label _text;

        public string TextValue
        {
            get => _text.text;
            set => _text.text = value;
        }

        public TabToggleControl()
        {
            AddToClassList(UssClassName);

            _container = new VisualElement()
            {
                name = "container"
            };
            _container.AddToClassList(UssClassName_Container);
            hierarchy.Add(_container);

            _text = new Label()
            {
                name = "text"
            };
            _text.AddToClassList(UssClassName_Text);
            _container.Add(_text);

            _led = new VisualElement()
            {
                name = "led"
            };
            _led.AddToClassList(UssClassName_Led);
            _container.Add(_led);

            hierarchy.Add((_container));

            RegisterCallback<PointerUpEvent>(OnPointerUpEvent);
            RegisterCallback<PointerDownEvent>(OnPointerDownEvent,TrickleDown.TrickleDown);
            RegisterCallback<ClickEvent>(OnClickEvent);
            RegisterCallback<PointerEnterEvent>(OnPointerEnterEvent);
            RegisterCallback<PointerLeaveEvent>(OnPointerLeaveEvent);

            // set as toggled off when first built
            SwitchToggleState(false, false);
            
            // set as enabled when first built
            SetEnabled(false);
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
                _text.RemoveFromClassList(UssTextUnchecked);
                _text.AddToClassList(UssTextChecked);
                _container.RemoveFromClassList(UssContainerUnchecked);
                _container.AddToClassList(UssContainerChecked);

                // if (playSound && Settings.PlayUiSounds.Value) { KSPAudioEventManager.onPartManagerVisibilityChanged(true); }
            }
            else
            {
                _led.RemoveFromClassList(UssLedChecked);
                _led.AddToClassList(UssLedUnchecked);
                _text.RemoveFromClassList(UssTextChecked);
                _text.AddToClassList(UssTextUnchecked);
                _container.RemoveFromClassList(UssContainerChecked);
                _container.AddToClassList(UssContainerUnchecked);

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
                _led.AddToClassList(UssLedUnchecked);
                _text.RemoveFromClassList(UssTextDisabled);
            }
            else
            {
                _led.RemoveFromClassList(UssLedUnchecked);
                _led.RemoveFromClassList(UssLedChecked);
                _led.AddToClassList(UssLedDisabled);
                _text.AddToClassList(UssTextDisabled);
            }
        }        

        public new class UxmlFactory : UxmlFactory<TabToggleControl, UxmlTraits> { }
        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            UxmlStringAttributeDescription _name = new()
            { name = "Text", defaultValue = "Tab" };

            UxmlBoolAttributeDescription _isEnabled = new()
            { name = "IsEnabled", defaultValue = false };
            
            UxmlBoolAttributeDescription _isToggled = new()
            { name = "IsToggled", defaultValue = false };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);

                if (ve is TabToggleControl control)
                {
                    control.TextValue = _name.GetValueFromBag(bag, cc);
                    control.SetEnabled(_isEnabled.GetValueFromBag(bag, cc));
                    control.SwitchToggleState(_isToggled.GetValueFromBag(bag, cc), false);
                }
            }
        }
    }
}