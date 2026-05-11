using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LegacyCompanion;
using OpenClaw.Shared;

namespace OpenClaw
{
    /// <summary>
    /// Simplified MCP (Model Context Protocol) HTTP server that combines
    /// ScreenCaptureService, CommandRunner, and DeviceIdentity into a
    /// single JSON-RPC endpoint.
    ///
    /// Protocol: JSON-RPC 2.0 over HTTP POST
    /// Endpoint: http://127.0.0.1:{port}/jsonrpc
    /// Security: Loopback-only binding + Bearer token auth
    ///
    /// .NET Framework 2.0 compatible — no LINQ, lambdas, async/await, or var.
    /// </summary>
    public sealed class McpHttpServer
    {
        // ───────────────────── Fields ─────────────────────

        private HttpListener _listener;
        private int _port;
        private string _authToken;
        private ScreenCaptureService _screenCapture;
        private CommandRunner _commandRunner;
        private DeviceIdentity _deviceIdentity;
        private bool _running;
        private Thread _listenerThread;

        private CapabilityRegistry _capabilityRegistry;
        private NotifyIcon _notifyIcon;

        private const string JSON_RPC_VERSION = "2.0";
        // JSON-RPC error codes
        private const int ERR_PARSE = -32700;
        private const int ERR_INVALID_REQUEST = -32600;
        private const int ERR_METHOD_NOT_FOUND = -32601;
        private const int ERR_INVALID_PARAMS = -32602;
        private const int ERR_INTERNAL = -32603;

        // ───────────────────── Constructor ─────────────────────

        /// <summary>
        /// Creates the MCP HTTP server.
        /// </summary>
        /// <param name="port">TCP port to listen on (127.0.0.1 only).</param>
        /// <param name="authToken">Bearer token required for all requests.</param>
        /// <param name="screenCapture">Screen capture service instance.</param>
        /// <param name="commandRunner">Command execution service instance.</param>
        /// <param name="deviceIdentity">Device identity service instance.</param>
        /// <param name="capabilityRegistry">Capability registry for extensible method dispatch.</param>
        /// <param name="notifyIcon">System tray icon for balloon notifications.</param>
        public McpHttpServer(int port, string authToken,
            ScreenCaptureService screenCapture,
            CommandRunner commandRunner,
            DeviceIdentity deviceIdentity,
            CapabilityRegistry capabilityRegistry,
            NotifyIcon notifyIcon)
        {
            _port = port;
            _authToken = authToken;
            _screenCapture = screenCapture;
            _commandRunner = commandRunner;
            _deviceIdentity = deviceIdentity;
            _capabilityRegistry = capabilityRegistry;
            _notifyIcon = notifyIcon;
            _running = false;
            _listenerThread = null;
        }

        // ───────────────────── Public API ─────────────────────

        /// <summary>
        /// Starts the HTTP listener on http://127.0.0.1:{port}/.
        /// Runs the accept loop on a background thread.
        /// </summary>
        public void Start()
        {
            if (_running)
            {
                return; // Already running
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
            _listener.Start();

            _running = true;

            _listenerThread = new Thread(new ThreadStart(ListenLoop));
            _listenerThread.IsBackground = true;
            _listenerThread.Name = "McpHttpListener";
            _listenerThread.Start();
        }

        /// <summary>
        /// Stops the HTTP listener. Waits briefly for the listener thread.
        /// </summary>
        public void Stop()
        {
            _running = false;

            if (_listener != null && _listener.IsListening)
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                    // Swallow exceptions during shutdown
                }
            }

            // Wait for listener thread to finish (with timeout)
            if (_listenerThread != null && _listenerThread.IsAlive)
            {
                _listenerThread.Join(1000);
            }

            if (_listener != null)
            {
                try
                {
                    _listener.Close();
                }
                catch
                {
                    // Swallow
                }

                _listener = null;
            }
        }

        // ───────────────────── Listener Loop ─────────────────────

