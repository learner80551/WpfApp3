using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp3
{
    public static class FileTransfer
    {
        private const int BufferSize = 64 * 1024;

        private static readonly TimeSpan NetworkOperationTimeout =
            TimeSpan.FromSeconds(60);

        private static readonly TimeSpan HashOperationTimeout =
            TimeSpan.FromMinutes(10);

        private const long MaxFileSize =
            100L * 1024L * 1024L * 1024L;

        public static async Task<string> CalculateSha256Async(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "File path cannot be empty.",
                    nameof(filePath));
            }

            FileInfo fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    "The file does not exist.",
                    filePath);
            }

            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(HashOperationTimeout);

            return await CalculateSha256Async(
                filePath,
                timeoutCts.Token);
        }

        public static async Task<string> CalculateSha256Async(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "File path cannot be empty.",
                    nameof(filePath));
            }

            FileInfo fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    "The file does not exist.",
                    filePath);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using FileStream stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return Convert.ToHexString(hash);
        }

        public static Task SendFileAsync(
            Stream stream,
            string filePath,
            IProgress<double>? progress = null)
        {
            return SendFileAsync(
                stream,
                filePath,
                progress,
                CancellationToken.None);
        }

        public static async Task SendFileAsync(
            Stream stream,
            string filePath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanWrite)
            {
                throw new IOException(
                    "The transfer stream is not writable.");
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "File path cannot be empty.",
                    nameof(filePath));
            }

            FileInfo fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException(
                    "The selected file no longer exists.",
                    filePath);
            }

            if (fileInfo.Length > MaxFileSize)
            {
                throw new IOException(
                    "The selected file is larger than the maximum supported transfer size.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            long expectedFileSize = fileInfo.Length;
            long totalSent = 0;

            byte[] buffer = new byte[BufferSize];

            using FileStream fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            while (totalSent < expectedFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(
                    buffer.Length,
                    expectedFileSize - totalSent);

                int bytesRead = await fileStream.ReadAsync(
                    buffer.AsMemory(0, bytesToRead),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    throw new IOException(
                        "The file ended unexpectedly during transfer.");
                }

                await WriteWithTimeoutAsync(
                    stream,
                    buffer,
                    bytesRead,
                    cancellationToken);

                totalSent += bytesRead;

                double percentage = expectedFileSize == 0
                    ? 100
                    : (double)totalSent / expectedFileSize * 100;

                progress?.Report(Math.Min(100, percentage));
            }

            if (fileStream.Position != expectedFileSize)
            {
                throw new IOException(
                    "The source file changed during transfer.");
            }

            await FlushWithTimeoutAsync(
                stream,
                cancellationToken);

            progress?.Report(100);
        }

        public static Task<string> ReceiveFileAsync(
            Stream stream,
            string filePath,
            long fileSize,
            IProgress<double>? progress = null)
        {
            return ReceiveFileAsync(
                stream,
                filePath,
                fileSize,
                progress,
                CancellationToken.None);
        }

        public static async Task<string> ReceiveFileAsync(
            Stream stream,
            string filePath,
            long fileSize,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanRead)
            {
                throw new IOException(
                    "The transfer stream is not readable.");
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "File path cannot be empty.",
                    nameof(filePath));
            }

            if (fileSize < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fileSize),
                    "File size cannot be negative.");
            }

            if (fileSize > MaxFileSize)
            {
                throw new IOException(
                    "The requested file is larger than the maximum supported transfer size.");
            }

            string fullPath = Path.GetFullPath(filePath);
            string? directory = Path.GetDirectoryName(fullPath);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);

                    string? root = Path.GetPathRoot(fullPath);

                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        DriveInfo drive = new DriveInfo(root);

                        if (drive.IsReady &&
                            drive.AvailableFreeSpace < fileSize)
                        {
                            throw new IOException(
                                "There is not enough free disk space to receive this file.");
                        }
                    }
                }

                byte[] buffer = new byte[BufferSize];
                long totalReceived = 0;

                using FileStream fileStream = new FileStream(
                    fullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);

                while (totalReceived < fileSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int bytesToRead = (int)Math.Min(
                        buffer.Length,
                        fileSize - totalReceived);

                    int bytesRead = await ReadWithTimeoutAsync(
                        stream,
                        buffer,
                        bytesToRead,
                        cancellationToken);

                    if (bytesRead == 0)
                    {
                        throw new IOException(
                            "Connection closed before the complete file was received.");
                    }

                    await fileStream.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken);

                    totalReceived += bytesRead;

                    double percentage = fileSize == 0
                        ? 100
                        : (double)totalReceived / fileSize * 100;

                    progress?.Report(Math.Min(100, percentage));
                }

                await fileStream.FlushAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (fileStream.Length != fileSize)
                {
                    throw new IOException(
                        "The received file size does not match the requested file size.");
                }

                progress?.Report(100);

                string receivedHash =
                    await CalculateSha256Async(
                        fullPath,
                        cancellationToken);

                return receivedHash;
            }
            catch
            {
                TryDeleteFile(fullPath);
                throw;
            }
        }

        private static async Task<int> ReadWithTimeoutAsync(
            Stream stream,
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(
                    NetworkOperationTimeout);

            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

            try
            {
                return await stream.ReadAsync(
                    buffer.AsMemory(0, count),
                    linkedCts.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      timeoutCts.IsCancellationRequested)
            {
                throw new IOException(
                    "The transfer timed out while waiting for data from the other device.");
            }
        }

        private static async Task WriteWithTimeoutAsync(
            Stream stream,
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(
                    NetworkOperationTimeout);

            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

            try
            {
                await stream.WriteAsync(
                    buffer.AsMemory(0, count),
                    linkedCts.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      timeoutCts.IsCancellationRequested)
            {
                throw new IOException(
                    "The transfer timed out while sending data to the other device.");
            }
        }

        private static async Task FlushWithTimeoutAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(
                    NetworkOperationTimeout);

            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

            try
            {
                await stream.FlushAsync(
                    linkedCts.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      timeoutCts.IsCancellationRequested)
            {
                throw new IOException(
                    "The transfer timed out while finishing the network operation.");
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) &&
                    File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }
    }
}
