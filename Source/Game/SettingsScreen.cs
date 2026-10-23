using System;
using System.Text;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace SirCorrectifsPulsar
{
    // The plugin's settings, opened from Pulsar: one checkbox per setting.
    // Every change is saved at once and applies to the game at the next
    // update, without restarting it.
    internal sealed class SettingsScreen : MyGuiScreenBase
    {
        private const float Width = 0.62f;
        private const float Height = 0.44f;
        private const float LabelColumn = -0.27f;
        private const float ControlColumn = 0.24f;
        private const float LineSpacing = 0.07f;

        private readonly SirCorrectifsPulsarPlugin m_plugin;
        private readonly Settings m_settings;

        public SettingsScreen(SirCorrectifsPulsarPlugin plugin)
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(Width, Height), false, null, 0.9f, 0.9f)
        {
            m_plugin = plugin;
            m_settings = plugin.Settings;
            EnabledBackgroundFade = true;
            CloseButtonEnabled = true;
            RecreateControls(true);
        }

        public override string GetFriendlyName()
        {
            return "SirCorrectifsPulsarSettingsScreen";
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption(Texts.ScreenTitle);

            var y = -Height / 2 + 0.13f;

            AddCheckbox(Texts.WheeledVehiclePrediction, Texts.WheeledVehiclePredictionHelp, y,
                m_settings.WheeledVehiclePrediction, v => m_settings.WheeledVehiclePrediction = v);
            y += LineSpacing;

            AddCheckbox(Texts.MechanicalBlocksPrediction, Texts.MechanicalBlocksPredictionHelp, y,
                m_settings.MechanicalBlocksPrediction, v => m_settings.MechanicalBlocksPrediction = v);
            y += LineSpacing;

            Controls.Add(new MyGuiControlLabel(new Vector2(LabelColumn, y), null, Texts.ScreenOnlyAsClient, null, 0.7f));
            y += LineSpacing;

            if (m_plugin.Stop != null && m_plugin.Stop.IsStopped)
                Controls.Add(new MyGuiControlLabel(new Vector2(LabelColumn, y), null, Texts.ScreenStopped, null, 0.8f, "Red"));

            var buttonsY = Height / 2 - 0.07f;
            Controls.Add(new MyGuiControlButton(new Vector2(0f, buttonsY), text: new StringBuilder(Texts.CloseButton),
                onButtonClick: b => CloseScreen()));
        }

        private void AddCheckbox(string text, string help, float y, bool value, Action<bool> assign)
        {
            var label = new MyGuiControlLabel(new Vector2(LabelColumn, y), null, text);
            label.SetToolTip(help);
            Controls.Add(label);

            var checkbox = new MyGuiControlCheckbox(new Vector2(ControlColumn, y), null, help, value);
            checkbox.IsCheckedChanged = c =>
            {
                assign(c.IsChecked);
                m_plugin.Apply(m_settings);
            };
            Controls.Add(checkbox);
        }
    }
}
