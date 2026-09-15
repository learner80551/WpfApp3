using System.Windows;
using System.Windows.Media;
using WpfApp3.Authentication;

namespace WpfApp3.UI.Settings
{
    public partial class ChangePasswordWindow : Window
    {
        public ChangePasswordWindow()
        {
            InitializeComponent();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ResultText.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = false;

            string current = CurrentPasswordBox.Password;
            string newPwd  = NewPasswordBox.Password;
            string confirm = ConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(current))
            {
                ShowError("Current password is required.");
                SaveButton.IsEnabled = true;
                return;
            }

            if (newPwd != confirm)
            {
                ShowError("New passwords do not match.");
                SaveButton.IsEnabled = true;
                return;
            }

            long? userId = SessionManager.CurrentSession?.UserId;
            if (userId == null)
            {
                ShowError("No active session.");
                SaveButton.IsEnabled = true;
                return;
            }

            var (ok, error) = AuthenticationService.ChangePassword(userId.Value, current, newPwd);

            if (ok)
            {
                ResultText.Foreground = (Brush)Application.Current.Resources["SuccessColor"];
                ResultText.Text = "Password changed successfully.";
                ResultText.Visibility = Visibility.Visible;
                CurrentPasswordBox.Clear();
                NewPasswordBox.Clear();
                ConfirmPasswordBox.Clear();

                MessageBox.Show(
                    "Your password has been changed successfully.",
                    "Navlan — Password Changed",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError(error);
            }

            SaveButton.IsEnabled = true;
        }

        private void ShowError(string msg)
        {
            ResultText.Foreground = (Brush)Application.Current.Resources["DangerColor"];
            ResultText.Text = msg;
            ResultText.Visibility = Visibility.Visible;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
