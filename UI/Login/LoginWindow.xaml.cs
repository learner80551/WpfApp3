using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfApp3.Authentication;
using WpfApp3.Identity;
using WpfApp3.Theme;

namespace WpfApp3.UI.Login
{
    public partial class LoginWindow : Window
    {
        public bool LoginSucceeded { get; private set; }
        public bool ForcePasswordReset { get; private set; }

        public LoginWindow()
        {
            InitializeComponent();

            // Display device ID
            DeviceIdText.Text = DeviceIdentity.GetOrCreateDeviceId();

            // Sync theme toggle state
            ThemeToggleBtn.IsChecked = !ThemeManager.IsDark;
            UsernameBox.Focus();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            DoLogin();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                DoLogin();
        }

        private void DoLogin()
        {
            ErrorText.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = false;

            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;
            string deviceId = DeviceIdentity.GetOrCreateDeviceId();

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                ShowError("Please enter your username and password.");
                LoginButton.IsEnabled = true;
                return;
            }

            LoginResult result = AuthenticationService.Login(username, password, deviceId);

            if (result.IsSuccess)
            {
                LoginSucceeded = true;
                ForcePasswordReset = result.Code == LoginResultCode.ForcePasswordReset;
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError(result.Message);
                PasswordBox.Clear();
                LoginButton.IsEnabled = true;
            }
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool isDark = ThemeToggleBtn.IsChecked != true;
            ThemeManager.ApplyTheme(isDark);
            ThemeToggleBtn.Content = isDark ? "☀ Light Mode" : "🌙 Dark Mode";
        }
    }
}
