using System;
using System.Collections.Generic;
using System.IO;

namespace OpenClaw
{
    public sealed class LegacySettingsManager
    {
        private readonly string _settingsPath;

        public string GatewayUrl { get; set; }
        public string Token { get; set; }
        public bool EnableNodeMode { get; set; }

        public string SettingsPath
        {
            get { return _settingsPath; }
        }

        public LegacySettingsManager(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath))
            {
                throw new ArgumentNullException("dataPath");
            }

            _settingsPath = Path.Combine(dataPath, "settings.json");
            GatewayUrl = "ws://localhost:18789";
            Token = "";
            EnableNodeMode = false;
            Load();
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return;
                }

                string json = File.ReadAllText(_settingsPath);
                object parsed = JsonHelper.Parse(json);
                Dictionary<string, object> dict = parsed as Dictionary<string, object>;
                if (dict == null)
                {
                    return;
                }

                object value;
                if (dict.TryGetValue("GatewayUrl", out value) && value is string)
                {
                    GatewayUrl = (string)value;
                }
                if (dict.TryGetValue("Token", out value) && value is string)
                {
                    Token = (string)value;
                }
                if (dict.TryGetValue("EnableNodeMode", out value) && value is bool)
                {
                    EnableNodeMode = (bool)value;
                }
            }
            catch
            {
                // Ignore malformed settings and keep defaults.
            }
        }

        public void Save()
        {
            string dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Dictionary<string, object> dict = new Dictionary<string, object>();
            dict["GatewayUrl"] = GatewayUrl ?? "";
            dict["Token"] = Token ?? "";
            dict["EnableNodeMode"] = EnableNodeMode;

            File.WriteAllText(_settingsPath, JsonHelper.Serialize(dict));
        }
    }
}
