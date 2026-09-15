using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WpfApp3.Identity
{
    public static class DeviceIdentity
    {
        private static readonly string DataFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LANShare");

        private static readonly string IdFilePath =
            Path.Combine(DataFolder, "device-id.json");

        private static string? _cachedDeviceId;

        /// <summary>
        /// Returns the stable NAV-XXXXXXXX device ID for this installation.
        /// Generated once and persisted. Stable across reboots.
        /// </summary>
        public static string GetOrCreateDeviceId()
        {
            if (_cachedDeviceId != null) return _cachedDeviceId;

            Directory.CreateDirectory(DataFolder);

            if (File.Exists(IdFilePath))
            {
                try
                {
                    string json = File.ReadAllText(IdFilePath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("DeviceId", out var idEl))
                    {
                        string? id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            _cachedDeviceId = id;
                            return id;
                        }
                    }
                }
                catch { }
            }

            // Generate new device ID: NAV- + 8 uppercase hex chars
            byte[] rnd = RandomNumberGenerator.GetBytes(4);
            string suffix = Convert.ToHexString(rnd);
            string deviceId = $"NAV-{suffix}";

            // Persist
            string json2 = JsonSerializer.Serialize(
                new { DeviceId = deviceId, CreatedAt = DateTime.UtcNow.ToString("O") },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IdFilePath, json2);

            _cachedDeviceId = deviceId;
            return deviceId;
        }

        /// <summary>
        /// Returns the SHA-256 fingerprint of the local device certificate,
        /// or null if no certificate exists yet.
        /// </summary>
        public static string? GetCertificateFingerprint()
        {
            try
            {
                X509Certificate2 cert = DeviceCertificate.GetOrCreateCertificate();
                return cert.GetCertHashString(HashAlgorithmName.SHA256);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Invalidates the cached device ID (e.g., after cert rotation).
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedDeviceId = null;
        }
    }
}
