using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security;

namespace WpfApp3
{
    public class TransferServer
    {
        private const int ControlPort = 42000;
        private const int SecureTransferPort = 42001;

        private const int MaxLineLength = 64 * 1024;
        private const int MaxRequestSize = 64 * 1024;

        private static readonly TimeSpan ClientAuthenticationTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan RequestReadTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan RejectionWriteTimeout =
            TimeSpan.FromSeconds(10);

        private readonly TcpListener controlListener;
        private readonly TcpListener secureTransferListener;

        private readonly X509Certificate2 serverCertificate;
        private readonly PairingManager pairingManager;

        private readonly ConcurrentDictionary<string, DateTime> processedRequests =
            new ConcurrentDictionary<string, DateTime>();

        private readonly object serverLock = new object();

        private CancellationTokenSource? serverCts;
        private bool started;

        public event Action<SslStream, TcpClient, TransferRequest>? TransferRequested;

        public event Action<SslStream, TcpClient, PairingRequest>? PairRequested;

        /// <summary>
        /// Raised when a CHAT_MESSAGE packet arrives on an authenticated TLS connection.
        /// </summary>
        public event Action<Chat.ChatMessagePacket>? ChatMessageReceived;

        public TransferServer()
        {
            serverCertificate = DeviceCertificate.GetOrCreateCertificate();
            pairingManager = new PairingManager();

            controlListener = new TcpListener(
                IPAddress.Any,
                ControlPort);

            secureTransferListener = new TcpListener(
                IPAddress.Any,
                SecureTransferPort);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (serverLock)
            {
                if (started)
                {
                    return Task.CompletedTask;
                }

                started = true;

                serverCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

                controlListener.Start();
                secureTransferListener.Start();
            }

            _ = Task.Run(
                () => ControlListenerLoopAsync(serverCts!.Token),
                CancellationToken.None);

            _ = Task.Run(
                () => SecureTransferListenerLoopAsync(serverCts!.Token),
                CancellationToken.None);

            return Task.CompletedTask;
        }

        public void Stop()
        {
            lock (serverLock)
            {
                if (!started)
                {
                    return;
                }

                started = false;

                try
                {
                    serverCts?.Cancel();
                }
                catch
                {
                }

                try
                {
                    controlListener.Stop();
                }
                catch
                {
                }

                try
                {
                    secureTransferListener.Stop();
                }
                catch
                {
                }

                try
                {
                    serverCts?.Dispose();
                }
                catch
                {
                }

                serverCts = null;
            }
        }

        private async Task ControlListenerLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await controlListener.AcceptTcpClientAsync(
                        cancellationToken);

