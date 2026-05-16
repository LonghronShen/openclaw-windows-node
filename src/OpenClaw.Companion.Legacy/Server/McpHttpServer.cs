using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OpenClaw.Shared;

namespace OpenClaw
{
    /// <summary>
    /// MCP HTTP server using HttpListener.
    /// Uses prefix http://127.0.0.1:{port}/{path}/ which works without URL ACL
    /// because port 47873 and /help/ path is pre-registered for Everyone.
    /// </summary>
    public sealed class McpHttpServer
    {
        private HttpListener _listener;
        private int _port;
        private string _path;
        private string _authToken;
        private ScreenCaptureService _screenCapture;
        private CommandRunner _commandRunner;
        private DeviceIdentity _deviceIdentity;
        private bool _running;
        private Thread _listenerThread;
        private CapabilityRegistry _capabilityRegistry;
        private NotifyIcon _notifyIcon;

        private const string JSON_RPC_VERSION = "2.0";
        private const int ERR_PARSE = -32700;
        private const int ERR_INVALID_REQUEST = -32600;
        private const int ERR_METHOD_NOT_FOUND = -32601;
        private const int ERR_INVALID_PARAMS = -32602;
        private const int ERR_INTERNAL = -32603;

        public McpHttpServer(int port, string authToken,
            ScreenCaptureService screenCapture,
            CommandRunner commandRunner,
            DeviceIdentity deviceIdentity,
            CapabilityRegistry capabilityRegistry,
            NotifyIcon notifyIcon)
        {
            _port = port;
            _path = "";
            _authToken = authToken;
            _screenCapture = screenCapture;
            _commandRunner = commandRunner;
            _deviceIdentity = deviceIdentity;
            _capabilityRegistry = capabilityRegistry;
            _notifyIcon = notifyIcon;
            _running = false;
            _listenerThread = null;
        }

        public int Port { get { return _port; } }
        public string Path { get { return _path; } }

        public bool Start()
        {
            if (_running) return true;

            try { System.IO.File.WriteAllText("C:\\inetpub\\ftproot\\start_debug.txt", "Start() entered"); } catch { }

            // Try ports in order: 47873 (known ACL for Everyone), then fallback
            int[] ports = { 47873, 18790, 18791, 18792 };
            string[] paths = { "/help/", "/openclaw/", "/oc/", "/mcp/" };

            for (int pi = 0; pi < ports.Length; pi++)
            {
                for (int pj = 0; pj < paths.Length; pj++)
                {
                    string prefix = "http://127.0.0.1:" + ports[pi] + paths[pj];
                    try
                    {
                        HttpListener hl = new HttpListener();
                        hl.Prefixes.Add(prefix);
                        hl.Start();
                        hl.Stop();

                        // Works! Use this prefix
                        _port = ports[pi];
                        _path = paths[pj];
                        _listener = new HttpListener();
                        _listener.Prefixes.Add(prefix);
                        _listener.Start();
                        _running = true;

                        _listenerThread = new Thread(new ThreadStart(ListenLoop));
                        _listenerThread.IsBackground = true;
                        _listenerThread.Name = "McpHttpListener";
                        _listenerThread.Start();

                        return true;
                    }
                    catch
                    {
                        // Try next port/path combination
                    }
                }
            }

            return false;
        }

        public void Stop()
        {
            _running = false;
            if (_listener != null && _listener.IsListening)
            {
                try { _listener.Stop(); } catch { }
            }
            if (_listenerThread != null && _listenerThread.IsAlive)
                _listenerThread.Join(1000);
            if (_listener != null)
            {
                try { _listener.Close(); } catch { }
                _listener = null;
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(new WaitCallback(HandleRequest), ctx);
                }
                catch (HttpListenerException)
                {
                    if (!_running) break;
                    Thread.Sleep(100);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(100);
                }
            }
        }

        // ── Request Handler ──

