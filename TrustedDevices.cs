using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WpfApp3
{
    public static class TrustedDevices
    {
        private static readonly string FolderPath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LANShare");

        private static readonly string FilePath =
            Path.Combine(
                FolderPath,
                "trusted-devices.json");

        private static Dictionary<string, string>
            Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);

                string json =
                    File.ReadAllText(FilePath);

                return JsonSerializer.Deserialize<
                    Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void Save(
            Dictionary<string, string> devices)
        {
            Directory.CreateDirectory(FolderPath);

            string json =
                JsonSerializer.Serialize(
                    devices,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                FilePath,
                json);
        }

        public static bool IsTrusted(
            string deviceName,
            string fingerprint)
        {
            Dictionary<string, string> devices =
                Load();

            if (!devices.TryGetValue(
                    deviceName,
                    out string? savedFingerprint))
            {
                return false;
            }

            return string.Equals(
                savedFingerprint,
                fingerprint,
                StringComparison.OrdinalIgnoreCase);
        }

        public static void TrustDevice(
            string deviceName,
            string fingerprint)
        {
            Dictionary<string, string> devices =
                Load();

            devices[deviceName] =
                fingerprint;

            Save(devices);
        }

        public static bool HasDevice(
            string deviceName)
        {
            Dictionary<string, string> devices =
                Load();

            return devices.ContainsKey(deviceName);
        }
    }
}