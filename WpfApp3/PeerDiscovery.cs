using System;
using System.Net;
using System.Net.Sockets;
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
        public int Port { get; set; }
    }

    public class DiscoveryMessage
    {
        public string Type { get; set; } = "LANSHARE";
        public string Name { get; set; } = "";
        public int Port { get; set; } = 42000;
    }

    public class PeerDiscovery
    {
        private const int DiscoveryPort = 42101;
        private const int TransferPort = 42000;

        private readonly string deviceName;
        private readonly UdpClient udpClient;

        public event Action<PeerInfo>? PeerFound;

        public PeerDiscovery()
        {
            deviceName = Environment.MachineName;

            udpClient = new UdpClient();

            udpClient.EnableBroadcast = true;

            udpClient.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);

            udpClient.Client.Bind(
                new IPEndPoint(
                    IPAddress.Any,
                    DiscoveryPort));
        }

        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            _ = ListenAsync(cancellationToken);
            _ = AnnounceAsync(cancellationToken);

            await Task.CompletedTask;
        }

        private async Task AnnounceAsync(
            CancellationToken cancellationToken)
        {
            IPEndPoint broadcastEndpoint =
                new IPEndPoint(
                    IPAddress.Parse("192.168.1.255"),
                    DiscoveryPort);

            while (!cancellationToken.IsCancellationRequested)
            {
                DiscoveryMessage message =
                    new DiscoveryMessage
                    {
                        Type = "LANSHARE",
                        Name = deviceName,
                        Port = TransferPort
                    };

                string json =
                    JsonSerializer.Serialize(message);

                byte[] data =
                    Encoding.UTF8.GetBytes(json);

                try
                {
                    await udpClient.SendAsync(
                        data,
                        data.Length,
                        broadcastEndpoint);
                }
                catch
                {
                    // Ignore temporary network errors.
                }

                try
                {
                    await Task.Delay(
                        3000,
                        cancellationToken);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task ListenAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result =
                        await udpClient.ReceiveAsync(
                            cancellationToken);

                    string json =
                        Encoding.UTF8.GetString(
                            result.Buffer);

                    DiscoveryMessage? message =
                        JsonSerializer.Deserialize<DiscoveryMessage>(
                            json);

                    if (message == null)
                        continue;

                    if (message.Type != "LANSHARE")
                        continue;

                    // Ignore our own announcement.
                    if (message.Name == deviceName)
                        continue;

                    PeerInfo peer =
                        new PeerInfo
                        {
                            Name = message.Name,
                            IpAddress =
                                result.RemoteEndPoint
                                    .Address
                                    .ToString(),
                            Port = message.Port
                        };

                    PeerFound?.Invoke(peer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore invalid discovery packets.
                }
            }
        }
    }
}