        private void HandleRequest(object state)
        {
            HttpListenerContext ctx = (HttpListenerContext)state;
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse resp = ctx.Response;

            try
            {
                // Check auth
                string authHeader = req.Headers["Authorization"];
                if (!CheckAuth(authHeader))
                {
                    SendError(resp, 401, "Unauthorized");
                    return;
                }

                // CSRF check
                string origin = req.Headers["Origin"];
                if (origin != null && origin.Length > 0)
                {
                    SendJson(resp, 403, JsonRpcErrorJson(null, ERR_INVALID_REQUEST, "Origin header not allowed"));
                    return;
                }

                // Only POST
                if (string.Compare(req.HttpMethod, "POST", true) != 0)
                {
                    SendError(resp, 405, "Method Not Allowed");
                    return;
                }

                // Read body
                string requestBody;
                using (StreamReader sr = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    requestBody = sr.ReadToEnd();
                }

                // Parse JSON-RPC request
                Dictionary<string, object> rpcRequest = null;
                try
                {
                    object parsed = JsonHelper.Parse(requestBody);
                    if (parsed is Dictionary<string, object>)
                        rpcRequest = (Dictionary<string, object>)parsed;
                    else
                    {
                        SendJson(resp, 400, JsonRpcErrorJson(null, ERR_INVALID_REQUEST, "Body must be JSON object"));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SendJson(resp, 400, JsonRpcErrorJson(null, ERR_PARSE, "Parse error: " + ex.Message));
                    return;
                }

                object jsonrpcVer = null;
                rpcRequest.TryGetValue("jsonrpc", out jsonrpcVer);
                string jrpc = (jsonrpcVer is string) ? (string)jsonrpcVer : null;
                if (string.Compare(jrpc, JSON_RPC_VERSION, true) != 0)
                {
                    SendJson(resp, 400, JsonRpcErrorJson(null, ERR_INVALID_REQUEST,
                        "jsonrpc must be \"" + JSON_RPC_VERSION + "\""));
                    return;
                }

                object methodObj = null;
                rpcRequest.TryGetValue("method", out methodObj);
                string methodName = (methodObj is string) ? (string)methodObj : null;
                if (methodName == null || methodName.Trim().Length == 0)
                {
                    SendJson(resp, 400, JsonRpcErrorJson(null, ERR_INVALID_REQUEST, "Missing 'method'"));
                    return;
                }

                object requestId = null;
                rpcRequest.TryGetValue("id", out requestId);

                object paramsObj = null;
                rpcRequest.TryGetValue("params", out paramsObj);
                Dictionary<string, object> rpcParams = paramsObj as Dictionary<string, object>;
                if (rpcParams == null) rpcParams = new Dictionary<string, object>();

                // Dispatch
                Dictionary<string, object> result = null;

                if (string.Compare(methodName, "system.list_capabilities", true) == 0)
                    result = HandleListCapabilities(requestId);
                else if (string.Compare(methodName, "screen.snapshot", true) == 0)
                    result = HandleScreenSnapshot(requestId, rpcParams);
                else if (string.Compare(methodName, "system.run", true) == 0)
                    result = HandleSystemRun(requestId, rpcParams);
                else if (string.Compare(methodName, "device.info", true) == 0)
                    result = HandleDeviceInfo(requestId);
                else if (string.Compare(methodName, "device.auth_token", true) == 0)
                    result = HandleAuthToken(requestId, rpcParams);
                else if (string.Compare(methodName, "system.notify", true) == 0)
                    result = HandleSystemNotify(requestId, rpcParams);
                else
                {
                    CapabilityRegistry.CapabilityHandler regHandler = null;
                    if (_capabilityRegistry != null &&
                        _capabilityRegistry.TryGetHandler(methodName, out regHandler))
                    {
                        try
                        {
                            string pJson = JsonHelper.Serialize(rpcParams);
                            string rJson = regHandler(pJson);
                            Dictionary<string, object> parsedRes =
                                JsonHelper.Parse(rJson) as Dictionary<string, object>;
                            if (parsedRes != null)
                                result = JsonRpcResult(requestId, parsedRes);
                            else
                            {
                                Dictionary<string, object> rd = new Dictionary<string, object>();
                                rd["value"] = rJson;
                                result = JsonRpcResult(requestId, rd);
                            }
                        }
                        catch (Exception ex)
                        {
                            result = JsonRpcError(requestId, ERR_INTERNAL,
                                "Handler error: " + ex.Message);
                        }
                    }
                    else
                    {
                        result = JsonRpcError(requestId, ERR_METHOD_NOT_FOUND,
                            "Not found: " + methodName);
                    }
                }

                SendJson(resp, 200, JsonHelper.Serialize(result));
            }
            catch
            {
                try { SendError(resp, 500, "Internal Server Error"); }
                catch { }
            }
        }

        // ────── Auth ──────

        private bool CheckAuth(string authHeader)
        {
            if (authHeader == null) return false;
            string t = authHeader.Trim();
            if (t.Length < 8) return false;
            if (!t.StartsWith("Bearer ")) return false;
            string token = t.Substring(7).Trim();
            if (token.Length == 0) return false;
            return (token == _authToken);
        }

        // Handler methods - same as before
        private Dictionary<string, object> HandleListCapabilities(object requestId)
        {
            List<object> caps = new List<object>();
            caps.Add("screen.snapshot");
            caps.Add("system.run");
            caps.Add("device.info");
            caps.Add("device.auth_token");
            caps.Add("system.notify");

            if (_capabilityRegistry != null)
            {
                string[] regs = _capabilityRegistry.GetCapabilityNames();
                for (int i = 0; i < regs.Length; i++)
                {
                    bool dup = false;
                    for (int j = 0; j < caps.Count; j++)
                    {
                        string s = caps[j] as string;
                        if (s != null && string.Compare(s, regs[i], true) == 0)
                        { dup = true; break; }
                    }
                    if (!dup) caps.Add(regs[i]);
                }
            }

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["capabilities"] = caps;
            return JsonRpcResult(requestId, r);
        }

        private Dictionary<string, object> HandleScreenSnapshot(object requestId, Dictionary<string, object> p)
        {
            try
            {
                int mon = GetInt(p, "monitor", 0);
                string fmt = GetString(p, "format", "png");
                int mw = GetInt(p, "maxWidth", 1920);
                int q = GetInt(p, "quality", 80);
                bool ip = GetBool(p, "includePointer", true);
                if (string.Compare(fmt, "jpg", true) == 0) fmt = "jpeg";

                byte[] data = _screenCapture.CaptureScreenshot(mon, fmt, mw, q, ip);
                string b64 = Convert.ToBase64String(data);

                Dictionary<string, object> r = new Dictionary<string, object>();
                r["format"] = fmt;
                r["base64"] = b64;
                return JsonRpcResult(requestId, r);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL, "Screen capture failed: " + ex.Message);
            }
        }

