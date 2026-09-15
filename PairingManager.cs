using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WpfApp3
{
    public class PairingManager
    {
        private readonly object syncLock = new();

        private readonly string storageDirectory;
        private readonly string storageFile;

        private Dictionary<string, PairedDevice> pairedDevices =
            new(StringComparer.OrdinalIgnoreCase);

        public PairingManager()
        {
            storageDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LANShare");

            storageFile = Path.Combine(
                storageDirectory,
                "paired-devices.json");

            Load();
        }

        public bool AddOrUpdate(
            string deviceName,
            string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(deviceName) ||
                string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            deviceName = deviceName.Trim();
            fingerprint = NormalizeFingerprint(fingerprint);

            lock (syncLock)
            {
                if (pairedDevices.TryGetValue(
                        deviceName,
                        out PairedDevice? existing))
                {
                    // Existing device is trusted only if its certificate
                    // fingerprint has not changed.
                    if (!string.Equals(
                            existing.Fingerprint,
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    existing.LastPairedUtc = DateTime.UtcNow;

                    Save();
                    return true;
                }

                pairedDevices[deviceName] = new PairedDevice
                {
                    DeviceName = deviceName,
                    Fingerprint = fingerprint,
                    LastPairedUtc = DateTime.UtcNow
                };

                Save();

                return true;
            }
        }

        // Checks whether the device name is paired.
        public bool IsPaired(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            lock (syncLock)
            {
                return pairedDevices.ContainsKey(
                    deviceName.Trim());
            }
        }

        // Checks whether both device name AND certificate fingerprint match.
        public bool IsPaired(
            string deviceName,
            string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(deviceName) ||
                string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            deviceName = deviceName.Trim();
            fingerprint = NormalizeFingerprint(fingerprint);

            lock (syncLock)
            {
                if (!pairedDevices.TryGetValue(
                        deviceName,
                        out PairedDevice? device))
                {
                    return false;
                }

                return string.Equals(
                    device.Fingerprint,
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool ContainsDevice(string deviceName)
        {
            return IsPaired(deviceName);
        }

        public bool ContainsFingerprint(
            string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            fingerprint = NormalizeFingerprint(fingerprint);

            lock (syncLock)
            {
                return pairedDevices.Values.Any(
                    device =>
                        string.Equals(
                            device.Fingerprint,
                            fingerprint,
                            StringComparison.OrdinalIgnoreCase));
            }
        }

        public string? GetFingerprint(
            string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return null;
            }

            lock (syncLock)
            {
                if (pairedDevices.TryGetValue(
                        deviceName.Trim(),
                        out PairedDevice? device))
                {
                    return device.Fingerprint;
                }

                return null;
            }
        }

        public List<PairedDevice> GetPairedDevices()
        {
            lock (syncLock)
            {
                return pairedDevices.Values
                    .Select(device => new PairedDevice
                    {
                        DeviceName = device.DeviceName,
                        Fingerprint = device.Fingerprint,
                        LastPairedUtc = device.LastPairedUtc
                    })
                    .ToList();
            }
        }

        public bool Remove(
            string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            lock (syncLock)
            {
                bool removed =
                    pairedDevices.Remove(
                        deviceName.Trim());

                if (removed)
                {
                    Save();
                }

                return removed;
            }
        }

        public bool Remove(
            string deviceName,
            string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(deviceName) ||
                string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            deviceName = deviceName.Trim();
            fingerprint = NormalizeFingerprint(fingerprint);

            lock (syncLock)
            {
                if (!pairedDevices.TryGetValue(
                        deviceName,
                        out PairedDevice? device))
                {
                    return false;
                }

                if (!string.Equals(
                        device.Fingerprint,
                        fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                bool removed =
                    pairedDevices.Remove(deviceName);

                if (removed)
                {
                    Save();
                }

                return removed;
            }
        }

        public void ClearAll()
        {
            lock (syncLock)
            {
                pairedDevices.Clear();
                Save();
            }
        }

        private void Load()
        {
            lock (syncLock)
            {
                try
                {
                    Directory.CreateDirectory(
                        storageDirectory);

                    if (!File.Exists(storageFile))
                    {
                        pairedDevices =
                            new Dictionary<string, PairedDevice>(
                                StringComparer.OrdinalIgnoreCase);

                        return;
                    }

                    string json =
                        File.ReadAllText(storageFile);

                    List<PairedDevice>? devices =
                        JsonSerializer.Deserialize<List<PairedDevice>>(
                            json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                    if (devices == null)
                    {
                        pairedDevices =
                            new Dictionary<string, PairedDevice>(
                                StringComparer.OrdinalIgnoreCase);

                        return;
                    }

                    var loaded =
                        new Dictionary<string, PairedDevice>(
                            StringComparer.OrdinalIgnoreCase);

                    foreach (PairedDevice device in devices)
                    {
                        if (string.IsNullOrWhiteSpace(
                                device.DeviceName) ||
                            string.IsNullOrWhiteSpace(
                                device.Fingerprint))
                        {
                            continue;
                        }

                        device.DeviceName =
                            device.DeviceName.Trim();

                        device.Fingerprint =
                            NormalizeFingerprint(
                                device.Fingerprint);

                        loaded[device.DeviceName] =
                            device;
                    }

                    pairedDevices = loaded;
                }
                catch
                {
                    // Fail closed if the trust database cannot be loaded.
                    pairedDevices =
                        new Dictionary<string, PairedDevice>(
                            StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(
                    storageDirectory);

                List<PairedDevice> devices =
                    pairedDevices.Values
                        .Select(device => new PairedDevice
                        {
                            DeviceName = device.DeviceName,
                            Fingerprint = device.Fingerprint,
                            LastPairedUtc = device.LastPairedUtc
                        })
                        .ToList();

                string json =
                    JsonSerializer.Serialize(
                        devices,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                string temporaryFile =
                    storageFile + ".tmp";

                File.WriteAllText(
                    temporaryFile,
                    json);

                if (File.Exists(storageFile))
                {
                    File.Delete(storageFile);
                }

                File.Move(
                    temporaryFile,
                    storageFile);
            }
            catch
            {
                // Keep the in-memory state if persistence fails.
            }
        }

        private static string NormalizeFingerprint(
            string fingerprint)
        {
            return fingerprint
                .Replace(":", "")
                .Replace("-", "")
                .Replace(" ", "")
                .Trim()
                .ToUpperInvariant();
        }
    }

    public class PairedDevice
    {
        public string DeviceName { get; set; } = "";

        public string Fingerprint { get; set; } = "";

        public DateTime LastPairedUtc { get; set; }
    }
}