using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfApp3.Authentication;
using WpfApp3.Database;
using System.IO;
namespace WpfApp3.Chat
{
    /// <summary>
    /// Sends broadcast chat messages to all online paired peers
    /// using the existing TLS infrastructure (port 42001).
    ///
    /// Chat messages are identified by Type = "CHAT_MESSAGE" in the
    /// JSON framing, so the TransferServer can route them here.
    /// </summary>
    public class BroadcastChatService
    {
        // Raised on the thread pool when a message is received
        public event Action<ChatMessage>? MessageReceived;

        private readonly PairingManager _pairingManager;
        private readonly int _transferPort;
        private readonly ConcurrentDictionary<string, bool> _seenMessageIds = new();

        public BroadcastChatService(PairingManager pairingManager, int transferPort = 42001)
        {
            _pairingManager = pairingManager;
            _transferPort   = transferPort;
        }

        // ──────────────────────────────────────────────────
        // SEND
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Sends a chat message to all currently known online peers.
        /// Peers that are offline are silently skipped.
        /// </summary>
        public async Task SendToAllPeersAsync(
            string text,
            IEnumerable<PeerInfo> onlinePeers,
            CancellationToken cancellationToken = default)
        {
            AppSession? session = SessionManager.CurrentSession;
            if (session == null) return;

            var packet = new ChatMessagePacket
            {
                MessageId      = Guid.NewGuid().ToString(),
                SenderDeviceId = session.DeviceId,
                SenderUsername = session.Username,
                Text           = text,
                Timestamp      = DateTime.UtcNow.ToString("O")
            };

            // Save our own message locally
            var localMsg = ToMessage(packet);
            ChatRepository.Save(localMsg);
            MessageReceived?.Invoke(localMsg);

            string packetJson = JsonSerializer.Serialize(packet);

            X509Certificate2 localCert;
            try { localCert = DeviceCertificate.GetOrCreateCertificate(); }
            catch { return; }

            // Fire and forget to each peer
            foreach (PeerInfo peer in onlinePeers)
            {
                if (!peer.IsOnline) continue;
                if (string.IsNullOrWhiteSpace(peer.CertificateFingerprint)) continue;
                if (!_pairingManager.IsPaired(peer.Name, peer.CertificateFingerprint)) continue;

                string peerIp          = peer.IpAddress;
                string expectedFp      = peer.CertificateFingerprint;
                X509Certificate2 cert  = localCert;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendChatPacketAsync(
                            peerIp, expectedFp, cert,
                            packetJson, cancellationToken);
                    }
                    catch { /* Offline peers are silently skipped */ }
                }, CancellationToken.None);
            }
        }

        private async Task SendChatPacketAsync(
            string peerIp, string expectedFingerprint,
            X509Certificate2 localCert,
            string packetJson,
            CancellationToken cancellationToken)
        {
            using TcpClient client = new();
            client.NoDelay = true;

            using CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(peerIp, _transferPort, cts.Token);

            using SslStream ssl = new(
                client.GetStream(), false,
                (_, cert2, _, _) =>
                {
                    if (cert2 == null) return false;
                    try
                    {
                        using X509Certificate2 x =
                            X509CertificateLoader.LoadCertificate(
                                cert2.Export(X509ContentType.Cert));
                        string fp = x.GetCertHashString(
                            System.Security.Cryptography.HashAlgorithmName.SHA256);
                        return string.Equals(fp, expectedFingerprint,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost        = "LANShare",
                    EnabledSslProtocols =
                        SslProtocols.Tls13 | SslProtocols.Tls12,
                    ClientCertificates  = new X509CertificateCollection { localCert }
                }, cts.Token);

            using StreamWriter writer = new(ssl, Encoding.UTF8, 4096, true);
            writer.AutoFlush = false;
            await writer.WriteLineAsync(packetJson);
            await writer.FlushAsync(cts.Token);
        }

        // ──────────────────────────────────────────────────
        // RECEIVE  (called by Transfer.cs server loop)
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Called by TransferServer when a CHAT_MESSAGE packet arrives
        /// on an authenticated TLS connection.
        /// </summary>
        public void HandleIncomingPacket(ChatMessagePacket packet)
        {
            if (string.IsNullOrWhiteSpace(packet.MessageId)) return;
            if (string.IsNullOrWhiteSpace(packet.Text))      return;
            if (string.IsNullOrWhiteSpace(packet.SenderDeviceId)) return;

            // Deduplication
            if (!_seenMessageIds.TryAdd(packet.MessageId, true)) return;

            // Limit dedup cache size
            if (_seenMessageIds.Count > 500)
                _seenMessageIds.Clear();

            var msg = ToMessage(packet);
            ChatRepository.Save(msg);
            MessageReceived?.Invoke(msg);
        }

        // ──────────────────────────────────────────────────
        // HISTORY
        // ──────────────────────────────────────────────────

        public List<ChatMessage> LoadHistory() => ChatRepository.LoadRecent();

        // ──────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────

        private static ChatMessage ToMessage(ChatMessagePacket p) => new()
        {
            MessageId      = p.MessageId,
            SenderDeviceId = p.SenderDeviceId,
            SenderUsername = p.SenderUsername,
            Text           = p.Text,
            Timestamp      = DateTime.TryParse(p.Timestamp, out DateTime ts)
                             ? ts : DateTime.UtcNow
        };
    }
}
