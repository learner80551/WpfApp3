using System.Windows;
using WpfApp3.Authentication;
using WpfApp3.Identity;

namespace WpfApp3.UI.Login
{
    public partial class FirstRunSetupWindow : Window
    {
        public bool SetupCompleted { get; private set; }

        public FirstRunSetupWindow()
        {
            InitializeComponent();
            DeviceIdDisplay.Text = DeviceIdentity.GetOrCreateDeviceId();
            AdminUsernameBox.Focus();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            CreateButton.IsEnabled = false;

            string username = AdminUsernameBox.Text.Trim();
            string password = AdminPasswordBox.Password;
            string confirm  = ConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Admin username is required.");
                CreateButton.IsEnabled = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                ShowError("Password must be at least 8 characters.");
                CreateButton.IsEnabled = true;
                return;
            }

            if (password != confirm)
            {
                ShowError("Passwords do not match.");
                CreateButton.IsEnabled = true;
                return;
            }

            var (success, error, deviceId) =
                AuthenticationService.CreateAdmin(username, password);

            if (success)
            {
                SetupCompleted = true;
                MessageBox.Show(
                    $"Administrator account created successfully.\n\n" +
                    $"Username: {username}\n" +
                    $"Device ID: {deviceId}\n\n" +
                    "Please log in with your new credentials.",
                    "Navlan — Setup Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError(error);
                CreateButton.IsEnabled = true;
            }
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
