using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenClaw
{
    /// <summary>
    /// Main application window — shows status info.
    /// Hidden by default; only appears on tray double-click or "Open Window..." click.
    /// </summary>
    public class MainForm : Form
    {
        private Label titleLabel;
        private Label statusLabel;
        private Button closeButton;
        private string _netInfo;
        private string _nodeInfo;
        private string _gatewayInfo;

        public MainForm()
        {
            _netInfo = "";
            _nodeInfo = "Node: off";
            _gatewayInfo = "";
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.titleLabel = new Label();
            this.statusLabel = new Label();
            this.closeButton = new Button();
            this.SuspendLayout();

            // titleLabel
            this.titleLabel.AutoSize = true;
            this.titleLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point, ((byte)(0)));
            this.titleLabel.Location = new Point(20, 18);
            this.titleLabel.Name = "titleLabel";
            this.titleLabel.Size = new Size(310, 21);
            this.titleLabel.TabIndex = 0;
            this.titleLabel.Text = "OpenClaw Legacy Companion v1.0.0";

            // statusLabel
            this.statusLabel.AutoSize = true;
            this.statusLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, ((byte)(0)));
            this.statusLabel.Location = new Point(22, 55);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new Size(250, 15);
            this.statusLabel.TabIndex = 1;
            this.statusLabel.Text = "Status: Initializing...";

            // closeButton
            this.closeButton.Location = new Point(140, 125);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new Size(80, 26);
            this.closeButton.TabIndex = 2;
            this.closeButton.Text = "&Hide";
            this.closeButton.UseVisualStyleBackColor = true;
            this.closeButton.Click += new EventHandler(closeButton_Click);

            // MainForm
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(500, 170);
            this.Controls.Add(this.closeButton);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.titleLabel);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "OpenClaw Legacy Companion";
            this.ShowInTaskbar = false;
            this.FormClosing += new FormClosingEventHandler(MainForm_FormClosing);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        public void SetVersionInfo(string netVersion)
        {
            _netInfo = netVersion;
            UpdateStatusLabel();
        }

        public void SetNodeInfo(string nodeInfo, string gatewayUrl)
        {
            _nodeInfo = string.IsNullOrEmpty(nodeInfo) ? "Node: off" : nodeInfo;
            _gatewayInfo = string.IsNullOrEmpty(gatewayUrl) ? "" : gatewayUrl;
            UpdateStatusLabel();
        }

        private void UpdateStatusLabel()
        {
            this.statusLabel.Text = "Status: Running"
                + "\n.NET: " + _netInfo
                + "\n" + _nodeInfo
                + "\nGateway: " + _gatewayInfo;
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            this.Hide();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // If the user tries to close via Alt+F4 or Close button,
            // just hide instead. Use tray Exit to truly quit.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }
    }
}
