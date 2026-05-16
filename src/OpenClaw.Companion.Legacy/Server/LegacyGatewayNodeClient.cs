using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OpenClaw.Shared;
using SuperSocket.ClientEngine;
using WebSocket4Net;

namespace OpenClaw
{
    /// <summary>
    /// Minimal gateway node client for legacy companion.
    /// Connects to OpenClaw gateway, registers as a node, and handles core node commands.
    /// </summary>
    public sealed class LegacyGatewayNodeClient : IDisposable
    {
        private readonly LegacySettingsManager _settings;
        private readonly DeviceIdentity _deviceIdentity;
        private readonly ScreenCaptureService _screenCapture;
        private readonly CommandRunner _commandRunner;
        private readonly NotifyIcon _notifyIcon;

        private Thread _worker;
        private bool _stopRequested;
        private WebSocket _socket;
        private readonly ManualResetEvent _socketClosed = new ManualResetEvent(false);

        private bool _isConnected;
        private bool _isPendingApproval;
        private bool _isPaired;
        private string _lastStatusMessage;

        public event EventHandler StatusChanged;

        public bool IsConnected { get { return _isConnected; } }
        public bool IsPendingApproval { get { return _isPendingApproval; } }
        public bool IsPaired { get { return _isPaired; } }
        public string LastStatusMessage { get { return _lastStatusMessage; } }
        public string PairingApprovalCommand { get { return "openclaw devices approve " + _deviceIdentity.DeviceId; } }

        public LegacyGatewayNodeClient(
            LegacySettingsManager settings,
            DeviceIdentity deviceIdentity,
            ScreenCaptureService screenCapture,
            CommandRunner commandRunner,
            NotifyIcon notifyIcon)
        {
            _settings = settings;
            _deviceIdentity = deviceIdentity;
            _screenCapture = screenCapture;
            _commandRunner = commandRunner;
            _notifyIcon = notifyIcon;
            _worker = null;
            _socket = null;
            _lastStatusMessage = "Node: idle";
        }

        public void Start()
        {
            if (_worker != null)
            {
                return;
            }

            _stopRequested = false;
            _worker = new Thread(new ThreadStart(RunWorker));
            _worker.IsBackground = true;
            _worker.Name = "LegacyGatewayNodeClient";
            _worker.Start();
        }

        public void Stop()
        {
            _stopRequested = true;
            CloseSocket();

            if (_worker != null)
            {
                _worker.Join(1500);
                _worker = null;
            }

            _isConnected = false;
            RaiseStatusChanged();
        }

        private void RunWorker()
        {
            while (!_stopRequested)
            {
                try
                {
                    if (string.IsNullOrEmpty(_settings.GatewayUrl) || string.IsNullOrEmpty(_settings.Token))
                    {
                        _isConnected = false;
                        _lastStatusMessage = "Gateway URL or token missing";
                        RaiseStatusChanged();
                        return;
                    }

                    string gatewayUrl = NormalizeGatewayUrl(_settings.GatewayUrl);
                    if (IsLegacyWindows9x() && gatewayUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                    {
                        _isConnected = false;
                        _lastStatusMessage = "wss is likely unsupported on Win9x; use ws://";
                        RaiseStatusChanged();
                        return;
                    }

                    List<KeyValuePair<string, string>> customHeaders = new List<KeyValuePair<string, string>>();
                    customHeaders.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + (_settings.Token ?? string.Empty)));

                    _socketClosed.Reset();
                    _socket = new WebSocket(
                        gatewayUrl,
                        string.Empty,
                        null,
                        customHeaders,
                        "OpenClawLegacy/1.0.0",
                        WebSocketVersion.Rfc6455);
                    _socket.EnableAutoSendPing = true;
                    _socket.AutoSendPingInterval = 30;
                    _socket.Opened += OnSocketOpened;
                    _socket.Closed += OnSocketClosed;
                    _socket.Error += OnSocketError;
                    _socket.MessageReceived += OnSocketMessageReceived;
                    _socket.Open();

                    while (!_stopRequested && !_socketClosed.WaitOne(500, false))
                    {
                        // Wait for closed/error and let event handlers process traffic.
                    }
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    _lastStatusMessage = "Connection failed: " + ex.Message;
                    RaiseStatusChanged();
                }
                finally
                {
                    CloseSocket();
                }

                if (!_stopRequested)
                {
                    Thread.Sleep(2000);
                }
            }
        }

