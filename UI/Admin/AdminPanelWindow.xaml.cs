using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using WpfApp3.Admin;
using WpfApp3.Authentication;
using WpfApp3.Database;

namespace WpfApp3.UI.Admin
{
    // View model for the device list
    public class DeviceViewModel
    {
        public string DeviceId    { get; set; } = "";
        public string Username    { get; set; } = "";
        public string Role        { get; set; } = "";
        public string StatusText  { get; set; } = "";
        public string LastSeenText{ get; set; } = "";
        public long   UserId      { get; set; }
        public bool   IsRevoked   { get; set; }
        public bool   UserIsActive{ get; set; }
    }

    public partial class AdminPanelWindow : Window
    {
        private DeviceViewModel? _selectedDevice;

        public AdminPanelWindow()
        {
            InitializeComponent();
            LoadDevices();
        }

        private void LoadDevices()
        {
            var session = SessionManager.CurrentSession;
            var devices = AdminService.GetAllDevices(session);
            var vms = new List<DeviceViewModel>();

            foreach (var d in devices)
            {
                string status = d.IsRevoked ? "Revoked"
                              : !d.IsAuthorized ? "Unauthorized"
                              : !d.UserIsActive ? "Disabled"
                              : "Active";

                string lastSeen = d.LastSeenAt.HasValue
                    ? d.LastSeenAt.Value.ToLocalTime().ToString("dd MMM HH:mm")
                    : "Never";

                vms.Add(new DeviceViewModel
                {
                    DeviceId     = d.DeviceId,
                    Username     = d.Username,
                    Role         = d.Role,
                    StatusText   = status,
                    LastSeenText = lastSeen,
                    UserId       = d.UserId,
                    IsRevoked    = d.IsRevoked,
                    UserIsActive = d.UserIsActive
                });
            }

            DeviceListView.ItemsSource = vms;
        }

        private void DeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedDevice = DeviceListView.SelectedItem as DeviceViewModel;
            ActionResultText.Visibility = Visibility.Collapsed;

            if (_selectedDevice == null)
            {
                SelectedDeviceText.Text = "— select a device —";
                EnableBtn.IsEnabled  = false;
                DisableBtn.IsEnabled = false;
                RevokeBtn.IsEnabled  = false;
                ResetBtn.IsEnabled   = false;
                return;
            }

            SelectedDeviceText.Text =
                $"{_selectedDevice.DeviceId}  ({_selectedDevice.Username})";

            bool notSelf = _selectedDevice.DeviceId != SessionManager.CurrentSession?.DeviceId;
            EnableBtn.IsEnabled  = notSelf && !_selectedDevice.UserIsActive;
            DisableBtn.IsEnabled = notSelf && _selectedDevice.UserIsActive;
            RevokeBtn.IsEnabled  = notSelf && !_selectedDevice.IsRevoked;
            ResetBtn.IsEnabled   = notSelf;
        }

        private void CreateUserButton_Click(object sender, RoutedEventArgs e)
        {
            CreateResultText.Visibility = Visibility.Collapsed;
            CreateResultText.Foreground =
                (System.Windows.Media.Brush)FindResource("DangerColor");

            string username    = NewUsernameBox.Text.Trim();
            string deviceId    = NewDeviceIdBox.Text.Trim();
            string password    = NewPasswordBox.Password;

            var result = AdminService.CreateUser(
                SessionManager.CurrentSession,
                username, deviceId, password);

            CreateResultText.Text       = result.Message;
            CreateResultText.Foreground = result.IsSuccess
                ? (System.Windows.Media.Brush)FindResource("SuccessColor")
                : (System.Windows.Media.Brush)FindResource("DangerColor");
            CreateResultText.Visibility = Visibility.Visible;

            if (result.IsSuccess)
            {
                NewUsernameBox.Clear();
                NewDeviceIdBox.Clear();
                NewPasswordBox.Clear();
                LoadDevices();
            }
        }

        private void GenerateDeviceId_Click(object sender, RoutedEventArgs e)
        {
            byte[] rnd = RandomNumberGenerator.GetBytes(4);
            NewDeviceIdBox.Text = "NAV-" + Convert.ToHexString(rnd);
        }

        private void EnableBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            var r = AdminService.EnableUser(
                SessionManager.CurrentSession, _selectedDevice.UserId);
            ShowActionResult(r.Message, r.IsSuccess);
            LoadDevices();
        }

        private void DisableBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            var result = MessageBox.Show(
                $"Disable user '{_selectedDevice.Username}'?\n\n" +
                "They will not be able to log in until re-enabled.",
                "Navlan — Disable User",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            var r = AdminService.DisableUser(
                SessionManager.CurrentSession, _selectedDevice.UserId);
            ShowActionResult(r.Message, r.IsSuccess);
            LoadDevices();
        }

        private void RevokeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            var result = MessageBox.Show(
                $"Revoke device '{_selectedDevice.DeviceId}'?\n\n" +
                "The device will not be able to log in.",
                "Navlan — Revoke Device",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            var r = AdminService.RevokeDevice(
                SessionManager.CurrentSession, _selectedDevice.DeviceId);
            ShowActionResult(r.Message, r.IsSuccess);
            LoadDevices();
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null) return;
            var result = MessageBox.Show(
                $"Reset access for '{_selectedDevice.Username}'?\n\n" +
                "A temporary password will be generated. " +
                "The user must change it on next login.",
                "Navlan — Reset Access",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var (r, tempPwd) = AdminService.ResetUserAccess(
                SessionManager.CurrentSession, _selectedDevice.UserId);

            if (r.IsSuccess)
            {
                MessageBox.Show(
                    $"Access reset for '{_selectedDevice.Username}'.\n\n" +
                    $"Temporary password (share securely):\n\n{tempPwd}\n\n" +
                    "The user will be forced to change this on next login.",
                    "Navlan — Access Reset",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            ShowActionResult(r.Message, r.IsSuccess);
            LoadDevices();
        }

        private void ShowActionResult(string msg, bool success)
        {
            ActionResultText.Text = msg;
            ActionResultText.Foreground = success
                ? (System.Windows.Media.Brush)FindResource("SuccessColor")
                : (System.Windows.Media.Brush)FindResource("DangerColor");
            ActionResultText.Visibility = Visibility.Visible;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadDevices();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
