using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenClaw.Shared;

namespace OpenClaw
{
    static class Program
    {
        private static NotifyIcon trayIcon;
        private static ContextMenuStrip trayMenu;
        private static MainForm mainForm;
        private static McpHttpServer mcpServer;
        private static LegacyGatewayNodeClient gatewayNodeClient;
        private static LegacySettingsManager settings;
        private static DeviceIdentity deviceIdentity;
        private static ScreenCaptureService screenCapture;
        private static CommandRunner commandRunner;
        private static CapabilityRegistry capabilityRegistry;
        private static string dataPath;
        private static DateTime runningStarted;
        private static string deviceId;
        private static ToolStripMenuItem miServerStatus;
        private static bool isLegacyWindows9x;
        private static string compatibilityWarning;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            try { System.IO.File.WriteAllText("C:\\inetpub\\ftproot\\outer_catch.txt", "Entered Main"); } catch { }

            try
            {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Log .NET Framework version
            string netVersion = GetDotNetFrameworkVersion();
            Debug.WriteLine("OpenClaw Legacy Companion started. .NET Version: " + netVersion);
            isLegacyWindows9x = IsLegacyWindows9x();

            // Create the main form first (hidden)
            mainForm = new MainForm();
            mainForm.SetVersionInfo(netVersion);

            // Create the system tray icon
            CreateTrayIcon();

            // ── Wire up all services ──

            // 1. Load settings and device identity
            dataPath = ResolveDataPath();
            settings = new LegacySettingsManager(dataPath);
            deviceIdentity = new DeviceIdentity(dataPath, null);
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
            screenCapture = new ScreenCaptureService();

            // 3. Create CommandRunner
            commandRunner = new CommandRunner();

            // 4. Create CapabilityRegistry
            capabilityRegistry = new CapabilityRegistry();

            // 5. Create McpHttpServer with port 18790, authToken = deviceId
            compatibilityWarning = BuildCompatibilityWarning();

            bool started = false;
            if (!isLegacyWindows9x)
            {
                mcpServer = new McpHttpServer(18790, deviceId,
                    screenCapture, commandRunner, deviceIdentity,
                    capabilityRegistry, trayIcon);

                // 6. Start the HTTP server
                started = mcpServer.Start();
                if (started)
                {
                    Debug.WriteLine("MCP HTTP Server started on port " + mcpServer.Port + ", path=" + mcpServer.Path);
                    System.IO.File.WriteAllText("C:\\inetpub\\ftproot\\start_ok.txt",
                        "port=" + mcpServer.Port + ",path=" + mcpServer.Path);
                }
                else
                {
                    Debug.WriteLine("Failed to start MCP HTTP Server - no available port");
                    System.IO.File.WriteAllText("C:\\inetpub\\ftproot\\start_ok.txt", "FAILED");
                }
            }
            else
            {
                Debug.WriteLine("MCP HTTP Server disabled: HttpListener is not reliable on Windows 9x.");
            }

            // 7. Record start time
            runningStarted = DateTime.Now;

            // 8. Show tray balloon notification
            try
            {
                trayIcon.ShowBalloonTip(5000,
                    "OpenClaw Legacy Companion",
                    (started ? "Running on port 18790" : "Running in legacy mode") + Environment.NewLine +
                    "Device ID: " + deviceId,
                    ToolTipIcon.Info);
            }
            catch
            {
                // Balloon tips may not be supported on all Windows versions
            }

            if (!string.IsNullOrEmpty(compatibilityWarning))
            {
                Debug.WriteLine("Compatibility warning: " + compatibilityWarning);
            }

            // 9. Update status menu item
            UpdateServerStatusMenuItem();

            // 10. Start node registration if enabled
            EnsureGatewayNodeClientState();

            // Run the application — keep alive via the tray icon
            Application.Run();
            }
            catch (System.Exception ex)
            {
                try { System.IO.File.WriteAllText("C:\\inetpub\\ftproot\\outer_catch.txt", "EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message + "\r\nStackTrace: " + ex.StackTrace); } catch { }
            }
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

            ToolStripMenuItem miSettings = new ToolStripMenuItem("Settings...");
            miSettings.Click += new EventHandler(miSettings_Click);
            trayMenu.Items.Add(miSettings);

            ToolStripMenuItem miConnectNode = new ToolStripMenuItem("Connect Node");
            miConnectNode.Click += new EventHandler(miConnectNode_Click);
            trayMenu.Items.Add(miConnectNode);

            ToolStripMenuItem miDisconnectNode = new ToolStripMenuItem("Disconnect Node");
            miDisconnectNode.Click += new EventHandler(miDisconnectNode_Click);
            trayMenu.Items.Add(miDisconnectNode);

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

        private static void miSettings_Click(object sender, EventArgs e)
        {
            if (settings == null)
            {
                return;
            }

            using (SettingsForm form = new SettingsForm(settings.GatewayUrl, settings.Token, settings.EnableNodeMode))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    settings.GatewayUrl = form.GatewayUrl;
                    settings.Token = form.Token;
                    settings.EnableNodeMode = form.EnableNodeMode;
                    settings.Save();
                    EnsureGatewayNodeClientState();
                    UpdateServerStatusMenuItem();
                }
            }
        }