                    _ = Task.Run(
                        () => HandleControlClientAsync(
                            client,
                            cancellationToken),
                        CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        client?.Close();
                    }
                    catch
                    {
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(
                            250,
                            CancellationToken.None);
                    }
                }
            }
        }

        private async Task SecureTransferListenerLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await secureTransferListener.AcceptTcpClientAsync(
                        cancellationToken);

                    _ = Task.Run(
                        () => HandleSecureTransferClientAsync(
                            client,
                            cancellationToken),
                        CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        client?.Close();
                    }
                    catch
                    {
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(
                            250,
                            CancellationToken.None);
                    }
                }
            }
        }

        private async Task HandleControlClientAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            NetworkStream? networkStream = null;
            SslStream? sslStream = null;

            bool connectionTransferred = false;

            try
            {
                networkStream = client.GetStream();

                sslStream = new SslStream(
                    networkStream,
                    leaveInnerStreamOpen: false,
                    UserCertificateValidationCallback);

                using CancellationTokenSource authCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                authCts.CancelAfter(ClientAuthenticationTimeout);

                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCertificate,
                        EnabledSslProtocols =
                            SslProtocols.Tls12 |
                            SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                        CertificateRevocationCheckMode =
                            X509RevocationMode.NoCheck
                    },
                    authCts.Token);

                string? line = await ReadLineWithTimeoutAsync(
                    sslStream,
                    cancellationToken,
                    RequestReadTimeout);

                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (line.Length > MaxLineLength)
                {
                    return;
                }

                PairingRequest? pairingRequest =
                    Deserialize<PairingRequest>(line);

                if (pairingRequest == null)
                {
                    return;
                }

                if (!ValidatePairingRequest(pairingRequest))
                {
                    return;
                }

                Action<SslStream, TcpClient, PairingRequest>? handler =
                    PairRequested;

                if (handler == null)
                {
                    return;
                }

                connectionTransferred = true;

                handler(
                    sslStream,
                    client,
                    pairingRequest);

                sslStream = null;
                networkStream = null;
            }
            catch
            {
                // Connection is closed by finally.
            }
            finally
            {
                if (!connectionTransferred)
                {
                    try
                    {
                        sslStream?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        networkStream?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task HandleSecureTransferClientAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            NetworkStream? networkStream = null;
            SslStream? sslStream = null;

            bool connectionTransferred = false;

            try
            {
                networkStream = client.GetStream();

                sslStream = new SslStream(
                    networkStream,
                    leaveInnerStreamOpen: false,
                    UserCertificateValidationCallback);

                using CancellationTokenSource authCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                authCts.CancelAfter(ClientAuthenticationTimeout);

                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCertificate,
                        EnabledSslProtocols =
                            SslProtocols.Tls12 |
                            SslProtocols.Tls13,
                        ClientCertificateRequired = true,
                        CertificateRevocationCheckMode =
                            X509RevocationMode.NoCheck
                    },
                    authCts.Token);

                X509Certificate2? clientCertificate =
                    sslStream.RemoteCertificate == null
                        ? null
                        : X509CertificateLoader.LoadCertificate(
                            sslStream.RemoteCertificate.Export(
                                X509ContentType.Cert));

                if (clientCertificate == null)
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Client certificate required.",
                        cancellationToken);

                    return;
                }

                string clientFingerprint =
                    GetCertificateFingerprint(clientCertificate);

                if (!pairingManager.ContainsFingerprint(
                        clientFingerprint))
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Device is not paired.",
                        cancellationToken);

                    return;
                }

                string? line = await ReadLineWithTimeoutAsync(
                    sslStream,
                    cancellationToken,
                    RequestReadTimeout);

                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (line.Length > MaxRequestSize)
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Transfer request is too large.",
                        cancellationToken);

                    return;
                }

                // Peek at the JSON to determine message type
                JsonDocument? peekedDoc = null;
                string? messageType = null;
                try
                {
                    peekedDoc = JsonDocument.Parse(line);
                    if (peekedDoc.RootElement.TryGetProperty("Type", out var typeProp))
                        messageType = typeProp.GetString();
                }
                catch { }

                // Route CHAT_MESSAGE without going through the transfer pipeline
                if (string.Equals(messageType, "CHAT_MESSAGE",
                    StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Chat.ChatMessagePacket? chatPacket =
                            JsonSerializer.Deserialize<Chat.ChatMessagePacket>(line);
                        if (chatPacket != null)
                            ChatMessageReceived?.Invoke(chatPacket);
                    }
                    catch { }
                    return;
                }

                TransferRequest? request =
                    Deserialize<TransferRequest>(line);

                if (request == null)
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Invalid transfer request.",
                        cancellationToken);

                    return;
                }

                if (!ValidateTransferRequest(request))
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Invalid transfer request.",
                        cancellationToken);

                    return;
                }

                string? pairedFingerprint =
                    pairingManager.GetFingerprint(
                        request.SenderName);

                if (string.IsNullOrWhiteSpace(pairedFingerprint) ||
                    !string.Equals(
                        pairedFingerprint,
                        clientFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Certificate does not match paired device.",
                        cancellationToken);

                    return;
                }

                if (!TryRegisterRequest(request.RequestId))
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Duplicate transfer request.",
                        cancellationToken);

                    return;
                }

                Action<SslStream, TcpClient, TransferRequest>? handler =
                    TransferRequested;

                if (handler == null)
                {
                    await SendRejectionAsync(
                        sslStream,
                        "Transfer service unavailable.",
                        cancellationToken);

                    return;
                }

                connectionTransferred = true;

                handler(
                    sslStream,
                    client,
                    request);

                sslStream = null;
                networkStream = null;
            }
            catch
            {
                // Connection is closed by finally.
            }
            finally
            {
                if (!connectionTransferred)
                {
                    try
                    {
                        sslStream?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        networkStream?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool ValidatePairingRequest(
            PairingRequest request)
        {
            if (request == null)
            {
                return false;
            }

            if (!string.Equals(
                    request.Type,
                    "PAIRING_REQUEST",
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.DeviceName))
            {
                return false;
            }

            if (request.DeviceName.Length > 100)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(
                    request.CertificateBase64))
            {
                return false;
            }

            if (request.CertificateBase64.Length > 100_000)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return false;
            }

            if (request.RequestId.Length > 100)
            {
                return false;
            }

            return true;
        }

        private bool ValidateTransferRequest(
            TransferRequest request)
        {
            if (request == null)
            {
                return false;
            }

            if (!string.Equals(
                    request.Type,
                    "TRANSFER_REQUEST",
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return false;
            }

            if (request.RequestId.Length > 100)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.FileName))
            {
                return false;
            }

            if (request.FileName.Length > 255)
            {
                return false;
            }

            if (Path.GetFileName(request.FileName) != request.FileName)
            {
                return false;
            }

            if (request.FileName.Contains(
                    Path.DirectorySeparatorChar) ||
                request.FileName.Contains(
                    Path.AltDirectorySeparatorChar))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.SenderName))
            {
                return false;
            }

            if (request.SenderName.Length > 100)
            {
                return false;
            }

            if (request.FileSize < 0)
            {
                return false;
            }

            const long MaxFileSize =
                100L * 1024L * 1024L * 1024L;

            if (request.FileSize > MaxFileSize)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Sha256))
            {
                return false;
            }

            if (request.Sha256.Length != 64)
            {
                return false;
            }

            foreach (char c in request.Sha256)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryRegisterRequest(
            string requestId)
        {
            DateTime now = DateTime.UtcNow;

            foreach (var item in processedRequests)
            {
                if ((now - item.Value) >
                    TimeSpan.FromMinutes(30))
                {
                    processedRequests.TryRemove(
                        item.Key,
                        out _);
                }
            }

            return processedRequests.TryAdd(
                requestId,
                now);
        }

        private static async Task SendRejectionAsync(
            SslStream sslStream,
            string message,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = new TransferResponse
                {
                    Type = "TRANSFER_RESPONSE",
                    RequestId = "",
                    Accepted = false
                };

                string json =
                    JsonSerializer.Serialize(response);

                using CancellationTokenSource timeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                timeoutCts.CancelAfter(
                    RejectionWriteTimeout);

                await WriteLineAsync(
                    sslStream,
                    json,
                    timeoutCts.Token);
            }
            catch
            {
            }
        }

        private static async Task<string?> ReadLineWithTimeoutAsync(
            Stream stream,
            CancellationToken cancellationToken,
            TimeSpan timeout)
        {
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(timeout);

            try
            {
                var builder = new StringBuilder();

                byte[] buffer = new byte[1];

                while (builder.Length <= MaxLineLength)
                {
                    int read = await stream.ReadAsync(
                        buffer.AsMemory(0, 1),
                        timeoutCts.Token);

                    if (read == 0)
                    {
                        break;
                    }

                    char c = (char)buffer[0];

                    if (c == '\n')
                    {
                        return builder.ToString().TrimEnd('\r');
                    }

                    builder.Append(c);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteLineAsync(
            Stream stream,
            string text,
            CancellationToken cancellationToken)
        {
            byte[] data =
                Encoding.UTF8.GetBytes(text + "\n");

            await stream.WriteAsync(
                data.AsMemory(0, data.Length),
                cancellationToken);

            await stream.FlushAsync(
                cancellationToken);
        }

        private static T? Deserialize<T>(
            string json)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            catch
            {
                return null;
            }
        }

        private static bool UserCertificateValidationCallback(
            object sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            /*
             * Certificate pinning is performed after TLS authentication
             * using the actual certificate fingerprint.
             *
             * We do not rely on the normal Windows CA trust chain because
             * LANShare uses per-device self-signed certificates.
             */

            return certificate != null;
        }

        private static string GetCertificateFingerprint(
            X509Certificate2 certificate)
        {
            byte[] hash = SHA256.HashData(
                certificate.RawData);

            return Convert.ToHexString(hash);
        }
    }

    public class PairingRequest
    {
        public string Type { get; set; } =
            "PAIRING_REQUEST";

        public string RequestId { get; set; } =
            Guid.NewGuid().ToString();

        public string DeviceName { get; set; } = "";

        public string CertificateBase64 { get; set; } = "";
    }

    public class PairingChallenge
    {
        public string Type { get; set; } =
            "PAIRING_CHALLENGE";

        public string RequestId { get; set; } = "";

        public string ChallengeBase64 { get; set; } = "";
    }

    public class PairingProof
    {
        public string Type { get; set; } =
            "PAIRING_PROOF";

        public string RequestId { get; set; } = "";

        public string SignatureBase64 { get; set; } = "";
    }

    public class PairingResponse
    {
        public string Type { get; set; } =
            "PAIRING_RESPONSE";

        public string RequestId { get; set; } = "";

        public bool Accepted { get; set; }

        public string CertificateFingerprint { get; set; } = "";

        public string Message { get; set; } = "";
    }
}