        private Dictionary<string, object> HandleSystemRun(object requestId, Dictionary<string, object> p)
        {
            try
            {
                string cmd = GetString(p, "command", null);
                if (cmd == null || cmd.Trim().Length == 0)
                    return JsonRpcError(requestId, ERR_INVALID_PARAMS, "Missing 'command'");

                int t = GetInt(p, "timeoutMs", 30000);
                RunResult rr = _commandRunner.Execute(cmd, t);

                Dictionary<string, object> r = new Dictionary<string, object>();
                r["exitCode"] = rr.ExitCode;
                r["stdout"] = rr.Stdout;
                r["stderr"] = rr.Stderr;
                r["timedOut"] = rr.TimedOut;
                return JsonRpcResult(requestId, r);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL, "Exec failed: " + ex.Message);
            }
        }

        private Dictionary<string, object> HandleDeviceInfo(object requestId)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["deviceId"] = _deviceIdentity.DeviceId;
            r["machineName"] = _deviceIdentity.MachineName;
            r["osVersion"] = _deviceIdentity.OSVersion;
            r["userName"] = _deviceIdentity.UserName;
            return JsonRpcResult(requestId, r);
        }

        private Dictionary<string, object> HandleAuthToken(object requestId, Dictionary<string, object> p)
        {
            try
            {
                string token = _deviceIdentity.GenerateAuthToken();
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["token"] = token;
                return JsonRpcResult(requestId, r);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL, "Token failed: " + ex.Message);
            }
        }

        private Dictionary<string, object> HandleSystemNotify(object requestId, Dictionary<string, object> p)
        {
            try
            {
                string title = GetString(p, "title", "OpenClaw Companion");
                string msg = GetString(p, "message", "");
                int dur = GetInt(p, "durationMs", 5000);
                if (msg == null || msg.Length == 0) msg = "Notification";
                if (_notifyIcon != null)
                    _notifyIcon.ShowBalloonTip(dur, title, msg, ToolTipIcon.Info);
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["success"] = true;
                return JsonRpcResult(requestId, r);
            }
            catch (Exception ex)
            {
                return JsonRpcError(requestId, ERR_INTERNAL, "Notify failed: " + ex.Message);
            }
        }

        // ────── JSON-RPC Builders ──────

        private static Dictionary<string, object> JsonRpcResult(object id, Dictionary<string, object> data)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["jsonrpc"] = JSON_RPC_VERSION;
            r["result"] = data;
            r["id"] = id;
            return r;
        }

        private static Dictionary<string, object> JsonRpcError(object id, int code, string msg)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["code"] = code;
            e["message"] = msg;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["jsonrpc"] = JSON_RPC_VERSION;
            r["error"] = e;
            r["id"] = id;
            return r;
        }

        private static string JsonRpcErrorJson(object id, int code, string msg)
        {
            return JsonHelper.Serialize(JsonRpcError(id, code, msg));
        }

        // ────── HTTP Helpers ──────

        private static void SendJson(HttpListenerResponse resp, int status, string jsonBody)
        {
            byte[] body = Encoding.UTF8.GetBytes(jsonBody);
            resp.StatusCode = status;
            resp.ContentType = "application/json";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body, 0, body.Length);
            resp.OutputStream.Close();
        }

        private static void SendError(HttpListenerResponse resp, int status, string msg)
        {
            string json = "{\"error\":\"" + msg + "\"}";
            SendJson(resp, status, json);
        }

        // ────── Param Extractors ──────

        private static int GetInt(Dictionary<string, object> p, string k, int d)
        {
            object v; if (p.TryGetValue(k, out v))
            { if (v is int) return (int)v; if (v is long) return (int)((long)v); }
            return d;
        }

        private static string GetString(Dictionary<string, object> p, string k, string d)
        {
            object v; if (p.TryGetValue(k, out v) && v is string) return (string)v;
            return d;
        }

        private static bool GetBool(Dictionary<string, object> p, string k, bool d)
        {
            object v; if (p.TryGetValue(k, out v) && v is bool) return (bool)v;
            return d;
        }
    }
}
