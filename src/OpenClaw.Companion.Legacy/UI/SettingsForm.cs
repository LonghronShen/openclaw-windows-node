using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenClaw
{
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _gatewayUrlTextBox;
        private readonly TextBox _tokenTextBox;
        private readonly CheckBox _enableNodeModeCheckBox;
        private readonly Button _saveButton;
        private readonly Button _cancelButton;

        public string GatewayUrl
        {
            get { return _gatewayUrlTextBox.Text.Trim(); }
        }

        public string Token
        {
            get { return _tokenTextBox.Text.Trim(); }
        }

        public bool EnableNodeMode
        {
            get { return _enableNodeModeCheckBox.Checked; }
        }

        public SettingsForm(string gatewayUrl, string token, bool enableNodeMode)
        {
            Text = "OpenClaw Companion Settings";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 230);

            Label gatewayLabel = new Label();
            gatewayLabel.AutoSize = true;
            gatewayLabel.Location = new Point(20, 20);
            gatewayLabel.Text = "Gateway URL:";

            _gatewayUrlTextBox = new TextBox();
            _gatewayUrlTextBox.Location = new Point(20, 42);
            _gatewayUrlTextBox.Size = new Size(475, 22);
            _gatewayUrlTextBox.Text = gatewayUrl ?? "";

            Label tokenLabel = new Label();
            tokenLabel.AutoSize = true;
            tokenLabel.Location = new Point(20, 78);
            tokenLabel.Text = "Gateway Token:";

            _tokenTextBox = new TextBox();
            _tokenTextBox.Location = new Point(20, 100);
            _tokenTextBox.Size = new Size(475, 22);
            _tokenTextBox.Text = token ?? "";
            _tokenTextBox.UseSystemPasswordChar = true;

            _enableNodeModeCheckBox = new CheckBox();
            _enableNodeModeCheckBox.AutoSize = true;
            _enableNodeModeCheckBox.Location = new Point(20, 138);
            _enableNodeModeCheckBox.Text = "Enable Node Mode (connect and register to gateway)";
            _enableNodeModeCheckBox.Checked = enableNodeMode;

            _saveButton = new Button();
            _saveButton.Text = "Save";
            _saveButton.Size = new Size(90, 30);
            _saveButton.Location = new Point(310, 180);
            _saveButton.Click += new EventHandler(OnSaveClick);

            _cancelButton = new Button();
            _cancelButton.Text = "Cancel";
            _cancelButton.Size = new Size(90, 30);
            _cancelButton.Location = new Point(405, 180);
            _cancelButton.Click += new EventHandler(OnCancelClick);

            Controls.Add(gatewayLabel);
            Controls.Add(_gatewayUrlTextBox);
            Controls.Add(tokenLabel);
            Controls.Add(_tokenTextBox);
            Controls.Add(_enableNodeModeCheckBox);
            Controls.Add(_saveButton);
            Controls.Add(_cancelButton);
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (GatewayUrl.Length == 0)
            {
                MessageBox.Show(this, "Gateway URL is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (EnableNodeMode && Token.Length == 0)
            {
                MessageBox.Show(this, "Gateway token is required when Node Mode is enabled.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