        /// <summary>
        /// Main accept loop. Runs on a background thread.
        /// Each incoming request is dispatched to the thread pool.
        /// </summary>
        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(new WaitCallback(HandleRequest), context);
                }
                catch (HttpListenerException)
                {
                    // Listener stopped — break if shutting down
                    if (!_running)
                    {
                        break;
                    }

                    // Unexpected error — retry after brief delay
                    Thread.Sleep(100);
                }
                catch (ObjectDisposedException)
                {
                    // Listener disposed — exit loop
                    break;
                }
            }
        }

        // ───────────────────── Request Handler ─────────────────────

        /// <summary>
        /// Handles a single HTTP request. Thread pool entry point.
        /// </summary>
        private void HandleRequest(object state)
        {
            HttpListenerContext context = (HttpListenerContext)state;

            try
            {
                string method = context.Request.HttpMethod;
                string rawUrl = context.Request.RawUrl;

                // Only handle POST to /jsonrpc
                if (string.Compare(method, "POST", StringComparison.OrdinalIgnoreCase) != 0)
                {
                    SendStatus(context, 405, "Method Not Allowed");
                    return;
                }

                if (string.Compare(rawUrl, "/jsonrpc", StringComparison.OrdinalIgnoreCase) != 0)
                {
                    SendStatus(context, 404, "Not Found");
                    return;
                }

                // ── Security checks ──

                // Reject requests with Origin header (CSRF protection)
                string originHeader = context.Request.Headers["Origin"];
                if (originHeader != null && originHeader.Trim().Length > 0)
                {
                    SendJsonError(context, 403,
                        JsonRpcError(null, ERR_INVALID_REQUEST,
                            "Origin header is not allowed"));
                    return;
                }

                // Require Authorization: Bearer {token}
                string authHeader = context.Request.Headers["Authorization"];
                if (!CheckAuth(authHeader))
                {
                    context.Response.StatusCode = 401;
                    context.Response.StatusDescription = "Unauthorized";
                    byte[] body = Encoding.UTF8.GetBytes(
                        "{\"error\":\"Unauthorized\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    context.Response.OutputStream.Write(body, 0, body.Length);
                    context.Response.OutputStream.Close();
                    return;
                }

                // ── Read request body ──
                string requestBody;
                using (StreamReader reader = new StreamReader(
                    context.Request.InputStream, Encoding.UTF8))
                {
                    requestBody = reader.ReadToEnd();
                }

                // ── Parse JSON-RPC request ──
                Dictionary<string, object> rpcRequest = null;

                try
                {
                    object parsed = JsonHelper.Parse(requestBody);

                    if (parsed is Dictionary<string, object>)
                    {
                        rpcRequest = (Dictionary<string, object>)parsed;
                    }
                    else
                    {
                        SendJsonError(context, 400,
                            JsonRpcError(null, ERR_INVALID_REQUEST,
                                "Request body must be a JSON object"));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SendJsonError(context, 400,
                        JsonRpcError(null, ERR_PARSE, "Parse error: " + ex.Message));
                    return;
                }

                // ── Validate JSON-RPC fields ──
                object jsonrpcVersion = null;
                rpcRequest.TryGetValue("jsonrpc", out jsonrpcVersion);

                string jsonrpcStr = (jsonrpcVersion is string) ? (string)jsonrpcVersion : null;

                if (string.Compare(jsonrpcStr, JSON_RPC_VERSION, StringComparison.Ordinal) != 0)
                {
                    SendJsonError(context, 400,
                        JsonRpcError(null, ERR_INVALID_REQUEST,
                            "jsonrpc must be \"" + JSON_RPC_VERSION + "\""));
                    return;
                }

                // ── Extract method ──
                object methodObj = null;
                rpcRequest.TryGetValue("method", out methodObj);

                string methodName = (methodObj is string) ? (string)methodObj : null;

                if (methodName == null || methodName.Trim().Length == 0)
                {
                    SendJsonError(context, 400,
                        JsonRpcError(null, ERR_INVALID_REQUEST, "Missing 'method'"));
                    return;
                }

                // ── Extract id (optional) ──
                object requestId = null;
                rpcRequest.TryGetValue("id", out requestId);

                // ── Extract params (optional) ──
                object paramsObj = null;
                rpcRequest.TryGetValue("params", out paramsObj);

                Dictionary<string, object> rpcParams = paramsObj as Dictionary<string, object>;
                if (rpcParams == null)
                {
                    rpcParams = new Dictionary<string, object>();
                }

                // ── Dispatch ──
                Dictionary<string, object> result = null;

                if (string.Compare(methodName, "system.list_capabilities", StringComparison.Ordinal) == 0)
                {
                    result = HandleListCapabilities(requestId);
                }
                else if (string.Compare(methodName, "screen.snapshot", StringComparison.Ordinal) == 0)
                {
                    result = HandleScreenSnapshot(requestId, rpcParams);
                }
                else if (string.Compare(methodName, "system.run", StringComparison.Ordinal) == 0)
                {
                    result = HandleSystemRun(requestId, rpcParams);
                }
                else if (string.Compare(methodName, "device.info", StringComparison.Ordinal) == 0)
                {
                    result = HandleDeviceInfo(requestId);
                }
                else if (string.Compare(methodName, "device.auth_token", StringComparison.Ordinal) == 0)
                {
                    result = HandleAuthToken(requestId, rpcParams);
                }
                else if (string.Compare(methodName, "system.notify", StringComparison.Ordinal) == 0)
                {
                    result = HandleSystemNotify(requestId, rpcParams);
                }
                else
                {
                    // Fallback: check the capability registry for additional handlers
                    CapabilityRegistry.CapabilityHandler registryHandler = null;
                    if (_capabilityRegistry != null &&
                        _capabilityRegistry.TryGetHandler(methodName, out registryHandler))
                    {
                        try
                        {
                            string paramsJson = JsonHelper.Serialize(rpcParams);
                            string resultJson = registryHandler(paramsJson);
                            Dictionary<string, object> parsedResult =
                                JsonHelper.Parse(resultJson) as Dictionary<string, object>;
                            if (parsedResult != null)
                            {
                                result = JsonRpcResult(requestId, parsedResult);
                            }
                            else
                            {
                                Dictionary<string, object> resultDict = new Dictionary<string, object>();
                                resultDict["value"] = resultJson;
                                result = JsonRpcResult(requestId, resultDict);
                            }
                        }
                        catch (Exception ex)
                        {
                            result = JsonRpcError(requestId, ERR_INTERNAL,
                                "Capability handler error: " + ex.Message);
                        }
                    }
                    else
                    {
                        result = JsonRpcError(requestId, ERR_METHOD_NOT_FOUND,
                            "Method not found: " + methodName);
                    }
                }

                // ── Send response ──
                string responseJson = JsonHelper.Serialize(result);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                // Last-resort error handling
                try
                {
                    Dictionary<string, object> errorResponse = JsonRpcError(null,
                        ERR_INTERNAL, "Internal error: " + ex.Message);
                    string responseJson = JsonHelper.Serialize(errorResponse);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // Nothing more we can do
                }
            }
        }

        // ───────────────────── Authentication ─────────────────────

        /// <summary>
        /// Verify the Authorization header. Expects "Bearer {token}".
        /// </summary>
        private bool CheckAuth(string authHeader)
        {
            if (authHeader == null || authHeader.Trim().Length == 0)
            {
                return false;
            }

            string trimmed = authHeader.Trim();

            // Check for "Bearer " prefix (case-insensitive)
            if (trimmed.Length < 8)
            {
                return false;
            }

            string token = trimmed.Substring(7).Trim();

            // Case-insensitive check for "Bearer " prefix
            if (!StartsWithIgnoreCase(trimmed, "Bearer "))
            {
                return false;
            }

            // Compare token (case-sensitive — tokens are base64 or hash)
            if (token.Length == 0)
            {
                return false;
            }

            return (string.CompareOrdinal(token, _authToken) == 0);
        }

        // ───────────────────── Method Handlers ─────────────────────

        /// <summary>
        /// system.list_capabilities — Returns list of supported capabilities.
        /// </summary>
        private Dictionary<string, object> HandleListCapabilities(object requestId)
        {
            List<object> capabilities = new List<object>();
            capabilities.Add("screen.snapshot");
            capabilities.Add("system.run");
            capabilities.Add("device.info");
            capabilities.Add("device.auth_token");
            capabilities.Add("system.notify");

            // Add dynamically registered capabilities
            if (_capabilityRegistry != null)
            {
                string[] registeredNames = _capabilityRegistry.GetCapabilityNames();
                for (int i = 0; i < registeredNames.Length; i++)
                {
                    // Avoid duplicates
                    bool found = false;
                    for (int j = 0; j < capabilities.Count; j++)
                    {
                        if (string.Compare(
                            capabilities[j] as string,
                            registeredNames[i],
                            StringComparison.Ordinal) == 0)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        capabilities.Add(registeredNames[i]);
                    }
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["capabilities"] = capabilities;

            return JsonRpcResult(requestId, result);
        }

        /// <summary>
        /// screen.snapshot — Capture screenshot from the specified monitor.
        /// </summary>
        private Dictionary<string, object> HandleScreenSnapshot(
            object requestId, Dictionary<string, object> rpcParams)
        {
            try
            {
                // Extract params with defaults
                int monitor = GetIntParam(rpcParams, "monitor", 0);
                string format = GetStringParam(rpcParams, "format", "png");
                int maxWidth = GetIntParam(rpcParams, "maxWidth", 1920);
                int quality = GetIntParam(rpcParams, "quality", 80);
                bool includePointer = GetBoolParam(rpcParams, "includePointer", true);

                // Normalize format
                if (string.Compare(format, "jpg", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    format = "jpeg";
                }

                // Estimate output dimensions from screen info
                int screenWidth = 0;
                int screenHeight = 0;

                try
                {
                    System.Windows.Forms.Screen[] screens =
                        System.Windows.Forms.Screen.AllScreens;
                    int idx = monitor;
                    if (idx < 0 || idx >= screens.Length)
                    {
                        idx = 0;
                    }

                    screenWidth = screens[idx].Bounds.Width;
                    screenHeight = screens[idx].Bounds.Height;
                }
                catch
                {
                    screenWidth = 1920;
                    screenHeight = 1080;
                }

                int finalWidth = screenWidth;
                int finalHeight = screenHeight;

                if (screenWidth > maxWidth)
                {
                    double scale = (double)maxWidth / (double)screenWidth;
                    finalWidth = maxWidth;
                    finalHeight = (int)((double)screenHeight * scale);
                }

                // Capture the screenshot
                byte[] imageBytes = _screenCapture.CaptureScreenshot(
                    monitor, format, maxWidth, quality, includePointer);

                // Base64 encode
                string base64 = Convert.ToBase64String(imageBytes);

                Dictionary<string, object> result = new Dictionary<string, object>();
                result["format"] = format;
                result["width"] = finalWidth;
                result["height"] = finalHeight;
                result["base64"] = base64;

                return JsonRpcResult(requestId, result);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL,
                    "Screen capture failed: " + ex.Message);
            }
        }

        /// <summary>
        /// system.run — Execute a command and capture output.
        /// </summary>
        private Dictionary<string, object> HandleSystemRun(
            object requestId, Dictionary<string, object> rpcParams)
        {
            try
            {
                string command = GetStringParam(rpcParams, "command", null);
                if (command == null || command.Trim().Length == 0)
                {
                    return JsonRpcError(requestId, ERR_INVALID_PARAMS,
                        "Missing required parameter: 'command'");
                }

                int timeoutMs = GetIntParam(rpcParams, "timeoutMs", 30000);

                RunResult runResult = _commandRunner.Execute(command, timeoutMs);

                Dictionary<string, object> result = new Dictionary<string, object>();
                result["exitCode"] = runResult.ExitCode;
                result["stdout"] = runResult.Stdout;
                result["stderr"] = runResult.Stderr;
                result["timedOut"] = runResult.TimedOut;

                return JsonRpcResult(requestId, result);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL,
                    "Command execution failed: " + ex.Message);
            }
        }

        /// <summary>
        /// device.info — Get device information.
        /// </summary>
        private Dictionary<string, object> HandleDeviceInfo(object requestId)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["deviceId"] = _deviceIdentity.DeviceId;
            result["machineName"] = _deviceIdentity.MachineName;
            result["osVersion"] = _deviceIdentity.OSVersion;
            result["userName"] = _deviceIdentity.UserName;

            return JsonRpcResult(requestId, result);
        }

        /// <summary>
        /// device.auth_token — Generate an authentication token.
        /// </summary>
        private Dictionary<string, object> HandleAuthToken(
            object requestId, Dictionary<string, object> rpcParams)
        {
            try
            {
                // nonce is accepted but not required for this implementation
                string nonce = GetStringParam(rpcParams, "nonce", string.Empty);

                string token = _deviceIdentity.GenerateAuthToken();

                Dictionary<string, object> result = new Dictionary<string, object>();
                result["token"] = token;

                return JsonRpcResult(requestId, result);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL,
                    "Token generation failed: " + ex.Message);
            }
        }

        // ───────────────────── system.notify Handler ─────────────────────

        /// <summary>
        /// system.notify — Shows a balloon notification via the system tray icon.
        /// Params: { "title": "...", "message": "...", "durationMs": 5000 }
        /// </summary>
        private Dictionary<string, object> HandleSystemNotify(
            object requestId, Dictionary<string, object> rpcParams)
        {
            try
            {
                string title = GetStringParam(rpcParams, "title", "OpenClaw Legacy Companion");
                string message = GetStringParam(rpcParams, "message", string.Empty);
                int durationMs = GetIntParam(rpcParams, "durationMs", 5000);

                if (message == null || message.Trim().Length == 0)
                {
                    message = "Notification from OpenClaw Legacy Companion";
                }

                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(durationMs, title, message, ToolTipIcon.Info);
                }

                Dictionary<string, object> result = new Dictionary<string, object>();
                result["success"] = true;

                return JsonRpcResult(requestId, result);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL,
                    "Notification failed: " + ex.Message);
            }
        }

        // ───────────────────── JSON-RPC Response Builders ─────────────────────

        /// <summary>
        /// Build a successful JSON-RPC response.
        /// </summary>
        private static Dictionary<string, object> JsonRpcResult(
            object requestId, Dictionary<string, object> resultData)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["jsonrpc"] = JSON_RPC_VERSION;
            response["result"] = resultData;
            response["id"] = requestId;
            return response;
        }

        /// <summary>
        /// Build a JSON-RPC error response.
        /// </summary>
        private static Dictionary<string, object> JsonRpcError(
            object requestId, int code, string message)
        {
            Dictionary<string, object> errorObj = new Dictionary<string, object>();
            errorObj["code"] = code;
            errorObj["message"] = message;

            Dictionary<string, object> response = new Dictionary<string, object>();
            response["jsonrpc"] = JSON_RPC_VERSION;
            response["error"] = errorObj;
            response["id"] = requestId;
            return response;
        }

        // ───────────────────── HTTP Helpers ─────────────────────

        /// <summary>
        /// Send a plain status code response (no JSON body).
        /// </summary>
        private static void SendStatus(HttpListenerContext context,
            int statusCode, string statusDescription)
        {
            context.Response.StatusCode = statusCode;
            context.Response.StatusDescription = statusDescription;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Close();
        }

        /// <summary>
        /// Send a JSON error response with the given HTTP status code.
        /// </summary>
        private static void SendJsonError(HttpListenerContext context,
            int statusCode, Dictionary<string, object> jsonPayload)
        {
            try
            {
                string json = JsonHelper.Serialize(jsonPayload);
                byte[] body = Encoding.UTF8.GetBytes(json);

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.OutputStream.Close();
            }
            catch
            {
                // Best effort
            }
        }

        // ───────────────────── Param Extractors ─────────────────────

        /// <summary>
        /// Extract an int parameter from the params dictionary, with a default value.
        /// </summary>
        private static int GetIntParam(Dictionary<string, object> rpcParams,
            string key, int defaultValue)
        {
            object val = null;
            if (rpcParams.TryGetValue(key, out val))
            {
                if (val is int)
                {
                    return (int)val;
                }

                if (val is long)
                {
                    return (int)((long)val);
                }

                if (val is double)
                {
                    return (int)Math.Round((double)val);
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Extract a string parameter from the params dictionary, with a default value.
        /// </summary>
        private static string GetStringParam(Dictionary<string, object> rpcParams,
            string key, string defaultValue)
        {
            object val = null;
            if (rpcParams.TryGetValue(key, out val))
            {
                if (val is string)
                {
                    return (string)val;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Extract a bool parameter from the params dictionary, with a default value.
        /// </summary>
        private static bool GetBoolParam(Dictionary<string, object> rpcParams,
            string key, bool defaultValue)
        {
            object val = null;
            if (rpcParams.TryGetValue(key, out val))
            {
                if (val is bool)
                {
                    return (bool)val;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Case-insensitive StartsWith helper for ASCII strings.
        /// </summary>
        private static bool StartsWithIgnoreCase(string source, string prefix)
        {
            if (source == null || prefix == null)
            {
                return false;
            }

            if (source.Length < prefix.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                char c1 = source[i];
                char c2 = prefix[i];
                if (char.ToUpperInvariant(c1) != char.ToUpperInvariant(c2))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
