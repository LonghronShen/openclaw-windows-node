#if NET10_0
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace OpenClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using Ed25519
/// </summary>
public class DeviceIdentity
{
    private readonly string _keyPath;
    private readonly IOpenClawLogger _logger;
    private Key? _privateKey;
    private PublicKey? _publicKey;
    private string? _deviceId;
    private string? _deviceToken;
    
    private static readonly SignatureAlgorithm Ed25519Algorithm = SignatureAlgorithm.Ed25519;
    
    public string DeviceId => _deviceId ?? throw new InvalidOperationException("Device not initialized");
    public string PublicKeyBase64Url => _publicKey != null ? Base64UrlEncode(_publicKey.Export(KeyBlobFormat.RawPublicKey)) : throw new InvalidOperationException("Device not initialized");
    public string? DeviceToken => _deviceToken;

    public static string? TryReadStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null)
    {
        var keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        if (!File.Exists(keyPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(keyPath));
            if (doc.RootElement.TryGetProperty(nameof(DeviceKeyData.DeviceToken), out var deviceToken) &&
                deviceToken.ValueKind == JsonValueKind.String)
            {
                var value = deviceToken.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Failed to read stored device token: {ex.Message}");
        }

        return null;
    }

    public static bool HasStoredDeviceToken(string dataPath, IOpenClawLogger? logger = null) =>
        !string.IsNullOrWhiteSpace(TryReadStoredDeviceToken(dataPath, logger));
    
    public DeviceIdentity(string dataPath, IOpenClawLogger? logger = null)
    {
        _keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        _logger = logger ?? NullLogger.Instance;
    }
    
    /// <summary>
    /// Initialize the device identity - loads existing or generates new keypair
    /// </summary>
    public void Initialize()
    {
        if (File.Exists(_keyPath))
        {
            LoadExisting();
        }
        else
        {
            GenerateNew();
        }
    }
    
    private void LoadExisting()
    {
        try
        {
            var json = File.ReadAllText(_keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
            
            if (data == null || string.IsNullOrEmpty(data.PrivateKeyBase64))
            {
                _logger.Warn("Invalid device key file, generating new");
                GenerateNew();
                return;
            }
            
            var privateKeyBytes = Convert.FromBase64String(data.PrivateKeyBase64);
            _privateKey = Key.Import(Ed25519Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
            _publicKey = _privateKey.PublicKey;
            _deviceId = data.DeviceId;
            _deviceToken = data.DeviceToken;
            
            _logger.Info($"Loaded Ed25519 device identity: {_deviceId?[..16]}...");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load device key: {ex.Message}");
            GenerateNew();
        }
    }
    
    private void GenerateNew()
    {
        _logger.Info("Generating new Ed25519 device keypair...");
        
        // Generate Ed25519 keypair using NSec
        _privateKey = Key.Create(Ed25519Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _publicKey = _privateKey.PublicKey;
        
        // Get raw 32-byte public key
        var publicKeyBytes = _publicKey.Export(KeyBlobFormat.RawPublicKey);
        
        // Device ID is SHA256 hash of raw 32-byte public key (hex encoded)
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(publicKeyBytes);
        _deviceId = Convert.ToHexString(hashBytes).ToLowerInvariant();
        
        // Export private key for storage
        var privateKeyBytes = _privateKey.Export(KeyBlobFormat.RawPrivateKey);
        
        // Save to disk
        var data = new DeviceKeyData
        {
            PrivateKeyBase64 = Convert.ToBase64String(privateKeyBytes),
            PublicKeyBase64 = Convert.ToBase64String(publicKeyBytes),
            DeviceId = _deviceId,
            Algorithm = "Ed25519",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        
        File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        _logger.Info($"Generated new Ed25519 device identity: {_deviceId}");
    }
    
    /// <summary>
    /// Sign a payload for device authentication
    /// Payload format: v2|{deviceId}|{client.id}|{client.mode}|{role}|{scopes}|{signedAtMs}|{token}|{nonce}
    /// IMPORTANT: {token} is the auth.token from the connect request, NOT the device token!
    /// </summary>
    public string SignPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_privateKey == null || _deviceId == null)
            throw new InvalidOperationException("Device not initialized");
        
        // Build the payload to sign
        var payload = BuildDebugPayload(nonce, signedAtMs, clientId, authToken);
        
        // Sign with Ed25519
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        
        // Return base64url encoded signature
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Sign a v3 connect payload for operator/client connections.
    /// Format: v3|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}|{platform}|{deviceFamily}
    /// </summary>
    public string SignConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (_privateKey == null)
            throw new InvalidOperationException("Device not initialized");

        var payload = BuildConnectPayloadV3(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken,
            platform,
            deviceFamily);

        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Build the v3 connect payload string for signing/debugging.
    /// Format: v3|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}|{platform}|{deviceFamily}
    /// </summary>
    public string BuildConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        string scopesCsv;
        {
            var scopeList = scopes ?? Array.Empty<string>();
            var sb = new StringBuilder();
            bool first = true;
            foreach (var s in scopeList)
            {
                if (!first) sb.Append(',');
                sb.Append(s ?? string.Empty);
                first = false;
            }
            scopesCsv = sb.ToString();
        }
        var safeToken = authToken ?? string.Empty;
        var safeNonce = nonce ?? string.Empty;

        return $"v3|{_deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{safeToken}|{safeNonce}|{platform}|{deviceFamily}";
    }

    /// <summary>
    /// Sign a v2 connect payload for compatibility mode.
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}
    /// </summary>
    public string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken)
    {
        if (_privateKey == null)
            throw new InvalidOperationException("Device not initialized");

        var payload = BuildConnectPayloadV2(
            nonce,
            signedAtMs,
            clientId,
            clientMode,
            role,
            scopes,
            authToken);

        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Build the v2 connect payload string for signing/debugging.
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{tokenOrEmpty}|{nonce}
    /// </summary>
    public string BuildConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        IEnumerable<string> scopes,
        string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        string scopesCsv;
        {
            var scopeList = scopes ?? Array.Empty<string>();
            var sb = new StringBuilder();
            bool first = true;
            foreach (var s in scopeList)
            {
                if (!first) sb.Append(',');
                sb.Append(s ?? string.Empty);
                first = false;
            }
            scopesCsv = sb.ToString();
        }
        var safeToken = authToken ?? string.Empty;
        var safeNonce = nonce ?? string.Empty;

        return $"v2|{_deviceId}|{clientId}|{clientMode}|{role}|{scopesCsv}|{signedAtMs}|{safeToken}|{safeNonce}";
    }
    
    /// <summary>
    /// Build the payload string (for debugging)
    /// Format: v2|{deviceId}|{clientId}|{clientMode}|{role}||{signedAtMs}|{token}|{nonce}
    /// IMPORTANT: {token} is the auth.token from connect request!
    /// </summary>
    public string BuildDebugPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");
            
        // - clientId must match client.id in connect request
        // - clientMode = "node"
        // - role = "node" 
        // - scopes = empty
        // - token = the auth.token being used in the connect request
        return $"v2|{_deviceId}|{clientId}|node|node||{signedAtMs}|{authToken}|{nonce}";
    }
    
    /// <summary>
    /// Store the device token received after pairing approval
    /// </summary>
    public void StoreDeviceToken(string token)
    {
        _deviceToken = token;
        
        // Update the key file with the token
        try
        {
            if (File.Exists(_keyPath))
            {
                var json = File.ReadAllText(_keyPath);
                var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
                if (data != null)
                {
                    data.DeviceToken = token;
                    File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                    _logger.Info("Device token stored");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to store device token: {ex.Message}");
        }
    }
    
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
    
    private class DeviceKeyData
    {
        public string? PrivateKeyBase64 { get; set; }
        public string? PublicKeyBase64 { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceToken { get; set; }
        public string? Algorithm { get; set; }
        public long CreatedAt { get; set; }
    }
}
#elif NET20
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace OpenClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using RSA 1024-bit
/// (legacy companion compatible).
/// </summary>
public class DeviceIdentity
{
    private readonly string _dataPath;
    private readonly IOpenClawLogger _logger;
    private string _deviceId = "";
    private RSACryptoServiceProvider _rsa;

    private static readonly string KeyFilename = "device_key.xml";

    public string DeviceId
    {
        get
        {
            if (string.IsNullOrEmpty(_deviceId))
                throw new InvalidOperationException("Device not initialized");
            return _deviceId;
        }
    }

    public string PublicKeyBase64Url
    {
        get
        {
            if (_rsa == null)
                throw new InvalidOperationException("Device not initialized");
            string publicKeyXml = _rsa.ToXmlString(false);
            byte[] publicKeyBytes = Encoding.UTF8.GetBytes(publicKeyXml);
            return Convert.ToBase64String(publicKeyBytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }

    public string DeviceToken { get; private set; }

    public static string TryReadStoredDeviceToken(string dataPath, IOpenClawLogger logger)
    {
        string keyPath = Path.Combine(dataPath, "device_key.xml");
        if (!File.Exists(keyPath))
            return null;

        try
        {
            // Legacy token: stored as a sidecar file next to the key
            string tokenPath = Path.Combine(dataPath, "device_token.txt");
            if (File.Exists(tokenPath))
            {
                string value = File.ReadAllText(tokenPath).Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (IOException ex)
        {
            logger.Warn("Failed to read stored device token: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.Warn("Failed to read stored device token: " + ex.Message);
        }

        return null;
    }

    public static bool HasStoredDeviceToken(string dataPath, IOpenClawLogger logger) =>
        !string.IsNullOrEmpty(TryReadStoredDeviceToken(dataPath, logger));

    public DeviceIdentity(string dataPath, IOpenClawLogger logger)
    {
        _dataPath = dataPath;
        _logger = logger;
    }

    /// <summary>
    /// Initialize the device identity - loads existing or generates new keypair
    /// </summary>
    public void Initialize()
    {
        string keyFile = Path.Combine(_dataPath, KeyFilename);

        if (File.Exists(keyFile))
        {
            LoadExisting(keyFile);
        }
        else
        {
            GenerateNew(keyFile);
        }

        // Try loading device token
        string tokenPath = Path.Combine(_dataPath, "device_token.txt");
        if (File.Exists(tokenPath))
        {
            DeviceToken = File.ReadAllText(tokenPath).Trim();
        }
    }

    private void LoadExisting(string keyFile)
    {
        try
        {
            string xml = File.ReadAllText(keyFile);
            _rsa = new RSACryptoServiceProvider(1024);
            _rsa.FromXmlString(xml);

            // Compute device ID from public key
            _deviceId = ComputeDeviceId(_rsa);
            _logger.Info("Loaded RSA 1024-bit device identity: " + _deviceId.Substring(0, Math.Min(16, _deviceId.Length)) + "...");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load device key: " + ex.Message);
            GenerateNew(keyFile);
        }
    }

    private void GenerateNew(string keyFile)
    {
        _logger.Info("Generating new RSA 1024-bit device keypair...");

        _rsa = new RSACryptoServiceProvider(1024);

        // Save private key to disk
        string xml = _rsa.ToXmlString(true);
        string dir = Path.GetDirectoryName(keyFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(keyFile, xml);

        // Compute device ID
        _deviceId = ComputeDeviceId(_rsa);
        _logger.Info("Generated new RSA 1024-bit device identity: " + _deviceId);
    }

    private static string ComputeDeviceId(RSACryptoServiceProvider rsa)
    {
        string publicKeyXml = rsa.ToXmlString(false);
        byte[] publicKeyBytes = Encoding.UTF8.GetBytes(publicKeyXml);

        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(publicKeyBytes);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Sign a payload for device authentication (RSA-SHA256).
    /// </summary>
    public string SignPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_rsa == null || string.IsNullOrEmpty(_deviceId))
            throw new InvalidOperationException("Device not initialized");

        string payload = BuildDebugPayload(nonce, signedAtMs, clientId, authToken);
        byte[] dataBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = _rsa.SignData(dataBytes, new SHA256Managed());
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Sign a v3 connect payload (RSA-SHA256).
    /// </summary>
    public string SignConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        string[] scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (_rsa == null)
            throw new InvalidOperationException("Device not initialized");

        string payload = BuildConnectPayloadV3(
            nonce, signedAtMs, clientId, clientMode, role, scopes, authToken, platform, deviceFamily);

        byte[] dataBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = _rsa.SignData(dataBytes, new SHA256Managed());
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Build the v3 connect payload string for signing/debugging.
    /// </summary>
    public string BuildConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        string[] scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        if (string.IsNullOrEmpty(_deviceId))
            throw new InvalidOperationException("Device not initialized");

        string scopesCsv = JoinStrings(scopes, ",");
        string safeToken = authToken ?? string.Empty;
        string safeNonce = nonce ?? string.Empty;

        return "v3|" + _deviceId + "|" + clientId + "|" + clientMode + "|" + role + "|" + scopesCsv + "|" + signedAtMs + "|" + safeToken + "|" + safeNonce + "|" + platform + "|" + deviceFamily;
    }

    /// <summary>
    /// Sign a v2 connect payload (RSA-SHA256).
    /// </summary>
    public string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        string[] scopes,
        string authToken)
    {
        if (_rsa == null)
            throw new InvalidOperationException("Device not initialized");

        string payload = BuildConnectPayloadV2(
            nonce, signedAtMs, clientId, clientMode, role, scopes, authToken);

        byte[] dataBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = _rsa.SignData(dataBytes, new SHA256Managed());
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Build the v2 connect payload string for signing/debugging.
    /// </summary>
    public string BuildConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        string[] scopes,
        string authToken)
    {
        if (string.IsNullOrEmpty(_deviceId))
            throw new InvalidOperationException("Device not initialized");

        string scopesCsv = JoinStrings(scopes, ",");
        string safeToken = authToken ?? string.Empty;
        string safeNonce = nonce ?? string.Empty;

        return "v2|" + _deviceId + "|" + clientId + "|" + clientMode + "|" + role + "|" + scopesCsv + "|" + signedAtMs + "|" + safeToken + "|" + safeNonce;
    }

    /// <summary>
    /// Build the debug payload string.
    /// </summary>
    public string BuildDebugPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (string.IsNullOrEmpty(_deviceId))
            throw new InvalidOperationException("Device not initialized");

        return "v2|" + _deviceId + "|" + clientId + "|node|node||" + signedAtMs + "|" + authToken + "|" + nonce;
    }

    /// <summary>
    /// Store the device token received after pairing approval
    /// </summary>
    public void StoreDeviceToken(string token)
    {
        DeviceToken = token;

        try
        {
            string tokenPath = Path.Combine(_dataPath, "device_token.txt");
            if (!Directory.Exists(_dataPath))
                Directory.CreateDirectory(_dataPath);
            File.WriteAllText(tokenPath, token);
            _logger.Info("Device token stored");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to store device token: " + ex.Message);
        }
    }

    /// <summary>
    /// Verify a signature using this device's public key.
    /// </summary>
    public bool VerifySignature(string data, string signatureBase64)
    {
        try
        {
            if (_rsa == null)
                return false;

            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            byte[] signatureBytes = Convert.FromBase64String(signatureBase64);
            return _rsa.VerifyData(dataBytes, new SHA256Managed(), signatureBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Join strings with a separator (replacement for string.Join for net20 compatibility).
    /// </summary>
    private static string JoinStrings(string[] values, string separator)
    {
        if (values == null || values.Length == 0)
            return string.Empty;

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(separator);
            sb.Append(values[i] ?? string.Empty);
        }
        return sb.ToString();
    }

    #if NET10_0
    // Stub for the net10.0 overload with IEnumerable<string>
    public string SignConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        System.Collections.Generic.IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        return SignConnectPayloadV3(nonce, signedAtMs, clientId, clientMode, role, ToArray(scopes), authToken, platform, deviceFamily);
    }

    public string SignConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        System.Collections.Generic.IEnumerable<string> scopes,
        string authToken)
    {
        return SignConnectPayloadV2(nonce, signedAtMs, clientId, clientMode, role, ToArray(scopes), authToken);
    }

    public string BuildConnectPayloadV3(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        System.Collections.Generic.IEnumerable<string> scopes,
        string authToken,
        string platform,
        string deviceFamily)
    {
        return BuildConnectPayloadV3(nonce, signedAtMs, clientId, clientMode, role, ToArray(scopes), authToken, platform, deviceFamily);
    }

    public string BuildConnectPayloadV2(
        string nonce,
        long signedAtMs,
        string clientId,
        string clientMode,
        string role,
        System.Collections.Generic.IEnumerable<string> scopes,
        string authToken)
    {
        return BuildConnectPayloadV2(nonce, signedAtMs, clientId, clientMode, role, ToArray(scopes), authToken);
    }

    private static string[] ToArray(System.Collections.Generic.IEnumerable<string> items)
    {
        if (items == null) return new string[0];
        var list = new System.Collections.Generic.List<string>(items);
        return list.ToArray();
    }
    #endif
}
#endif
