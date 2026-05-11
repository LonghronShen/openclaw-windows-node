using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace OpenClawLegacy
{
    public class DeviceIdentity
    {
        private string _dataPath;
        private string _deviceId;
        private RSACryptoServiceProvider _rsa;

        private const string KEY_FILENAME = "device_key.xml";

        public string DeviceId
        {
            get { return _deviceId; }
        }

        public string MachineName
        {
            get { return Environment.MachineName; }
        }

        public string OSVersion
        {
            get { return Environment.OSVersion.ToString(); }
        }

        public string UserName
        {
            get { return Environment.UserName; }
        }

        public DeviceIdentity(string dataPath)
        {
            _dataPath = dataPath;
        }

        public void Initialize()
        {
            Directory.CreateDirectory(_dataPath);

            string keyFile = Path.Combine(_dataPath, KEY_FILENAME);

            if (File.Exists(keyFile))
            {
                string xml = File.ReadAllText(keyFile);
                _rsa = new RSACryptoServiceProvider(1024);
                _rsa.FromXmlString(xml);
            }
            else
            {
                _rsa = new RSACryptoServiceProvider(1024);
                string xml = _rsa.ToXmlString(true);
                File.WriteAllText(keyFile, xml);
            }

            // Compute device ID: SHA256 hash of public key XML, then take first 16 bytes as 32 hex characters
            string publicKeyXml = _rsa.ToXmlString(false);
            byte[] publicKeyBytes = Encoding.UTF8.GetBytes(publicKeyXml);

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(publicKeyBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                _deviceId = sb.ToString();
            }
        }

        /// <summary>
        /// Generates an authentication token by signing a challenge string with the RSA private key.
        /// </summary>
        public string GenerateAuthToken()
        {
            string nonce = Guid.NewGuid().ToString("N");
            string challenge = "openclaw-auth-" + _deviceId + "-" + nonce;
            byte[] challengeBytes = Encoding.UTF8.GetBytes(challenge);

            byte[] signature = _rsa.SignData(challengeBytes, new SHA256Managed());

            return Convert.ToBase64String(signature);
        }

        /// <summary>
        /// Verifies a signature against the original data using the provided RSA public key.
        /// </summary>
        /// <param name="data">The original signed data string.</param>
        /// <param name="signatureBase64">The base64-encoded signature to verify.</param>
        /// <param name="publicKeyXml">The RSA public key in XML format.</param>
        /// <returns>True if the signature is valid; otherwise false.</returns>
        public static bool VerifySignature(string data, string signatureBase64, string publicKeyXml)
        {
            try
            {
                byte[] dataBytes = Encoding.UTF8.GetBytes(data);
                byte[] signatureBytes = Convert.FromBase64String(signatureBase64);

                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(1024))
                {
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(dataBytes, new SHA256Managed(), signatureBytes);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifies a signature against data using this instance's RSA key.
        /// </summary>
        public bool VerifySignature(string data, string signatureBase64)
        {
            try
            {
                byte[] dataBytes = Encoding.UTF8.GetBytes(data);
                byte[] signatureBytes = Convert.FromBase64String(signatureBase64);

                return _rsa.VerifyData(dataBytes, new SHA256Managed(), signatureBytes);
            }
            catch
            {
                return false;
            }
        }
    }
}
