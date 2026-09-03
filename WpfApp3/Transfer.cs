using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp3
{
    public class TransferServer
    {
        private const int TransferPort = 42000;

        private TcpListener? listener;

        public event Action<TcpClient, TransferRequest>? TransferRequested;

        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            listener = new TcpListener(
                IPAddress.Any,
                TransferPort);

            listener.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    TcpClient client =
                        await listener.AcceptTcpClientAsync(
                            cancellationToken);

                    client.NoDelay = true;

                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore temporary connection errors.
                }
            }

            listener.Stop();
        }

        private async Task HandleClientAsync(
            TcpClient client)
        {
            try
            {
                NetworkStream stream =
                    client.GetStream();

                using StreamReader reader =
                    new StreamReader(
                        stream,
                        Encoding.UTF8,
                        false,
                        4096,
                        true);

                string? json =
                    await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(json))
                {
                    client.Close();
                    return;
                }

                TransferRequest? request =
                    JsonSerializer.Deserialize<TransferRequest>(
                        json);

                if (request == null ||
                    request.Type != "TRANSFER_REQUEST")
                {
                    client.Close();
                    return;
                }

                // Keep the connection open.
                // MainWindow will send the response
                // and then receive the file.
                TransferRequested?.Invoke(
                    client,
                    request);
            }
            catch
            {
                client.Close();
            }
        }

        public void Stop()
        {
            listener?.Stop();
        }
    }
}