        private static void miConnectNode_Click(object sender, EventArgs e)
        {
            if (settings == null)
            {
                return;
            }

            settings.EnableNodeMode = true;
            settings.Save();
            EnsureGatewayNodeClientState();
            UpdateServerStatusMenuItem();
        }

        private static void miDisconnectNode_Click(object sender, EventArgs e)
        {
            if (settings == null)
            {
                return;
            }

            settings.EnableNodeMode = false;
            settings.Save();
            EnsureGatewayNodeClientState();
            UpdateServerStatusMenuItem();
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
                string nodeState = "Node: off";
                if (gatewayNodeClient != null)
                {
                    if (gatewayNodeClient.IsPendingApproval)
                    {
                        nodeState = "Node: pending approval";
                    }
                    else if (gatewayNodeClient.IsConnected && gatewayNodeClient.IsPaired)
                    {
                        nodeState = "Node: connected";
                    }
                    else if (gatewayNodeClient.IsConnected)
                    {
                        nodeState = "Node: online";
                    }
                    else
                    {
                        nodeState = "Node: reconnecting";
                    }
                }

                string status = "Port: 18790 | Uptime: " +
                    (int)uptime.TotalMinutes + "m" +
                    " | " + nodeState +
                    " | ID: " + SafeShortDeviceId(deviceId);

                if (isLegacyWindows9x)
                {
                    status = "MCP: disabled on Win9x | Uptime: " +
                        (int)uptime.TotalMinutes + "m" +
                        " | " + nodeState +
                        " | ID: " + SafeShortDeviceId(deviceId);
                }

                if (gatewayNodeClient != null && gatewayNodeClient.IsPendingApproval)
                {
                    status += " | approve: openclaw devices approve " + SafeShortDeviceId(deviceId) + "...";
                }

                miServerStatus.Text = status;

                if (mainForm != null)
                {
                    string gatewayLabel = settings != null ? settings.GatewayUrl : "";
                    if (gatewayNodeClient != null && !string.IsNullOrEmpty(gatewayNodeClient.LastStatusMessage))
                    {
                        nodeState = nodeState + " (" + gatewayNodeClient.LastStatusMessage + ")";
                    }
                    else if (!string.IsNullOrEmpty(compatibilityWarning))
                    {
                        nodeState = nodeState + " (" + compatibilityWarning + ")";
                    }
                    mainForm.SetNodeInfo(nodeState, gatewayLabel);
                }
            }
        }

        private static void EnsureGatewayNodeClientState()
        {
            if (gatewayNodeClient != null)
            {
                gatewayNodeClient.StatusChanged -= OnGatewayNodeStatusChanged;
                gatewayNodeClient.Stop();
                gatewayNodeClient = null;
            }

            if (settings == null || !settings.EnableNodeMode)
            {
                return;
            }

            if (string.IsNullOrEmpty(settings.GatewayUrl) || string.IsNullOrEmpty(settings.Token))
            {
                return;
            }

            gatewayNodeClient = new LegacyGatewayNodeClient(
                settings,
                deviceIdentity,
                screenCapture,
                commandRunner,
                trayIcon);
            gatewayNodeClient.StatusChanged += new EventHandler(OnGatewayNodeStatusChanged);
            gatewayNodeClient.Start();
        }

        private static void OnGatewayNodeStatusChanged(object sender, EventArgs e)
        {
            if (mainForm != null && mainForm.IsHandleCreated)
            {
                try
                {
                    mainForm.BeginInvoke(new MethodInvoker(UpdateServerStatusMenuItem));
                    return;
                }
                catch
                {
                    // Fall back to direct update if invoke is unavailable.
                }
            }

            UpdateServerStatusMenuItem();
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

            if (gatewayNodeClient != null)
            {
                gatewayNodeClient.StatusChanged -= OnGatewayNodeStatusChanged;
                gatewayNodeClient.Stop();
                gatewayNodeClient = null;
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

        private static bool IsLegacyWindows9x()
        {
            PlatformID p = Environment.OSVersion.Platform;
            return p == PlatformID.Win32Windows;
        }

        private static string ResolveDataPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                return Path.Combine(local, "OpenClawCompanion");
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "OpenClawCompanion");
            }

            return Path.Combine(Environment.CurrentDirectory, "OpenClawCompanion");
        }

        private static string BuildCompatibilityWarning()
        {
            StringBuilder sb = new StringBuilder();

            if (isLegacyWindows9x)
            {
                sb.Append("Win9x mode: local MCP HTTP is disabled");
                if (settings != null && settings.GatewayUrl != null &&
                    settings.GatewayUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(", wss may fail on old TLS stack");
                }
            }

            try
            {
                using (new System.Security.Cryptography.SHA256Managed())
                {
                }
            }
            catch
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("SHA256 provider unavailable");
            }

            return sb.ToString();
        }
    }

}
