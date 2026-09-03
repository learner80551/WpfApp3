using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WpfApp3
{
    public static class FileTransfer
    {
        private const int BufferSize = 64 * 1024;

        public static async Task SendFileAsync(
            NetworkStream stream,
            string filePath,
            IProgress<double>? progress = null)
        {
            FileInfo fileInfo = new FileInfo(filePath);

            byte[] buffer = new byte[BufferSize];

            long totalSent = 0;

            using FileStream fileStream =
                new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            int bytesRead;

            while ((bytesRead =
                await fileStream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length)) > 0)
            {
                await stream.WriteAsync(
                    buffer,
                    0,
                    bytesRead);

                totalSent += bytesRead;

                double percentage =
                    (double)totalSent /
                    fileInfo.Length *
                    100;

                progress?.Report(percentage);
            }

            await stream.FlushAsync();

            progress?.Report(100);
        }

        public static async Task ReceiveFileAsync(
            NetworkStream stream,
            string filePath,
            long fileSize,
            IProgress<double>? progress = null)
        {
            byte[] buffer = new byte[BufferSize];

            long totalReceived = 0;

            using FileStream fileStream =
                new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

            while (totalReceived < fileSize)
            {
                int bytesToRead =
                    (int)Math.Min(
                        buffer.Length,
                        fileSize - totalReceived);

                int bytesRead =
                    await stream.ReadAsync(
                        buffer,
                        0,
                        bytesToRead);

                if (bytesRead == 0)
                {
                    throw new IOException(
                        "Connection closed before the complete file was received.");
                }

                await fileStream.WriteAsync(
                    buffer,
                    0,
                    bytesRead);

                totalReceived += bytesRead;

                double percentage =
                    (double)totalReceived /
                    fileSize *
                    100;

                progress?.Report(percentage);
            }

            await fileStream.FlushAsync();

            progress?.Report(100);
        }
    }
}