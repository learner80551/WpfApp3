using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp3
{
    public class PeerInfo
    {
        public string Name { get; set; } = "";

        public string IpAddress { get; set; } = "";

        public string CertificateFingerprint { get; set; } = "";

        public DateTime LastSeenUtc { get; set; } =
            DateTime.UtcNow;

        public bool IsOnline { get; set; } = true;
    }

    public class DiscoveryMessage
    {
        public string Type { get; set; } =
            "LANSHARE_DISCOVERY";

        public string DeviceName { get; set; } = "";

        public string IpAddress { get; set; } = "";

        public string CertificateFingerprint { get; set; } = "";

        public int TransferPort { get; set; } = 42001;

        public string RequestId { get; set; } =
            Guid.NewGuid().ToString();
    }

    public class PeerDiscovery
    {
        private const int DiscoveryPort = 42101;
        private const int DiscoveryIntervalMs = 3000;
        private const int PeerTimeoutSeconds = 10;
        private const int PeerRemoveSeconds = 30;

        private readonly object peerLock = new();

        // Certificate fingerprint is the stable device identity.
        private readonly Dictionary<string, PeerInfo> peers =
            new(StringComparer.OrdinalIgnoreCase);

        private UdpClient? udpClient;

        private CancellationTokenSource? internalCts;

        private bool isRunning;

        private string localCertificateFingerprint = "";

        public event Action<PeerInfo>? PeerFound;

        public event Action<PeerInfo>? PeerOnline;

        public event Action<PeerInfo>? PeerOffline;

        public event Action<PeerInfo>? PeerRemoved;

        public IReadOnlyList<PeerInfo> GetPeers()
        {
            lock (peerLock)
            {
                return peers.Values
                    .Select(ClonePeer)
                    .ToList();
            }
        }

        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            if (isRunning)
            {
                return;
            }

            isRunning = true;

            internalCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            try
            {
                try
                {
                    X509Certificate2 certificate =
                        DeviceCertificate.GetOrCreateCertificate();

                    localCertificateFingerprint =
                        DeviceCertificate.GetFingerprint(
                            certificate);
                }
                catch
                {
                    localCertificateFingerprint = "";
                }

                udpClient =
                    new UdpClient(
                        AddressFamily.InterNetwork);

                udpClient.EnableBroadcast = true;

                udpClient.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    true);

                udpClient.Client.Bind(
                    new IPEndPoint(
                        IPAddress.Any,
                        DiscoveryPort));

                CancellationToken token =
                    internalCts.Token;

                Task listenerTask =
                    ListenAsync(token);

                Task broadcasterTask =
                    BroadcastLoopAsync(token);

                Task cleanupTask =
                    CleanupLoopAsync(token);

                await Task.WhenAll(
                    listenerTask,
                    broadcasterTask,
                    cleanupTask);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                StopInternal();
            }
        }

        // =========================================================
        // BROADCAST LOOP
        // =========================================================

        private async Task BroadcastLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await BroadcastPresenceAsync(
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(
                        DiscoveryIntervalMs,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // =========================================================
        // SEND DISCOVERY
        // =========================================================

        private async Task BroadcastPresenceAsync(
            CancellationToken cancellationToken)
        {
            if (udpClient == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    localCertificateFingerprint))
            {
                return;
            }

            string localIp =
                GetBestLocalIPv4Address();

            if (!IsValidIPv4(localIp))
            {
                return;
            }

            DiscoveryMessage message =
                new DiscoveryMessage
                {
                    DeviceName =
                        Environment.MachineName,

                    IpAddress =
                        localIp,

                    CertificateFingerprint =
                        localCertificateFingerprint,

                    TransferPort =
                        42001,

                    RequestId =
                        Guid.NewGuid().ToString()
                };

            string json =
                JsonSerializer.Serialize(message);

            byte[] data =
                Encoding.UTF8.GetBytes(json);

            await udpClient.SendAsync(
                data,
                data.Length,
                new IPEndPoint(
                    IPAddress.Broadcast,
                    DiscoveryPort));

            cancellationToken.ThrowIfCancellationRequested();

            foreach (IPAddress broadcastAddress
                     in GetBroadcastAddresses())
            {
                if (broadcastAddress.Equals(
                        IPAddress.Broadcast))
                {
                    continue;
                }

                try
                {
                    await udpClient.SendAsync(
                        data,
                        data.Length,
                        new IPEndPoint(
                            broadcastAddress,
                            DiscoveryPort));

                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
        }

        // =========================================================
        // LISTEN
        // =========================================================

        private async Task ListenAsync(
            CancellationToken cancellationToken)
        {
            if (udpClient == null)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result =
                        await udpClient.ReceiveAsync(
                            cancellationToken);

                    ProcessDiscoveryPacket(
                        result.Buffer,
                        result.RemoteEndPoint);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        // =========================================================
        // PROCESS DISCOVERY
        // =========================================================

        private void ProcessDiscoveryPacket(
            byte[] data,
            IPEndPoint remoteEndpoint)
        {
            if (data.Length == 0 ||
                data.Length > 64 * 1024)
            {
                return;
            }

            DiscoveryMessage? message;

            try
            {
                message =
                    JsonSerializer.Deserialize<DiscoveryMessage>(
                        Encoding.UTF8.GetString(data));
            }
            catch
            {
                return;
            }

            if (message == null)
            {
                return;
            }

            if (!string.Equals(
                    message.Type,
                    "LANSHARE_DISCOVERY",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    message.DeviceName))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(
                    localCertificateFingerprint) &&
                string.Equals(
                    message.CertificateFingerprint,
                    localCertificateFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    message.CertificateFingerprint))
            {
                return;
            }

            if (!IsValidFingerprint(
                    message.CertificateFingerprint))
            {
                return;
            }

            if (!IsValidIPv4(
                    message.IpAddress))
            {
                message.IpAddress =
                    remoteEndpoint.Address.ToString();
            }

            if (!IsValidIPv4(
                    message.IpAddress))
            {
                return;
            }

            if (message.TransferPort <= 0 ||
                message.TransferPort > 65535)
            {
                return;
            }

            DateTime now =
                DateTime.UtcNow;

            PeerInfo? peerToNotify = null;

            bool isNew = false;

            bool cameBackOnline = false;

            string fingerprint =
                NormalizeFingerprint(
                    message.CertificateFingerprint);

            lock (peerLock)
            {
                if (peers.TryGetValue(
                        fingerprint,
                        out PeerInfo? existing))
                {
                    bool wasOffline =
                        !existing.IsOnline;

                    existing.Name =
                        message.DeviceName.Trim();

                    existing.IpAddress =
                        message.IpAddress.Trim();

                    existing.CertificateFingerprint =
                        fingerprint;

                    existing.LastSeenUtc =
                        now;

                    existing.IsOnline =
                        true;

                    cameBackOnline =
                        wasOffline;

                    peerToNotify =
                        ClonePeer(existing);
                }
                else
                {
                    PeerInfo newPeer =
                        new PeerInfo
                        {
                            Name =
                                message.DeviceName.Trim(),

                            IpAddress =
                                message.IpAddress.Trim(),

                            CertificateFingerprint =
                                fingerprint,

                            LastSeenUtc =
                                now,

                            IsOnline =
                                true
                        };

                    peers[fingerprint] =
                        newPeer;

                    peerToNotify =
                        ClonePeer(newPeer);

                    isNew = true;
                }
            }

            if (peerToNotify == null)
            {
                return;
            }

            if (isNew)
            {
                PeerFound?.Invoke(peerToNotify);
            }
            else if (cameBackOnline)
            {
                PeerOnline?.Invoke(peerToNotify);
                PeerFound?.Invoke(peerToNotify);
            }
            else
            {
                PeerFound?.Invoke(peerToNotify);
            }
        }

        // =========================================================
        // CLEANUP
        // =========================================================

        private async Task CleanupLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        3000,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                RemoveStalePeers();
            }
        }

        private void RemoveStalePeers()
        {
            DateTime now =
                DateTime.UtcNow;

            List<PeerInfo> offlinePeers =
                new();

            List<PeerInfo> removedPeers =
                new();

            lock (peerLock)
            {
                List<string> keysToRemove =
                    new();

                foreach (KeyValuePair<string, PeerInfo> pair
                         in peers)
                {
                    PeerInfo peer =
                        pair.Value;

                    TimeSpan age =
                        now - peer.LastSeenUtc;

                    if (age.TotalSeconds >
                        PeerTimeoutSeconds)
                    {
                        if (peer.IsOnline)
                        {
                            peer.IsOnline = false;

                            offlinePeers.Add(
                                ClonePeer(peer));
                        }

                        if (age.TotalSeconds >
                            PeerRemoveSeconds)
                        {
                            keysToRemove.Add(
                                pair.Key);

                            removedPeers.Add(
                                ClonePeer(peer));
                        }
                    }
                }

                foreach (string key in keysToRemove)
                {
                    peers.Remove(key);
                }
            }

            foreach (PeerInfo peer in offlinePeers)
            {
                PeerOffline?.Invoke(peer);
            }

            foreach (PeerInfo peer in removedPeers)
            {
                PeerRemoved?.Invoke(peer);
            }
        }

        // =========================================================
        // LOCAL ETHERNET IP
        // =========================================================

        private static string GetBestLocalIPv4Address()
        {
            try
            {
                foreach (NetworkInterface networkInterface
                         in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus !=
                        OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (networkInterface.NetworkInterfaceType !=
                        NetworkInterfaceType.Ethernet)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties =
                        networkInterface.GetIPProperties();

                    foreach (UnicastIPAddressInformation address
                             in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily ==
                            AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(
                                address.Address))
                        {
                            return address.Address.ToString();
                        }
                    }
                }
            }
            catch
            {
            }

            return "0.0.0.0";
        }

        // =========================================================
        // SUBNET BROADCAST
        // =========================================================

        private static IEnumerable<IPAddress>
            GetBroadcastAddresses()
        {
            List<IPAddress> addresses =
                new();

            try
            {
                foreach (NetworkInterface networkInterface
                         in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus !=
                        OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (networkInterface.NetworkInterfaceType !=
                        NetworkInterfaceType.Ethernet)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties =
                        networkInterface.GetIPProperties();

                    foreach (UnicastIPAddressInformation unicast
                             in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily !=
                            AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        if (unicast.IPv4Mask == null)
                        {
                            continue;
                        }

                        IPAddress broadcast =
                            CalculateBroadcastAddress(
                                unicast.Address,
                                unicast.IPv4Mask);

                        if (!addresses.Any(
                                x => x.Equals(broadcast)))
                        {
                            addresses.Add(broadcast);
                        }
                    }
                }
            }
            catch
            {
            }

            return addresses;
        }

        private static IPAddress
            CalculateBroadcastAddress(
                IPAddress address,
                IPAddress subnetMask)
        {
            byte[] ipBytes =
                address.GetAddressBytes();

            byte[] maskBytes =
                subnetMask.GetAddressBytes();

            byte[] broadcastBytes =
                new byte[4];

            for (int i = 0; i < 4; i++)
            {
                broadcastBytes[i] =
                    (byte)(
                        ipBytes[i] |
                        ~maskBytes[i]);
            }

            return new IPAddress(
                broadcastBytes);
        }

        // =========================================================
        // VALIDATION
        // =========================================================

        private static bool IsValidIPv4(
            string? address)
        {
            return
                !string.IsNullOrWhiteSpace(address) &&
                IPAddress.TryParse(
                    address,
                    out IPAddress? ip) &&
                ip.AddressFamily ==
                    AddressFamily.InterNetwork;
        }

        private static bool IsValidFingerprint(
            string fingerprint)
        {
            string normalized =
                NormalizeFingerprint(fingerprint);

            if (normalized.Length != 64)
            {
                return false;
            }

            foreach (char character in normalized)
            {
                if (!(
                    character >= '0' &&
                    character <= '9' ||
                    character >= 'A' &&
                    character <= 'F'))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeFingerprint(
            string fingerprint)
        {
            return fingerprint
                .Replace(":", "")
                .Replace(" ", "")
                .Trim()
                .ToUpperInvariant();
        }

        // =========================================================
        // CLONE
        // =========================================================

        private static PeerInfo ClonePeer(
            PeerInfo peer)
        {
            return new PeerInfo
            {
                Name =
                    peer.Name,

                IpAddress =
                    peer.IpAddress,

                CertificateFingerprint =
                    peer.CertificateFingerprint,

                LastSeenUtc =
                    peer.LastSeenUtc,

                IsOnline =
                    peer.IsOnline
            };
        }

        // =========================================================
        // STOP
        // =========================================================

        public void Stop()
        {
            StopInternal();
        }

        private void StopInternal()
        {
            if (!isRunning)
            {
                return;
            }

            isRunning = false;

            try
            {
                internalCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                udpClient?.Close();
            }
            catch
            {
            }

            udpClient = null;

            lock (peerLock)
            {
                peers.Clear();
            }
        }
    }
}