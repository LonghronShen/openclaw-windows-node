using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using LegacyCompanion;
using OpenClaw.Shared;

namespace OpenClaw
{
    static class Program
    {
        private static NotifyIcon trayIcon;
        private static ContextMenuStrip trayMenu;
        private static MainForm mainForm;
        private static McpHttpServer mcpServer;
        private static DateTime runningStarted;
        private static string deviceId;
        private static ToolStripMenuItem miServerStatus;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Log .NET Framework version
            string netVersion = GetDotNetFrameworkVersion();
            Debug.WriteLine("OpenClaw Legacy Companion started. .NET Version: " + netVersion);

            // Create the main form first (hidden)
            mainForm = new MainForm();
            mainForm.SetVersionInfo(netVersion);

            // Create the system tray icon
            CreateTrayIcon();

            // ── Wire up all services ──

            // 1. Load or create DeviceIdentity
            string dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawCompanion");
            DeviceIdentity deviceIdentity = new DeviceIdentity(dataPath, null);
            try
            {
                deviceIdentity.Initialize();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to initialize DeviceIdentity: " + ex.Message);
            }
            deviceId = deviceIdentity.DeviceId;

            // 2. Create ScreenCaptureService
            ScreenCaptureService screenCapture = new ScreenCaptureService();

            // 3. Create CommandRunner
            CommandRunner commandRunner = new CommandRunner();

            // 4. Create CapabilityRegistry
            CapabilityRegistry capabilityRegistry = new CapabilityRegistry();

            // 5. Create McpHttpServer with port 18790, authToken = deviceId
            mcpServer = new McpHttpServer(18790, deviceId,
                screenCapture, commandRunner, deviceIdentity,
                capabilityRegistry, trayIcon);

            // 6. Start the HTTP server
            try
            {
                mcpServer.Start();
                Debug.WriteLine("MCP HTTP Server started on port 18790");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to start MCP HTTP Server: " + ex.Message);
            }

            // 7. Record start time
            runningStarted = DateTime.Now;

            // 8. Show tray balloon notification
            try
            {
                trayIcon.ShowBalloonTip(5000,
                    "OpenClaw Legacy Companion",
                    "Running on port 18790" + Environment.NewLine +
                    "Device ID: " + deviceId,
                    ToolTipIcon.Info);
            }
            catch
            {
                // Balloon tips may not be supported on all Windows versions
            }

            // 9. Update status menu item
            UpdateServerStatusMenuItem();

            // Run the application — keep alive via the tray icon
            Application.Run();
        }

        private static void CreateTrayIcon()
        {
            trayMenu = new ContextMenuStrip();

            // Status display item (non-interactive)
            miServerStatus = new ToolStripMenuItem("Initializing...");
            miServerStatus.Enabled = false;
            trayMenu.Items.Add(miServerStatus);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miStatus = new ToolStripMenuItem("Open Window...");
            miStatus.Click += new EventHandler(miStatus_Click);
            trayMenu.Items.Add(miStatus);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += new EventHandler(miExit_Click);
            trayMenu.Items.Add(miExit);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "OpenClaw Legacy Companion";
            trayIcon.Icon = GetDefaultIcon();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;

            trayIcon.DoubleClick += new EventHandler(trayIcon_DoubleClick);
        }

        private static void miStatus_Click(object sender, EventArgs e)
        {
            ShowMainForm();
        }

        private static void miExit_Click(object sender, EventArgs e)
        {
            CleanupAndExit();
        }

        private static void trayIcon_DoubleClick(object sender, EventArgs e)
        {
            ToggleMainForm();
        }

        private static void ShowMainForm()
        {
            if (mainForm != null)
            {
                mainForm.Show();
                mainForm.Activate();
                mainForm.WindowState = FormWindowState.Normal;
            }
        }

        private static void HideMainForm()
        {
            if (mainForm != null)
            {
                mainForm.Hide();
            }
        }

        private static void ToggleMainForm()
        {
            if (mainForm == null)
            {
                return;
            }

            if (mainForm.Visible)
            {
                HideMainForm();
            }
            else
            {
                ShowMainForm();
            }
        }

        private static void UpdateServerStatusMenuItem()
        {
            if (miServerStatus != null)
            {
                TimeSpan uptime = DateTime.Now - runningStarted;
                string status = "Port: 18790 | Uptime: " +
                    (int)uptime.TotalMinutes + "m" +
                    " | ID: " + SafeShortDeviceId(deviceId);
                miServerStatus.Text = status;
            }
        }

        /// <summary>
        /// Returns first 8 chars of device ID for compact display.
        /// </summary>
        private static string SafeShortDeviceId(string id)
        {
            if (id == null || id.Length == 0)
            {
                return "---";
            }

            if (id.Length <= 8)
            {
                return id;
            }

            return id.Substring(0, 8);
        }

        private static void CleanupAndExit()
        {
            // Stop the MCP server first
            if (mcpServer != null)
            {
                try
                {
                    mcpServer.Stop();
                    Debug.WriteLine("MCP HTTP Server stopped");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error stopping MCP server: " + ex.Message);
                }
                mcpServer = null;
            }

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }

            if (trayMenu != null)
            {
                trayMenu.Dispose();
                trayMenu = null;
            }

            if (mainForm != null)
            {
                mainForm.Close();
                mainForm.Dispose();
                mainForm = null;
            }

            Application.Exit();
        }

        private static Icon GetDefaultIcon()
        {
            // Create a simple 16x16 icon programmatically (blue circle)
            // This avoids needing an embedded .ico resource
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                SolidBrush brush = new SolidBrush(Color.DodgerBlue);
                g.FillEllipse(brush, 1, 1, 14, 14);
                brush.Dispose();
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon icon = Icon.FromHandle(hIcon);
            return icon;
        }

        /// <summary>
        /// Reads the installed .NET Framework version from the registry
        /// and returns a formatted string with the version details.
        /// </summary>
        public static string GetDotNetFrameworkVersion()
        {
            StringBuilder result = new StringBuilder();

            try
            {
                // Read .NET Framework 2.0 install state
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v2.0.50727"))
                {
                    if (key != null)
                    {
                        object installValue = key.GetValue("Install");
                        object versionValue = key.GetValue("Version");
                        object spValue = key.GetValue("SP");

                        if (installValue != null && Convert.ToInt32(installValue) == 1)
                        {
                            result.Append(".NET Framework 2.0 is installed");
                        }
                        else
                        {
                            result.Append(".NET Framework 2.0 is NOT installed");
                        }

                        if (versionValue != null)
                        {
                            result.Append(" (Version: " + versionValue.ToString() + ")");
                        }

                        if (spValue != null)
                        {
                            result.Append(" SP" + spValue.ToString());
                        }

                        key.Close();
                    }
                    else
                    {
                        result.Append(".NET Framework 2.0 registry key not found");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Append("Error reading registry: " + ex.Message);
            }

            return result.ToString();
        }
    }

    /// <summary>
    /// Main application window — shows status info.
    /// Hidden by default; only appears on tray double-click or "Status..." click.
    /// </summary>
    public class MainForm : Form
    {
        private Label titleLabel;
        private Label statusLabel;
        private Button closeButton;

        public MainForm()
        {
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
            this.closeButton.Location = new Point(140, 95);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new Size(80, 26);
            this.closeButton.TabIndex = 2;
            this.closeButton.Text = "&Hide";
            this.closeButton.UseVisualStyleBackColor = true;
            this.closeButton.Click += new EventHandler(closeButton_Click);

            // MainForm
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(360, 140);
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
            this.statusLabel.Text = "Status: Running\n.NET: " + netVersion;
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