        private void OnSocketOpened(object sender, EventArgs e)
        {
            _isConnected = true;
            _isPendingApproval = false;
            _isPaired = !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);
            _lastStatusMessage = _isPaired ? "Node connected" : "Node connected (unpaired)";
            RaiseStatusChanged();
        }

        private void OnSocketClosed(object sender, EventArgs e)
        {
            _isConnected = false;
            _lastStatusMessage = _stopRequested ? "Node stopped" : "Disconnected; reconnecting";
            RaiseStatusChanged();
            _socketClosed.Set();
        }

        private void OnSocketError(object sender, ErrorEventArgs e)
        {
            _isConnected = false;
            _lastStatusMessage = "Socket error; reconnecting";
            RaiseStatusChanged();
            _socketClosed.Set();
        }

        private void OnSocketMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            HandleIncomingMessage(e.Message);
        }

        private void HandleIncomingMessage(string json)
        {
            Dictionary<string, object> root = JsonHelper.Parse(json) as Dictionary<string, object>;
            if (root == null)
            {
                return;
            }

            string type = GetString(root, "type", "");
            if (string.Compare(type, "event", true) == 0)
            {
                HandleEvent(root);
                return;
            }

            if (string.Compare(type, "res", true) == 0)
            {
                HandleResponse(root);
                return;
            }

            if (string.Compare(type, "req", true) == 0)
            {
                HandleRequest(root);
            }
        }

        private void HandleEvent(Dictionary<string, object> root)
        {
            string eventType = GetString(root, "event", "");

            if (string.Compare(eventType, "connect.challenge", true) == 0)
            {
                Dictionary<string, object> payload = GetDict(root, "payload");
                string nonce = GetString(payload, "nonce", null);
                SendConnect(nonce);
                return;
            }

            if (string.Compare(eventType, "node.invoke.request", true) == 0)
            {
                HandleInvoke(GetDict(root, "payload"));
                return;
            }

            if (string.Compare(eventType, "node.pair.requested", true) == 0 ||
                string.Compare(eventType, "device.pair.requested", true) == 0)
            {
                _isPendingApproval = true;
                _isPaired = false;
                _lastStatusMessage = "Pairing pending; run: " + PairingApprovalCommand;
                RaiseStatusChanged();
                return;
            }

            if (string.Compare(eventType, "node.pair.resolved", true) == 0 ||
                string.Compare(eventType, "device.pair.resolved", true) == 0)
            {
                Dictionary<string, object> payload = GetDict(root, "payload");
                string decision = GetString(payload, "decision", "");
                if (string.Compare(decision, "approved", true) == 0)
                {
                    _isPendingApproval = false;
                    _isPaired = true;
                    _lastStatusMessage = "Pairing approved";
                    RaiseStatusChanged();
                    CloseSocket();
                }
                else if (string.Compare(decision, "rejected", true) == 0)
                {
                    _isPendingApproval = false;
                    _isPaired = false;
                    _lastStatusMessage = "Pairing rejected";
                    RaiseStatusChanged();
                }
                return;
            }
        }

        private void HandleResponse(Dictionary<string, object> root)
        {
            bool ok = GetBool(root, "ok", true);
            if (!ok)
            {
                Dictionary<string, object> error = GetDict(root, "error");
                string code = GetString(error, "code", "");
                if (string.Compare(code, "NOT_PAIRED", true) == 0)
                {
                    _isPendingApproval = true;
                    _isPaired = false;
                    _isConnected = true;
                    _lastStatusMessage = "Pairing required; run: " + PairingApprovalCommand;
                    RaiseStatusChanged();
                    return;
                }

                if (string.Compare(code, "token_mismatch", true) == 0)
                {
                    _deviceIdentity.StoreDeviceToken(null);
                    _isPendingApproval = false;
                    _isPaired = false;
                    _isConnected = false;
                    _lastStatusMessage = "Device token rejected; re-pair required";
                    RaiseStatusChanged();
                }
                return;
            }

            Dictionary<string, object> payload = GetDict(root, "payload");
            string payloadType = GetString(payload, "type", "");
            if (string.Compare(payloadType, "hello-ok", true) == 0)
            {
                _isConnected = true;
                _isPendingApproval = false;
                _isPaired = !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);

                Dictionary<string, object> auth = GetDict(payload, "auth");
                string deviceToken = GetString(auth, "deviceToken", null);
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    _deviceIdentity.StoreDeviceToken(deviceToken);
                    _isPaired = true;
                }

                _lastStatusMessage = _isPaired ? "Node connected and paired" : "Node connected; awaiting pairing";

                RaiseStatusChanged();
            }
        }

        private void HandleRequest(Dictionary<string, object> root)
        {
            string method = GetString(root, "method", "");

            if (string.Compare(method, "ping", true) == 0)
            {
                SendRaw(new Dictionary<string, object>
                {
                    { "type", "res" },
                    { "id", GetString(root, "id", Guid.NewGuid().ToString()) },
                    { "ok", true },
                    { "payload", new Dictionary<string, object>{{"type", "pong"}} }
                });
                return;
            }

            if (string.Compare(method, "node.invoke.request", true) == 0)
            {
                HandleInvoke(GetDict(root, "params"));
            }
        }

        private void HandleInvoke(Dictionary<string, object> payload)
        {
            string requestId = GetString(payload, "requestId", null);
            if (string.IsNullOrEmpty(requestId))
            {
                requestId = GetString(payload, "id", Guid.NewGuid().ToString());
            }

            string command = GetString(payload, "command", "");
            Dictionary<string, object> args = GetDict(payload, "args");
            if (args.Count == 0)
            {
                string paramsJson = GetString(payload, "paramsJSON", null);
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    try
                    {
                        object parsed = JsonHelper.Parse(paramsJson);
                        Dictionary<string, object> parsedArgs = parsed as Dictionary<string, object>;
                        if (parsedArgs != null)
                        {
                            args = parsedArgs;
                        }
                    }
                    catch
                    {
                        // Keep empty args if paramsJSON is malformed.
                    }
                }
            }

            bool ok = true;
            Dictionary<string, object> resultPayload = new Dictionary<string, object>();
            string error = null;

            try
            {
                if (string.Compare(command, "screen.snapshot", true) == 0)
                {
                    int mon = GetInt(args, "monitor", 0);
                    string fmt = GetString(args, "format", "png");
                    int mw = GetInt(args, "maxWidth", 1920);
                    int q = GetInt(args, "quality", 80);
                    bool ip = GetBool(args, "includePointer", true);
                    if (string.Compare(fmt, "jpg", true) == 0) fmt = "jpeg";

                    byte[] data = _screenCapture.CaptureScreenshot(mon, fmt, mw, q, ip);
                    resultPayload["format"] = fmt;
                    resultPayload["base64"] = Convert.ToBase64String(data);
                }
                else if (string.Compare(command, "system.run", true) == 0)
                {
                    string cmd = GetString(args, "command", null);
                    if (string.IsNullOrEmpty(cmd))
                    {
                        throw new InvalidOperationException("Missing command");
                    }

                    int timeout = GetInt(args, "timeoutMs", 30000);
                    RunResult rr = _commandRunner.Execute(cmd, timeout);
                    resultPayload["exitCode"] = rr.ExitCode;
                    resultPayload["stdout"] = rr.Stdout;
                    resultPayload["stderr"] = rr.Stderr;
                    resultPayload["timedOut"] = rr.TimedOut;
                }
                else if (string.Compare(command, "device.info", true) == 0)
                {
                    resultPayload["deviceId"] = _deviceIdentity.DeviceId;
                    resultPayload["machineName"] = _deviceIdentity.MachineName;
                    resultPayload["osVersion"] = _deviceIdentity.OSVersion;
                    resultPayload["userName"] = _deviceIdentity.UserName;
                }
                else if (string.Compare(command, "device.auth_token", true) == 0)
                {
                    resultPayload["token"] = _deviceIdentity.GenerateAuthToken();
                }
                else if (string.Compare(command, "system.notify", true) == 0)
                {
                    string title = GetString(args, "title", "OpenClaw Companion");
                    string message = GetString(args, "message", "Notification");
                    int durationMs = GetInt(args, "durationMs", 5000);
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.ShowBalloonTip(durationMs, title, message, ToolTipIcon.Info);
                    }
                    resultPayload["success"] = true;
                }
                else
                {
                    ok = false;
                    error = "Command not supported: " + command;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }

            Dictionary<string, object> p = new Dictionary<string, object>();
            p["id"] = requestId;
            p["nodeId"] = _deviceIdentity.DeviceId;
            p["ok"] = ok;
            p["payload"] = resultPayload;
            if (!ok)
            {
                p["error"] = new Dictionary<string, object> { { "message", error ?? "Unknown error" } };
            }

            SendRaw(new Dictionary<string, object>
            {
                { "type", "req" },
                { "id", Guid.NewGuid().ToString() },
                { "method", "node.invoke.result" },
                { "params", p }
            });
        }

        private void SendConnect(string nonce)
        {
            Dictionary<string, object> auth = new Dictionary<string, object>();
            auth["token"] = _settings.Token ?? "";
            if (!string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
            {
                auth["deviceToken"] = _deviceIdentity.DeviceToken;
            }

            string signature = null;
            long signedAt = (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L;
            if (!string.IsNullOrEmpty(nonce))
            {
                try
                {
                    signature = _deviceIdentity.SignPayload(nonce, signedAt, "node-host", _settings.Token ?? "");
                }
                catch (Exception ex)
                {
                    signature = null;
                    _lastStatusMessage = "Signature failed on current crypto provider: " + ex.Message;
                }
            }

            List<object> capabilities = new List<object> { "system", "screen", "device" };
            List<object> commands = new List<object> { "system.run", "system.notify", "screen.snapshot", "device.info", "device.auth_token" };

            Dictionary<string, object> permissions = new Dictionary<string, object>();
            permissions["screen.capture"] = true;
            permissions["notifications.show"] = true;

            Dictionary<string, object> client = new Dictionary<string, object>();
            client["id"] = "node-host";
            client["version"] = "1.0.0";
            client["platform"] = "windows";
            client["mode"] = "node";
            client["displayName"] = "Windows Legacy Node (" + Environment.MachineName + ")";

            Dictionary<string, object> device = new Dictionary<string, object>();
            device["id"] = _deviceIdentity.DeviceId;
            device["publicKey"] = _deviceIdentity.PublicKeyBase64Url;
            if (!string.IsNullOrEmpty(signature)) device["signature"] = signature;
            device["signedAt"] = signedAt;
            device["nonce"] = nonce;

            Dictionary<string, object> p = new Dictionary<string, object>();
            p["minProtocol"] = 3;
            p["maxProtocol"] = 3;
            p["client"] = client;
            p["role"] = "node";
            p["scopes"] = new List<object>();
            p["caps"] = capabilities;
            p["commands"] = commands;
            p["permissions"] = permissions;
            p["auth"] = auth;
            p["locale"] = "en-US";
            p["userAgent"] = "openclaw-legacy-companion/1.0.0";
            p["device"] = device;

            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["type"] = "req";
            msg["id"] = Guid.NewGuid().ToString();
            msg["method"] = "connect";
            msg["params"] = p;

            SendRaw(msg);
        }

        private void SendRaw(Dictionary<string, object> payload)
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                return;
            }

            string json = JsonHelper.Serialize(payload);
            _socket.Send(json);
        }

        private void CloseSocket()
        {
            try
            {
                if (_socket != null)
                {
                    _socket.Opened -= OnSocketOpened;
                    _socket.Closed -= OnSocketClosed;
                    _socket.Error -= OnSocketError;
                    _socket.MessageReceived -= OnSocketMessageReceived;

                    try { _socket.Close(); } catch { }
                    _socket = null;
                }
            }
            catch
            {
            }
            finally
            {
                _socketClosed.Set();
            }
        }

        private void RaiseStatusChanged()
        {
            EventHandler h = StatusChanged;
            if (h != null)
            {
                h(this, EventArgs.Empty);
            }
        }

        private static string NormalizeGatewayUrl(string gatewayUrl)
        {
            if (string.IsNullOrEmpty(gatewayUrl))
            {
                return string.Empty;
            }

            string value = gatewayUrl.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "ws://" + value.Substring("http://".Length);
            }
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "wss://" + value.Substring("https://".Length);
            }
            return value;
        }

        private static bool IsLegacyWindows9x()
        {
            PlatformID p = Environment.OSVersion.Platform;
            return p == PlatformID.Win32Windows;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is Dictionary<string, object>)
            {
                return (Dictionary<string, object>)v;
            }
            return new Dictionary<string, object>();
        }

        private static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is string)
            {
                return (string)v;
            }
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v))
            {
                if (v is int) return (int)v;
                if (v is long) return (int)(long)v;
                if (v is double) return (int)(double)v;
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is bool)
            {
                return (bool)v;
            }
            return def;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
