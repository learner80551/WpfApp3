using System.Windows;

namespace WpfApp3
{
    public partial class TransferRequestWindow : Window
    {
        public bool Accepted { get; private set; }

        public TransferRequestWindow(
            TransferRequest request)
        {
            InitializeComponent();

            SenderText.Text =
                $"Sender: {request.SenderName}";

            FileNameText.Text =
                $"File: {request.FileName}";

            FileSizeText.Text =
                $"Size: {FormatFileSize(request.FileSize)}";
        }

        private void AcceptButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Accepted = true;

            DialogResult = true;
        }

        private void RejectButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Accepted = false;

            DialogResult = false;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";

            if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";

            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }
}