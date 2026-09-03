using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WpfApp3
{
    public partial class MainWindow : Window
    {
        private string? selectedFile;

        private readonly CancellationTokenSource discoveryCts = new();
        private readonly CancellationTokenSource transferCts = new();

        private readonly PeerDiscovery peerDiscovery = new();
        private readonly TransferServer transferServer = new();

        public MainWindow()
        {
            InitializeComponent();

            TransferProgress.Value = 0;
            ProgressText.Text = "0%";

            peerDiscovery.PeerFound += PeerDiscovery_PeerFound;

            _ = peerDiscovery.StartAsync(
                discoveryCts.Token);

            transferServer.TransferRequested +=
                TransferServer_TransferRequested;

            _ = transferServer.StartAsync(
                transferCts.Token);

            StatusText.Text =
                "Searching for nearby devices...";
        }

        private void PeerDiscovery_PeerFound(PeerInfo peer)
        {
            Dispatcher.Invoke(() =>
            {
                string deviceText =
                    $"{peer.Name} - {peer.IpAddress}";

                foreach (var item in DevicesList.Items)
                {
                    if (item is string existing &&
                        existing == deviceText)
                    {
                        return;
                    }
                }

                DevicesList.Items.Add(deviceText);

                StatusText.Text =
                    "Nearby device found.";
            });
        }

        private void DevicesList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            UpdateSendButton();
        }

        private void SelectFileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenFileDialog dialog =
                new OpenFileDialog();

            if (dialog.ShowDialog() == true)
            {
                selectedFile = dialog.FileName;

                StatusText.Text =
                    $"Selected: {Path.GetFileName(selectedFile)}";

                UpdateSendButton();
            }
        }

        private async void SendButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedFile))
            {
                MessageBox.Show(
                    "Please select a file first.",
                    "LAN Transfer");

                return;
            }

            if (DevicesList.SelectedItem == null)
            {
                MessageBox.Show(
                    "Please select a nearby device.",
                    "LAN Transfer");

                return;
            }

            try
            {
                SendButton.IsEnabled = false;
                SelectFileButton.IsEnabled = false;

                TransferProgress.Value = 0;
                ProgressText.Text = "0%";

                string selectedDevice =
                    DevicesList.SelectedItem.ToString() ?? "";

                string[] parts =
                    selectedDevice.Split(
                        " - ",
                        StringSplitOptions.None);

                if (parts.Length != 2)
                {
                    MessageBox.Show(
                        "Invalid device information.",
                        "LAN Transfer");

                    return;
                }

                string targetIp = parts[1];

                FileInfo fileInfo =
                    new FileInfo(selectedFile);

                TransferRequest request =
                    new TransferRequest
                    {
                        FileName = fileInfo.Name,
                        FileSize = fileInfo.Length,
                        SenderName = Environment.MachineName
                    };

                using TcpClient client =
                    new TcpClient();

                client.NoDelay = true;

                StatusText.Text =
                    $"Connecting to {parts[0]}...";

                await client.ConnectAsync(
                    targetIp,
                    42000);

                using NetworkStream stream =
                    client.GetStream();

                using StreamReader reader =
                    new StreamReader(
                        stream,
                        Encoding.UTF8,
                        false,
                        4096,
                        true);

                using StreamWriter writer =
                    new StreamWriter(
                        stream,
                        Encoding.UTF8,
                        4096,
                        true);

                string json =
                    JsonSerializer.Serialize(request);

                await writer.WriteLineAsync(json);
                await writer.FlushAsync();

                StatusText.Text =
                    "Waiting for receiver response...";

                string? responseJson =
                    await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(responseJson))
                {
                    StatusText.Text =
                        "No response from receiver.";

                    return;
                }

                TransferResponse? response =
                    JsonSerializer.Deserialize<TransferResponse>(
                        responseJson);

                if (response == null)
                {
                    StatusText.Text =
                        "Invalid receiver response.";

                    return;
                }

                if (!response.Accepted)
                {
                    StatusText.Text =
                        "Transfer rejected.";

                    MessageBox.Show(
                        "The receiver rejected the transfer.",
                        "LAN Transfer");

                    return;
                }

                StatusText.Text =
                    "Transfer accepted. Sending file...";

                var progress =
                    new Progress<double>(
                        percentage =>
                        {
                            TransferProgress.Value =
                                percentage;

                            ProgressText.Text =
                                $"{percentage:F0}%";
                        });

                await FileTransfer.SendFileAsync(
                    stream,
                    selectedFile,
                    progress);

                TransferProgress.Value = 100;
                ProgressText.Text = "100%";

                StatusText.Text =
                    "File transfer completed.";

                MessageBox.Show(
                    $"File sent successfully.\n\n" +
                    $"{fileInfo.Name}",
                    "LAN Transfer");
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "File transfer failed.";

                TransferProgress.Value = 0;
                ProgressText.Text = "0%";

                MessageBox.Show(
                    "Could not transfer the file.\n\n" +
                    ex.Message,
                    "LAN Transfer");
            }
            finally
            {
                SelectFileButton.IsEnabled = true;
                UpdateSendButton();
            }
        }

        private void UpdateSendButton()
        {
            SendButton.IsEnabled =
                !string.IsNullOrEmpty(selectedFile) &&
                DevicesList.SelectedItem != null;
        }

        private void TransferServer_TransferRequested(
            TcpClient client,
            TransferRequest request)
        {
            _ = HandleTransferRequestAsync(
                client,
                request);
        }

        private async System.Threading.Tasks.Task
            HandleTransferRequestAsync(
                TcpClient client,
                TransferRequest request)
        {
            bool accepted = false;

            Dispatcher.Invoke(() =>
            {
                TransferRequestWindow dialog =
                    new TransferRequestWindow(request);

                dialog.Owner = this;

                dialog.ShowDialog();

                accepted = dialog.Accepted;
            });

            try
            {
                NetworkStream stream =
                    client.GetStream();

                using StreamWriter writer =
                    new StreamWriter(
                        stream,
                        Encoding.UTF8,
                        4096,
                        true);

                TransferResponse response =
                    new TransferResponse
                    {
                        RequestId = request.RequestId,
                        Accepted = accepted
                    };

                string json =
                    JsonSerializer.Serialize(response);

                await writer.WriteLineAsync(json);
                await writer.FlushAsync();

                if (!accepted)
                {
                    client.Close();
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    TransferProgress.Value = 0;
                    ProgressText.Text = "0%";

                    StatusText.Text =
                        $"Receiving {request.FileName}...";
                });

                string downloadFolder =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile),
                        "Downloads",
                        "LAN Transfer");

                Directory.CreateDirectory(
                    downloadFolder);

                string safeFileName =
                    Path.GetFileName(request.FileName);

                string destinationPath =
                    GetUniqueFilePath(
                        downloadFolder,
                        safeFileName);

                var progress =
                    new Progress<double>(
                        percentage =>
                        {
                            TransferProgress.Value =
                                percentage;

                            ProgressText.Text =
                                $"{percentage:F0}%";
                        });

                await FileTransfer.ReceiveFileAsync(
                    stream,
                    destinationPath,
                    request.FileSize,
                    progress);

                Dispatcher.Invoke(() =>
                {
                    TransferProgress.Value = 100;
                    ProgressText.Text = "100%";

                    StatusText.Text =
                        "File received successfully.";

                    MessageBox.Show(
                        $"File received successfully.\n\n" +
                        $"Saved to:\n{destinationPath}",
                        "LAN Transfer");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        "File receive failed.";

                    TransferProgress.Value = 0;
                    ProgressText.Text = "0%";

                    MessageBox.Show(
                        "Could not receive the file.\n\n" +
                        ex.Message,
                        "LAN Transfer");
                });
            }
            finally
            {
                client.Close();
            }
        }

        private static string GetUniqueFilePath(
            string folder,
            string fileName)
        {
            string path =
                Path.Combine(
                    folder,
                    fileName);

            if (!File.Exists(path))
                return path;

            string name =
                Path.GetFileNameWithoutExtension(
                    fileName);

            string extension =
                Path.GetExtension(fileName);

            int counter = 1;

            while (true)
            {
                string newPath =
                    Path.Combine(
                        folder,
                        $"{name} ({counter}){extension}");

                if (!File.Exists(newPath))
                    return newPath;

                counter++;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            discoveryCts.Cancel();
            transferCts.Cancel();

            transferServer.Stop();

            base.OnClosed(e);
        }
    }
}