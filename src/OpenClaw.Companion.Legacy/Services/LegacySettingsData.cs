using System;

namespace OpenClaw
{
    public sealed class LegacySettingsData
    {
        public string GatewayUrl { get; set; }
        public string Token { get; set; }
        public bool EnableNodeMode { get; set; }

        public LegacySettingsData()
        {
            GatewayUrl = "ws://localhost:18789";
            Token = "";
            EnableNodeMode = false;
        }
    }
}
