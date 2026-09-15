using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfApp3.Authentication;
using WpfApp3.Chat;
using WpfApp3.Theme;

namespace WpfApp3
{
    public partial class MainWindow : Window
    {
        private const int PairingPort = 42000;
        private const int SecureTransferPort = 42001;

        private static readonly TimeSpan PairingConnectTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan PairingChallengeTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan PairingApprovalTimeout =
            TimeSpan.FromSeconds(120);

        private static readonly TimeSpan TransferConnectTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan TransferResponseTimeout =
            TimeSpan.FromSeconds(60);

        private static readonly TimeSpan TransferCompletionTimeout =
            TimeSpan.FromSeconds(60);

        private static readonly TimeSpan IncomingResponseTimeout =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan IncomingPairingProofTimeout =
            TimeSpan.FromSeconds(60);

        private static readonly TimeSpan IncomingRequestDialogTimeout =
            TimeSpan.FromSeconds(60);

        private string? selectedFile;

        private readonly CancellationTokenSource discoveryCts = new();
        private readonly CancellationTokenSource transferCts = new();

        private CancellationTokenSource? activeTransferCts;

        private readonly PeerDiscovery peerDiscovery = new();
        private readonly TransferServer transferServer = new();
        private readonly PairingManager pairingManager = new();

        private readonly List<PeerInfo> discoveredPeers = new();

        private readonly object incomingTransferLock = new();

        private bool transferInProgress;

        private readonly HashSet<string> activeOrSeenTransferIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        private const int MaximumRememberedTransferIds = 1000;

        private BroadcastChatService? _chatService;

        // ============================================================
        // DARK / LIGHT COLOURS
        // ============================================================

        private bool _isDarkMode;

        private static readonly (string Key, string LightHex, string DarkHex)[] ThemeTokens =
        {
            ("AppBg",         "#E8ECEC", "#1A1F24"),
            ("SidebarBg",     "#DADEDF", "#161B21"),
            ("PanelBg",       "#E2E6E7", "#212830"),
            ("PanelBorder",   "#C9D0D2", "#2E3844"),
            ("InputBg",       "#EAEEEF", "#1E2530"),
            ("InputBorder",   "#BFC8CA", "#3A4856"),
            ("TextPrimary",   "#1C2B30", "#E8EFF2"),
            ("TextSecondary", "#4A6570", "#A8BDC5"),
            ("TextMuted",     "#8A9FA8", "#5A717C"),
            ("Accent",        "#2C3E50", "#3D5166"),
            ("AccentHover",   "#1A252F", "#283847"),
            ("ActiveNav",     "#2C3E50", "#3D5166"),
            ("NavHover",      "#C5CBCC", "#2A333D"),
        };

        private void ApplyTheme(bool dark)
        {
            _isDarkMode = dark;

            // ── FIX: delegate to ThemeManager which creates NEW brush instances ──
            // WPF freezes shared SolidColorBrush objects after first layout pass.
            // Calling brush.Color on a frozen brush throws InvalidOperationException.
            // ThemeManager.ApplyTheme() always replaces resources with NEW brushes.
            ThemeManager.ApplyTheme(dark);

            // Keep the existing window-level tokens in sync for backward compat
            foreach (var (key, lightHex, darkHex) in ThemeTokens)
            {
                string hex = dark ? darkHex : lightHex;
                var color = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var newBrush = new System.Windows.Media.SolidColorBrush(color);
                newBrush.Freeze();
                Resources[key] = newBrush;
            }

            // sync both toggle buttons
            ThemeToggleBtn.IsChecked = dark;
            ThemeToggleBtn.Content   = dark ? "\u2600 Light Mode" : "\U0001F319 Dark Mode";

            ThemeToggleSettings.IsChecked = dark;
            ThemeToggleSettings.Content   = dark ? "On" : "Off";
        }

        private void ThemeToggleBtn_Checked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!_isDarkMode) ApplyTheme(true);
        }

        private void ThemeToggleBtn_Unchecked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_isDarkMode) ApplyTheme(false);
        }

        // ============================================================
        // NAV HELPERS
        // ============================================================

        private enum NavPage { Home, History, KnownDevices, AuthDevice, Settings, Chat }

        private NavPage _currentPage = NavPage.Home;

        private void NavigateTo(NavPage page)
        {
            _currentPage = page;

            PageHome.Visibility         = page == NavPage.Home         ? Visibility.Visible : Visibility.Collapsed;
            PageHistory.Visibility      = page == NavPage.History      ? Visibility.Visible : Visibility.Collapsed;
            PageKnownDevices.Visibility = page == NavPage.KnownDevices ? Visibility.Visible : Visibility.Collapsed;
            PageAuthDevice.Visibility   = page == NavPage.AuthDevice   ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility     = page == NavPage.Settings     ? Visibility.Visible : Visibility.Collapsed;

            // PageChat is a UserControl embedded in the XAML
            if (PageChatContainer != null)
                PageChatContainer.Visibility =
                    page == NavPage.Chat ? Visibility.Visible : Visibility.Collapsed;

            NavHome.Style         = page == NavPage.Home         ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");
            NavHistory.Style      = page == NavPage.History      ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");
            NavKnownDevices.Style = page == NavPage.KnownDevices ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");
            NavAuthDevice.Style   = page == NavPage.AuthDevice   ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");
            NavSettings.Style     = page == NavPage.Settings     ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");

            if (NavChat != null)
                NavChat.Style =
                    page == NavPage.Chat ? (Style)FindResource("NavButtonActive") : (Style)FindResource("NavButton");
        }

        private void NavHome_Click(object sender, System.Windows.RoutedEventArgs e)         => NavigateTo(NavPage.Home);
        private void NavHistory_Click(object sender, System.Windows.RoutedEventArgs e)      => NavigateTo(NavPage.History);
        private void NavKnownDevices_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo(NavPage.KnownDevices);
        private void NavAuthDevice_Click(object sender, System.Windows.RoutedEventArgs e)   => NavigateTo(NavPage.AuthDevice);
        private void NavSettings_Click(object sender, System.Windows.RoutedEventArgs e)     => NavigateTo(NavPage.Settings);
        private void NavChat_Click(object sender, System.Windows.RoutedEventArgs e)         => NavigateTo(NavPage.Chat);

        private void NavAdmin_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var panel = new UI.Admin.AdminPanelWindow();
            panel.Owner = this;
            panel.ShowDialog();
        }

        private void LogoutButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "Are you sure you want to log out?",
                "Navlan — Logout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (r != MessageBoxResult.Yes) return;

            Database.AuditRepository.Log(
                "Logout",
                SessionManager.CurrentSession?.Username,
                SessionManager.CurrentSession?.DeviceId,
                result: "Success");

            SessionManager.InvalidateSession();

            // Hide main window, restart login
            Hide();

            var login = new UI.Login.LoginWindow();
            bool? ok = login.ShowDialog();

            if (ok == true && login.LoginSucceeded)
            {
                Show();
                UpdateAccountInfoPanel();
                UpdateRoleBasedVisibility();
            }
            else
            {
                Application.Current.Shutdown(0);
            }
        }

        private void ChangePasswordMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dlg = new UI.Settings.ChangePasswordWindow { Owner = this };
            dlg.ShowDialog();
        }

        private void UpdateRoleBasedVisibility()
        {
            AppSession? session = SessionManager.CurrentSession;
            bool isAdmin = session?.IsAdmin ?? false;

            if (NavAdmin != null)
                NavAdmin.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateAccountInfoPanel()
        {
            AppSession? session = SessionManager.CurrentSession;
            if (session == null) return;

            if (AccountUsernameText != null)
                AccountUsernameText.Text = session.Username;
            if (AccountDeviceIdText != null)
                AccountDeviceIdText.Text = session.DeviceId;
            if (AccountRoleText != null)
                AccountRoleText.Text = session.IsAdmin ? "Administrator" : "Authorized User";
        }

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public MainWindow()
        {
            InitializeComponent();

            TransferProgress.Value = 0;
            ProgressText.Text = "0%";

            DeviceNameBox.Text = Environment.MachineName;

            PairButton.Click += PairButton_Click;
            UnpairButton.Click += UnpairButton_Click;

            peerDiscovery.PeerFound += PeerDiscovery_PeerFound;
            peerDiscovery.PeerOnline += PeerDiscovery_PeerOnline;
            peerDiscovery.PeerOffline += PeerDiscovery_PeerOffline;
            peerDiscovery.PeerRemoved += PeerDiscovery_PeerRemoved;

            _ = peerDiscovery.StartAsync(
                discoveryCts.Token);

            transferServer.TransferRequested +=
                TransferServer_TransferRequested;

            transferServer.PairRequested +=
                TransferServer_PairRequested;

            _ = transferServer.StartAsync(
                transferCts.Token);

            // Initialize chat service
            _chatService = new BroadcastChatService(pairingManager, SecureTransferPort);
            transferServer.ChatMessageReceived += (packet) =>
            {
                _chatService.HandleIncomingPacket(packet);
            };

            if (ChatPanelControl != null)
            {
                ChatPanelControl.Initialize(
                    _chatService,
                    () => discoveredPeers.ToList());
            }

            // Apply saved theme (in case MainWindow resources differ from App resources)
            ApplyTheme(ThemeManager.IsDark);

            // Role-based UI & account info
            UpdateRoleBasedVisibility();
            UpdateAccountInfoPanel();

            StatusText.Text =
                "Searching for nearby devices...";

            PairingStatusText.Text =
                "Select a device";

            PairButton.IsEnabled = false;
            UnpairButton.IsEnabled = false;

            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        // ============================================================
        // TRANSFER SLOT / CONCURRENCY
        // ============================================================

        private bool IsTransferInProgress()
        {
            lock (incomingTransferLock)
            {
                return transferInProgress;
            }
        }

        private bool TryReserveTransferSlot()
        {
            lock (incomingTransferLock)
            {
                if (transferInProgress)
                    return false;

                transferInProgress = true;
                return true;
            }
        }

        private void ReleaseTransferSlot()
        {
            lock (incomingTransferLock)
            {
                transferInProgress = false;
            }
        }

        // ============================================================
        // TIMEOUT HELPERS
        // ============================================================

        private static async Task RunWithTimeoutAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken parentToken,
            TimeSpan timeout,
            string timeoutMessage)
        {
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    parentToken);

            timeoutCts.CancelAfter(timeout);

            try
            {
                await operation(timeoutCts.Token);

                if (!parentToken.IsCancellationRequested &&
                    timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        timeoutMessage);
                }
            }
            catch (OperationCanceledException)
                when (!parentToken.IsCancellationRequested &&
                      timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    timeoutMessage);
            }
        }

        private static async Task<T> RunWithTimeoutAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken parentToken,
            TimeSpan timeout,
            string timeoutMessage)
        {
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    parentToken);

            timeoutCts.CancelAfter(timeout);

            try
            {
                T result =
                    await operation(timeoutCts.Token);

                if (!parentToken.IsCancellationRequested &&
                    timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        timeoutMessage);
                }

                return result;
            }
            catch (OperationCanceledException)
                when (!parentToken.IsCancellationRequested &&
                      timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    timeoutMessage);
            }
        }

        private static Task<string?> ReadLineWithTimeoutAsync(
            StreamReader reader,
            CancellationToken cancellationToken,
            TimeSpan timeout,
            string timeoutMessage)
        {
            return RunWithTimeoutAsync(
                token =>
                    reader.ReadLineAsync(token).AsTask(),
                cancellationToken,
                timeout,
                timeoutMessage);
        }

        private static Task WriteLineWithTimeoutAsync(
            StreamWriter writer,
            string text,
            CancellationToken cancellationToken,
            TimeSpan timeout,
            string timeoutMessage)
        {
            return RunWithTimeoutAsync(
                async token =>
                {
                    await writer.WriteLineAsync(text);
                    await writer.FlushAsync(token);
                },
                cancellationToken,
                timeout,
                timeoutMessage);
        }

        // ============================================================
        // ESC CANCELLATION
        // ============================================================

        private void MainWindow_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (!IsTransferInProgress())
                return;

            CancellationTokenSource? cts =
                activeTransferCts;

            if (cts == null)
                return;

            MessageBoxResult result =
                MessageBox.Show(
                    "Cancel the active file transfer?",
                    "LANShare - Cancel Transfer",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            StatusText.Text =
                "Cancelling transfer...";

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            e.Handled = true;
        }

        private void ResetTransferProgress()
        {
            TransferProgress.Value = 0;
            ProgressText.Text = "0%";
        }

        // ============================================================
        // PEER DISCOVERY
        // ============================================================

        private void PeerDiscovery_PeerFound(
            PeerInfo peer)
        {
            Dispatcher.Invoke(() =>
            {
                AddOrUpdatePeer(peer);

                StatusText.Text =
                    "Nearby device found.";

                UpdatePairingStatus();
                UpdateSendButton();
            });
        }

        private void PeerDiscovery_PeerOnline(
            PeerInfo peer)
        {
            Dispatcher.Invoke(() =>
            {
                AddOrUpdatePeer(peer);

                StatusText.Text =
                    $"{peer.Name} is online.";

                UpdatePairingStatus();
                UpdateSendButton();
            });
        }

        private void PeerDiscovery_PeerOffline(
            PeerInfo peer)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePeerStatus(peer, false);

                StatusText.Text =
                    $"{peer.Name} is offline.";

                UpdatePairingStatus();
                UpdateSendButton();
            });
        }

        private void PeerDiscovery_PeerRemoved(
            PeerInfo peer)
        {
            Dispatcher.Invoke(() =>
            {
                RemovePeer(peer);

                StatusText.Text =
                    "Nearby device list updated.";

                UpdatePairingStatus();
                UpdateSendButton();
            });
        }

        private void AddOrUpdatePeer(
            PeerInfo peer)
        {
            if (peer == null)
                return;

            PeerInfo? selectedPeer =
                GetSelectedPeer();

            PeerInfo? existingPeer = null;

            if (!string.IsNullOrWhiteSpace(
                    peer.CertificateFingerprint))
            {
                existingPeer =
                    discoveredPeers.FirstOrDefault(
                        existing =>
                            !string.IsNullOrWhiteSpace(
                                existing.CertificateFingerprint) &&
                            string.Equals(
                                existing.CertificateFingerprint,
                                peer.CertificateFingerprint,
                                StringComparison.OrdinalIgnoreCase));
            }

            if (existingPeer == null)
            {
                existingPeer =
                    discoveredPeers.FirstOrDefault(
                        existing =>
                            string.Equals(
                                existing.Name,
                                peer.Name,
                                StringComparison.OrdinalIgnoreCase));
            }

            if (existingPeer != null)
                discoveredPeers.Remove(existingPeer);

            if (!string.IsNullOrWhiteSpace(
                    peer.CertificateFingerprint))
            {
                discoveredPeers.RemoveAll(
                    existing =>
                        !ReferenceEquals(
                            existing,
                            peer) &&
                        !string.IsNullOrWhiteSpace(
                            existing.CertificateFingerprint) &&
                        string.Equals(
                            existing.CertificateFingerprint,
                            peer.CertificateFingerprint,
                            StringComparison.OrdinalIgnoreCase));
            }

            discoveredPeers.Add(peer);

            RefreshDeviceList(selectedPeer);
        }

        private void UpdatePeerStatus(
            PeerInfo peer,
            bool online)
        {
            PeerInfo? selectedPeer =
                GetSelectedPeer();

            PeerInfo? existingPeer =
                discoveredPeers.FirstOrDefault(
                    existing =>
                        IsSamePeer(
                            existing,
                            peer));

            if (existingPeer == null)
            {
                peer.IsOnline = online;

                discoveredPeers.Add(peer);

                RefreshDeviceList(selectedPeer);
                return;
            }

            existingPeer.IsOnline = online;
            existingPeer.LastSeenUtc =
                peer.LastSeenUtc;

            if (!string.IsNullOrWhiteSpace(
                    peer.IpAddress))
            {
                existingPeer.IpAddress =
                    peer.IpAddress;
            }

            if (!string.IsNullOrWhiteSpace(
                    peer.Name))
            {
                existingPeer.Name =
                    peer.Name;
            }

            if (!string.IsNullOrWhiteSpace(
                    peer.CertificateFingerprint))
            {
                existingPeer.CertificateFingerprint =
                    peer.CertificateFingerprint;
            }

            RefreshDeviceList(selectedPeer);
        }

        private void RemovePeer(
            PeerInfo peer)
        {
            PeerInfo? selectedPeer =
                GetSelectedPeer();

            discoveredPeers.RemoveAll(
                existing =>
                    IsSamePeer(
                        existing,
                        peer));

            RefreshDeviceList(selectedPeer);
        }

        private static bool IsSamePeer(
            PeerInfo first,
            PeerInfo second)
        {
            if (!string.IsNullOrWhiteSpace(
                    first.CertificateFingerprint) &&
                !string.IsNullOrWhiteSpace(
                    second.CertificateFingerprint))
            {
                return string.Equals(
                    first.CertificateFingerprint,
                    second.CertificateFingerprint,
                    StringComparison.OrdinalIgnoreCase);
            }

            return
                string.Equals(
                    first.Name,
                    second.Name,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    first.IpAddress,
                    second.IpAddress,
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool _syncingSelections;

        private void RefreshDeviceList(
            PeerInfo? selectedPeer = null)
        {
            if (selectedPeer == null)
                selectedPeer = GetSelectedPeer();

            DevicesList.Items.Clear();
            RecipientCombo.Items.Clear();

            foreach (PeerInfo peer in discoveredPeers
                         .OrderByDescending(
                             peer => peer.IsOnline)
                         .ThenBy(
                             peer => peer.Name,
                             StringComparer.OrdinalIgnoreCase))
            {
                string deviceText =
                    $"{peer.Name} - {peer.IpAddress}";

                if (!peer.IsOnline)
                    deviceText += " (Offline)";

                DevicesList.Items.Add(deviceText);
                RecipientCombo.Items.Add(deviceText);
            }

            if (selectedPeer != null)
            {
                for (int i = 0;
                     i < DevicesList.Items.Count;
                     i++)
                {
                    string displayText =
                        DevicesList.Items[i]?.ToString() ?? "";

                    PeerInfo? currentPeer =
                        FindPeerFromDisplayText(
                            displayText);

                    if (currentPeer != null &&
                        IsSamePeer(
                            currentPeer,
                            selectedPeer))
                    {
                        _syncingSelections = true;
                        DevicesList.SelectedIndex  = i;
                        RecipientCombo.SelectedIndex = i;
                        _syncingSelections = false;
                        break;
                    }
                }
            }
        }

        private void DevicesList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (!_syncingSelections)
            {
                _syncingSelections = true;
                RecipientCombo.SelectedIndex = DevicesList.SelectedIndex;
                _syncingSelections = false;
            }

            UpdatePairingStatus();
            UpdateSendButton();
        }

        private void RecipientCombo_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (!_syncingSelections)
            {
                _syncingSelections = true;
                DevicesList.SelectedIndex = RecipientCombo.SelectedIndex;
                _syncingSelections = false;
            }

            UpdatePairingStatus();
            UpdateSendButton();
        }

        private PeerInfo? GetSelectedPeer()
        {
            // Try RecipientCombo first (Home page), fall back to DevicesList
            object? item = RecipientCombo.SelectedItem ?? DevicesList.SelectedItem;

            if (item == null)
                return null;

            string displayText = item.ToString() ?? "";

            return FindPeerFromDisplayText(
                displayText);
        }

        private PeerInfo? FindPeerFromDisplayText(
            string displayText)
        {
            if (string.IsNullOrWhiteSpace(
                    displayText))
            {
                return null;
            }

            const string offlineSuffix =
                " (Offline)";

            string cleanText =
                displayText;

            if (cleanText.EndsWith(
                    offlineSuffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                cleanText =
                    cleanText[
                        ..^offlineSuffix.Length];
            }

            int separatorIndex =
                cleanText.IndexOf(
                    " - ",
                    StringComparison.Ordinal);

            if (separatorIndex <= 0)
                return null;

            string deviceName =
                cleanText[..separatorIndex];

            string ipAddress =
                cleanText[
                    (separatorIndex + 3)..];

            if (string.IsNullOrWhiteSpace(
                    deviceName) ||
                string.IsNullOrWhiteSpace(
                    ipAddress))
            {
                return null;
            }

            return FindPeer(
                deviceName,
                ipAddress);
        }

        // ============================================================
        // FILE SELECTION
        // ============================================================

        private void SelectFileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (IsTransferInProgress())
                return;

            OpenFileDialog dialog =
                new OpenFileDialog();

            if (dialog.ShowDialog() == true)
            {
                selectedFile =
                    dialog.FileName;

                SelectedFileText.Text =
                    Path.GetFileName(selectedFile);

                StatusText.Text =
                    $"Selected: {Path.GetFileName(selectedFile)}";

                UpdateSendButton();
            }
        }

        // ============================================================
        // PAIRING CLIENT
        // ============================================================

        private async void PairButton_Click(
            object? sender,
            RoutedEventArgs e)
        {
            if (IsTransferInProgress())
                return;

            PeerInfo? selectedPeer =
                GetSelectedPeer();

            if (selectedPeer == null)
            {
                MessageBox.Show(
                    "Please select a nearby device.",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (!selectedPeer.IsOnline)
            {
                MessageBox.Show(
                    "This device is currently offline.\n\n" +
                    "Wait for it to come online before pairing.",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            string targetDeviceName =
                selectedPeer.Name;

            string targetIp =
                selectedPeer.IpAddress;

            string? expectedFingerprint =
                selectedPeer.CertificateFingerprint;

            if (string.IsNullOrWhiteSpace(
                    expectedFingerprint))
            {
                MessageBox.Show(
                    "The selected device did not provide a " +
                    "certificate fingerprint during discovery.",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            try
            {
                PairButton.IsEnabled = false;
                UnpairButton.IsEnabled = false;

                PairingStatusText.Text =
                    "Connecting securely...";

                StatusText.Text =
                    $"Pairing with {targetDeviceName}...";

                X509Certificate2 localCertificate =
                    DeviceCertificate.GetOrCreateCertificate();

                using RSA? privateKey =
                    localCertificate.GetRSAPrivateKey();

                if (privateKey == null)
                {
                    throw new AuthenticationException(
                        "The local device certificate does not " +
                        "contain an RSA private key.");
                }

                using TcpClient client =
                    new TcpClient();

                client.NoDelay = true;

                await RunWithTimeoutAsync(
                    token =>
                        client.ConnectAsync(
                            targetIp,
                            PairingPort,
                            token).AsTask(),
                    CancellationToken.None,
                    PairingConnectTimeout,
                    "The pairing connection timed out.");

                string? serverFingerprint = null;

                using SslStream sslStream =
                    new SslStream(
                        client.GetStream(),
                        false,
                        (
                            certificateSender,
                            certificate,
                            chain,
                            errors) =>
                                ValidatePinnedCertificate(
                                    certificate,
                                    expectedFingerprint,
                                    out serverFingerprint));

                await RunWithTimeoutAsync(
                    token =>
                        sslStream.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost =
                                    "LANShare",

                                EnabledSslProtocols =
                                    SslProtocols.Tls13 |
                                    SslProtocols.Tls12
                            },
                            token),
                    CancellationToken.None,
                    PairingConnectTimeout,
                    "The secure pairing handshake timed out.");

                if (string.IsNullOrWhiteSpace(
                        serverFingerprint))
                {
                    throw new AuthenticationException(
                        "The receiver did not provide a valid certificate.");
                }

                if (!string.Equals(
                        expectedFingerprint,
                        serverFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthenticationException(
                        "The receiver certificate does not match " +
                        "the certificate advertised during discovery.");
                }

                using StreamReader reader =
                    new StreamReader(
                        sslStream,
                        Encoding.UTF8,
                        false,
                        4096,
                        true);

                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                PairingRequest request =
                    new PairingRequest
                    {
                        DeviceName =
                            Environment.MachineName,

                        CertificateBase64 =
                            Convert.ToBase64String(
                                localCertificate.Export(
                                    X509ContentType.Cert))
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(request),
                    CancellationToken.None,
                    PairingChallengeTimeout,
                    "Sending the pairing request timed out.");

                PairingStatusText.Text =
                    "Waiting for security challenge...";

                string? challengeJson =
                    await ReadLineWithTimeoutAsync(
                        reader,
                        CancellationToken.None,
                        PairingChallengeTimeout,
                        "Waiting for the security challenge timed out.");

                if (string.IsNullOrWhiteSpace(
                        challengeJson))
                {
                    throw new AuthenticationException(
                        "The receiver closed the pairing connection.");
                }

                PairingChallenge? challenge =
                    JsonSerializer.Deserialize<PairingChallenge>(
                        challengeJson);

                if (challenge == null ||
                    challenge.RequestId !=
                        request.RequestId ||
                    challenge.Type !=
                        "PAIR_CHALLENGE")
                {
                    throw new AuthenticationException(
                        "Invalid pairing challenge.");
                }

                byte[] challengeBytes =
                    Convert.FromBase64String(
                        challenge.ChallengeBase64);

                byte[] signature =
                    privateKey.SignData(
                        challengeBytes,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                PairingProof proof =
                    new PairingProof
                    {
                        RequestId =
                            request.RequestId,

                        SignatureBase64 =
                            Convert.ToBase64String(
                                signature)
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(proof),
                    CancellationToken.None,
                    PairingChallengeTimeout,
                    "Sending the pairing proof timed out.");

                PairingStatusText.Text =
                    "Waiting for pairing approval...";

                string? responseJson =
                    await ReadLineWithTimeoutAsync(
                        reader,
                        CancellationToken.None,
                        PairingApprovalTimeout,
                        "The pairing approval timed out.");

                if (string.IsNullOrWhiteSpace(
                        responseJson))
                {
                    throw new AuthenticationException(
                        "No pairing response was received.");
                }

                PairingResponse? response =
                    JsonSerializer.Deserialize<PairingResponse>(
                        responseJson);

                if (response == null ||
                    response.RequestId !=
                        request.RequestId)
                {
                    throw new AuthenticationException(
                        "Invalid pairing response.");
                }

                if (!response.Accepted)
                {
                    PairingStatusText.Text =
                        "Pairing rejected.";

                    StatusText.Text =
                        "Pairing was rejected by the other device.";

                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(
                            response.Message)
                            ? "The other device rejected pairing."
                            : response.Message,
                        "LANShare",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                string receiverFingerprint =
                    response.CertificateFingerprint;

                if (string.IsNullOrWhiteSpace(
                        receiverFingerprint))
                {
                    throw new AuthenticationException(
                        "The receiver did not return its " +
                        "certificate fingerprint.");
                }

                if (!string.Equals(
                        receiverFingerprint,
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthenticationException(
                        "The receiver returned a certificate " +
                        "fingerprint that does not match the " +
                        "certificate verified during TLS.");
                }

                bool pairingSaved =
                    pairingManager.AddOrUpdate(
                        targetDeviceName,
                        receiverFingerprint);

                if (!pairingSaved)
                {
                    throw new AuthenticationException(
                        "This device already has a different " +
                        "trusted certificate.\n\n" +
                        "The existing trusted identity was NOT replaced.\n\n" +
                        "Explicitly unpair the device before pairing " +
                        "its new certificate.");
                }

                PairingStatusText.Text =
                    "✓ Paired and trusted";

                StatusText.Text =
                    $"Successfully paired with {targetDeviceName}.";

                MessageBox.Show(
                    $"Device paired successfully.\n\n" +
                    $"{targetDeviceName}\n\n" +
                    $"Certificate fingerprint:\n" +
                    $"{FormatFingerprint(receiverFingerprint)}",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex)
            {
                PairingStatusText.Text =
                    "Pairing timed out.";

                StatusText.Text =
                    "Pairing timed out.";

                MessageBox.Show(
                    "Pairing timed out.\n\n" +
                    ex.Message,
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (AuthenticationException ex)
            {
                PairingStatusText.Text =
                    "Pairing failed.";

                StatusText.Text =
                    "Security verification failed.";

                MessageBox.Show(
                    "Pairing was blocked.\n\n" +
                    ex.Message,
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                PairingStatusText.Text =
                    "Pairing failed.";

                StatusText.Text =
                    "Pairing failed.";

                MessageBox.Show(
                    "Could not pair with the device.\n\n" +
                    ex.Message,
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                UpdatePairingStatus();
                UpdateSendButton();
            }
        }

        // ============================================================
        // UNPAIR
        // ============================================================

        private void UnpairButton_Click(
            object? sender,
            RoutedEventArgs e)
        {
            if (IsTransferInProgress())
                return;

            PeerInfo? selectedPeer =
                GetSelectedPeer();

            if (selectedPeer == null)
            {
                MessageBox.Show(
                    "Please select a device first.",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            string deviceName =
                selectedPeer.Name;

            string? fingerprint =
                selectedPeer.CertificateFingerprint;

            MessageBoxResult result =
                MessageBox.Show(
                    $"Are you sure you want to unpair this device?\n\n" +
                    $"{deviceName}\n\n" +
                    "File transfers to this device will be disabled " +
                    "until it is paired again.",
                    "LANShare - Unpair Device",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            bool removed;

            if (!string.IsNullOrWhiteSpace(
                    fingerprint))
            {
                removed =
                    pairingManager.Remove(
                        deviceName,
                        fingerprint);
            }
            else
            {
                removed =
                    pairingManager.Remove(
                        deviceName);
            }

            if (removed)
            {
                PairingStatusText.Text =
                    "Not paired — pair this device before transferring.";

                StatusText.Text =
                    $"{deviceName} has been unpaired.";

                MessageBox.Show(
                    $"Device unpaired successfully.\n\n" +
                    $"{deviceName}",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text =
                    "Device was not paired.";

                MessageBox.Show(
                    "This device was not found in the " +
                    "trusted-device list.",
                    "LANShare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            UpdatePairingStatus();
            UpdateSendButton();
        }

        // ============================================================
        // SEND FILE
        // ============================================================

        private async void SendButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (IsTransferInProgress())
                return;

            if (string.IsNullOrWhiteSpace(
                    selectedFile))
            {
                MessageBox.Show(
                    "Please select a file first.",
                    "LAN Transfer");

                return;
            }

            PeerInfo? targetPeer =
                GetSelectedPeer();

            if (targetPeer == null)
            {
                MessageBox.Show(
                    "Please select a nearby device.",
                    "LAN Transfer");

                return;
            }

            if (!targetPeer.IsOnline)
            {
                MessageBox.Show(
                    "The selected device is currently offline.",
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (!TryReserveTransferSlot())
                return;

            CancellationTokenSource transferCancellationSource =
                new CancellationTokenSource();

            activeTransferCts =
                transferCancellationSource;

            CancellationToken cancellationToken =
                transferCancellationSource.Token;

            try
            {
                SendButton.IsEnabled = false;
                SelectFileButton.IsEnabled = false;
                PairButton.IsEnabled = false;
                UnpairButton.IsEnabled = false;

                ResetTransferProgress();

                string targetDeviceName =
                    targetPeer.Name;

                string targetIp =
                    targetPeer.IpAddress;

                string? expectedFingerprint =
                    targetPeer.CertificateFingerprint;

                if (string.IsNullOrWhiteSpace(
                        expectedFingerprint))
                {
                    throw new AuthenticationException(
                        "The selected device did not provide a " +
                        "certificate fingerprint during LAN discovery.");
                }

                if (!pairingManager.IsPaired(
                        targetDeviceName,
                        expectedFingerprint))
                {
                    StatusText.Text =
                        "Device is not paired.";

                    MessageBox.Show(
                        "This device is not paired.\n\n" +
                        "Pair the device before sending files.",
                        "LANShare",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                FileInfo fileInfo =
                    new FileInfo(selectedFile);

                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException(
                        "The selected file no longer exists.",
                        selectedFile);
                }

                if ((fileInfo.Attributes &
                     FileAttributes.Directory) != 0)
                {
                    throw new IOException(
                        "The selected path is a folder, not a file.");
                }

                StatusText.Text =
                    "Calculating SHA-256...";

                string sha256 =
                    await FileTransfer.CalculateSha256Async(
                        selectedFile,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                fileInfo.Refresh();

                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException(
                        "The selected file was removed or moved " +
                        "after SHA-256 calculation.",
                        selectedFile);
                }

                TransferRequest request =
                    new TransferRequest
                    {
                        FileName =
                            fileInfo.Name,

                        FileSize =
                            fileInfo.Length,

                        SenderName =
                            Environment.MachineName,

                        Sha256 =
                            sha256
                    };

                X509Certificate2 localCertificate =
                    DeviceCertificate.GetOrCreateCertificate();

                using TcpClient client =
                    new TcpClient();

                client.NoDelay = true;

                StatusText.Text =
                    $"Connecting securely to {targetDeviceName}...";

                await RunWithTimeoutAsync(
                    token =>
                        client.ConnectAsync(
                            targetIp,
                            SecureTransferPort,
                            token).AsTask(),
                    cancellationToken,
                    TransferConnectTimeout,
                    "The secure transfer connection timed out.");

                cancellationToken.ThrowIfCancellationRequested();

                string? serverFingerprint = null;

                using SslStream sslStream =
                    new SslStream(
                        client.GetStream(),
                        false,
                        (
                            certificateSender,
                            certificate,
                            chain,
                            errors) =>
                                ValidatePinnedCertificate(
                                    certificate,
                                    expectedFingerprint,
                                    out serverFingerprint));

                X509CertificateCollection clientCertificates =
                    new X509CertificateCollection
                    {
                        localCertificate
                    };

                await RunWithTimeoutAsync(
                    token =>
                        sslStream.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost =
                                    "LANShare",

                                EnabledSslProtocols =
                                    SslProtocols.Tls13 |
                                    SslProtocols.Tls12,

                                ClientCertificates =
                                    clientCertificates
                            },
                            token),
                    cancellationToken,
                    TransferConnectTimeout,
                    "The TLS handshake timed out.");

                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(
                        serverFingerprint))
                {
                    throw new AuthenticationException(
                        "The receiver did not provide a valid certificate.");
                }

                if (!string.Equals(
                        expectedFingerprint,
                        serverFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthenticationException(
                        "The receiver certificate does not match " +
                        "the certificate advertised during discovery.\n\n" +
                        "The connection has been blocked.");
                }

                StatusText.Text =
                    "TLS and device identity verified.";

                using StreamReader reader =
                    new StreamReader(
                        sslStream,
                        Encoding.UTF8,
                        false,
                        4096,
                        true);

                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                fileInfo.Refresh();

                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException(
                        "The selected file disappeared before " +
                        "transfer started.",
                        selectedFile);
                }

                if (fileInfo.Length != request.FileSize)
                {
                    throw new IOException(
                        "The selected file changed size after " +
                        "SHA-256 calculation. The transfer was stopped.");
                }

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(request),
                    cancellationToken,
                    TransferResponseTimeout,
                    "Sending the transfer request timed out.");

                StatusText.Text =
                    "Waiting for receiver response...";

                string? responseJson =
                    await ReadLineWithTimeoutAsync(
                        reader,
                        cancellationToken,
                        TransferResponseTimeout,
                        "Waiting for the receiver response timed out.");

                if (string.IsNullOrWhiteSpace(
                        responseJson))
                {
                    throw new IOException(
                        "The receiver closed the connection before " +
                        "accepting or rejecting the transfer.");
                }

                TransferResponse? response;

                try
                {
                    response =
                        JsonSerializer.Deserialize<TransferResponse>(
                            responseJson);
                }
                catch (JsonException)
                {
                    throw new IOException(
                        "The receiver returned malformed transfer-response data.");
                }

                if (response == null)
                {
                    throw new IOException(
                        "The receiver returned an empty transfer response.");
                }

                if (response.RequestId !=
                    request.RequestId)
                {
                    throw new IOException(
                        "The receiver returned a response for a " +
                        "different transfer request.");
                }

                if (!response.Accepted)
                {
                    StatusText.Text =
                        "Transfer rejected.";

                    ResetTransferProgress();

                    MessageBox.Show(
                        "The receiver rejected the transfer.",
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                StatusText.Text =
                    "Transfer accepted. Sending securely...";

                var progress =
                    new Progress<double>(
                        percentage =>
                        {
                            if (!IsTransferInProgress())
                                return;

                            TransferProgress.Value =
                                percentage;

                            ProgressText.Text =
                                $"{percentage:F0}%";
                        });

                await FileTransfer.SendFileAsync(
                    sslStream,
                    selectedFile,
                    progress,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                StatusText.Text =
                    "File sent. Waiting for receiver verification...";

                string? completionJson =
                    await ReadLineWithTimeoutAsync(
                        reader,
                        cancellationToken,
                        TransferCompletionTimeout,
                        "Waiting for receiver verification timed out.");

                if (string.IsNullOrWhiteSpace(
                        completionJson))
                {
                    throw new IOException(
                        "The receiver disconnected before sending " +
                        "the completion acknowledgement.");
                }

                TransferCompletionResponse? completion;

                try
                {
                    completion =
                        JsonSerializer.Deserialize<
                            TransferCompletionResponse>(
                                completionJson);
                }
                catch (JsonException)
                {
                    throw new IOException(
                        "The receiver returned malformed completion data.");
                }

                if (completion == null)
                {
                    throw new IOException(
                        "The receiver returned an empty completion acknowledgement.");
                }

                if (completion.RequestId !=
                    request.RequestId)
                {
                    throw new IOException(
                        "The receiver returned a completion " +
                        "acknowledgement for a different transfer.");
                }

                if (!completion.Verified)
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "Receiver could not verify the file.";

                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(
                            completion.Message)
                            ? "The receiver could not verify " +
                              "the transferred file."
                            : completion.Message,
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return;
                }

                if (!IsValidSha256(
                        completion.Sha256))
                {
                    throw new IOException(
                        "The receiver returned an invalid SHA-256 value.");
                }

                if (!string.Equals(
                        sha256,
                        completion.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "Hash confirmation mismatch.";

                    MessageBox.Show(
                        "The receiver reported successful verification, " +
                        "but its SHA-256 does not match the sender's SHA-256.\n\n" +
                        $"Sender SHA-256:\n{sha256}\n\n" +
                        $"Receiver SHA-256:\n{completion.Sha256}",
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return;
                }

                TransferProgress.Value = 100;
                ProgressText.Text = "100%";

                StatusText.Text =
                    "File transfer completed and verified.";

                // Record in history
                string historyEntry =
                    $"{DateTime.Now:HH:mm}  Sent: {Path.GetFileName(selectedFile ?? "")}  \u2192  {targetDeviceName}";

                HistoryList.Items.Insert(0, historyEntry);

                MessageBox.Show(
                    $"File sent successfully and verified by the receiver.\n\n" +
                    "TLS encrypted transfer.\n\n" +
                    "Mutual TLS certificate authentication.\n\n" +
                    $"SHA-256 verified by both devices:\n{sha256}",
                    "Navlan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                ResetTransferProgress();

                StatusText.Text =
                    "Transfer cancelled.";

                MessageBox.Show(
                    "The file transfer was cancelled.\n\n" +
                    "Any incomplete transfer data will be discarded.",
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (TimeoutException ex)
            {
                ResetTransferProgress();

                StatusText.Text =
                    "Transfer timed out.";

                MessageBox.Show(
                    "The transfer timed out.\n\n" +
                    ex.Message +
                    "\n\nAny incomplete transfer data will be discarded.",
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (AuthenticationException ex)
            {
                StatusText.Text =
                    "TLS security verification failed.";

                ResetTransferProgress();

                MessageBox.Show(
                    "The secure connection was rejected.\n\n" +
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (FileNotFoundException ex)
            {
                StatusText.Text =
                    "Selected file is unavailable.";

                ResetTransferProgress();

                MessageBox.Show(
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException ex)
            {
                StatusText.Text =
                    "File access was denied.";

                ResetTransferProgress();

                MessageBox.Show(
                    "LANShare could not access the selected file.\n\n" +
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (IOException ex)
            {
                StatusText.Text =
                    "Connection or file transfer failed.";

                ResetTransferProgress();

                MessageBox.Show(
                    "The transfer could not be completed.\n\n" +
                    "Possible causes:\n" +
                    "• The other device disconnected.\n" +
                    "• The network connection was interrupted.\n" +
                    "• The file changed or became unavailable.\n" +
                    "• The receiver stopped the transfer.\n\n" +
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (SocketException ex)
            {
                StatusText.Text =
                    "Network connection failed.";

                ResetTransferProgress();

                MessageBox.Show(
                    "The network connection to the other device failed.\n\n" +
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "File transfer failed.";

                ResetTransferProgress();

                MessageBox.Show(
                    "Could not transfer the file.\n\n" +
                    ex.Message,
                    "LAN Transfer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(
                        activeTransferCts,
                        transferCancellationSource))
                {
                    activeTransferCts = null;
                }

                transferCancellationSource.Dispose();

                ReleaseTransferSlot();

                SelectFileButton.IsEnabled = true;

                UpdatePairingStatus();
                UpdateSendButton();
            }
        }

        // ============================================================
        // INCOMING TRANSFER
        // ============================================================

        private void TransferServer_TransferRequested(
            SslStream sslStream,
            TcpClient client,
            TransferRequest request)
        {
            _ = HandleTransferRequestAsync(
                sslStream,
                client,
                request);
        }

        private async Task HandleTransferRequestAsync(
            SslStream sslStream,
            TcpClient client,
            TransferRequest request)
        {
            string? destinationPath = null;

            bool initialResponseSent = false;
            bool transferAccepted = false;
            bool transferSlotReserved = false;

            CancellationTokenSource? incomingTransferCts =
                null;

            try
            {
                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                ValidateIncomingTransferRequest(
                    request);

                lock (incomingTransferLock)
                {
                    if (activeOrSeenTransferIds.Contains(
                            request.RequestId))
                    {
                        throw new IOException(
                            "This transfer request has already been " +
                            "received or is already being processed.");
                    }

                    if (activeOrSeenTransferIds.Count >=
                        MaximumRememberedTransferIds)
                    {
                        activeOrSeenTransferIds.Clear();
                    }

                    activeOrSeenTransferIds.Add(
                        request.RequestId);
                }

                if (!TryReserveTransferSlot())
                {
                    TransferResponse busyResponse =
                        new TransferResponse
                        {
                            RequestId =
                                request.RequestId,

                            Accepted = false
                        };

                    await WriteLineWithTimeoutAsync(
                        writer,
                        JsonSerializer.Serialize(
                            busyResponse),
                        CancellationToken.None,
                        IncomingResponseTimeout,
                        "Sending busy response timed out.");

                    initialResponseSent = true;

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text =
                            $"Transfer from {request.SenderName} " +
                            "rejected because LANShare is busy.";
                    });

                    return;
                }

                transferSlotReserved = true;

                string downloadFolder =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile),
                        "Downloads",
                        "LAN Transfer");

                Directory.CreateDirectory(
                    downloadFolder);

                string safeFileName =
                    ValidateAndGetSafeFileName(
                        request.FileName);

                if (!HasEnoughDiskSpace(
                        downloadFolder,
                        request.FileSize))
                {
                    StatusTextOnDispatcher(
                        "Not enough disk space for incoming file.");

                    TransferResponse diskResponse =
                        new TransferResponse
                        {
                            RequestId =
                                request.RequestId,

                            Accepted = false
                        };

                    await WriteLineWithTimeoutAsync(
                        writer,
                        JsonSerializer.Serialize(
                            diskResponse),
                        CancellationToken.None,
                        IncomingResponseTimeout,
                        "Sending disk-space rejection timed out.");

                    initialResponseSent = true;

                    MessageBoxOnDispatcher(
                        "There is not enough free disk space " +
                        "to receive this file.\n\n" +
                        $"Required: " +
                        $"{FormatFileSize(request.FileSize)}\n\n" +
                        "The transfer was rejected before any " +
                        "file data was received.",
                        "LAN Transfer",
                        MessageBoxImage.Warning);

                    return;
                }

                (bool accepted, bool dialogTimedOut) =
                    ShowTransferRequestDialogWithTimeout(
                        request,
                        IncomingRequestDialogTimeout);

                TransferResponse response =
                    new TransferResponse
                    {
                        RequestId =
                            request.RequestId,

                        Accepted =
                            accepted
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        response),
                    CancellationToken.None,
                    IncomingResponseTimeout,
                    "Sending the transfer decision timed out.");

                initialResponseSent = true;

                if (!accepted)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ResetTransferProgress();

                        StatusText.Text =
                            dialogTimedOut
                                ? $"Transfer from {request.SenderName} " +
                                  "timed out waiting for approval."
                                : $"Transfer from {request.SenderName} " +
                                  "rejected.";
                    });

                    return;
                }

                transferAccepted = true;

                incomingTransferCts =
                    new CancellationTokenSource();

                CancellationToken incomingToken =
                    incomingTransferCts.Token;

                Dispatcher.Invoke(() =>
                {
                    activeTransferCts =
                        incomingTransferCts;

                    TransferProgress.Value = 0;
                    ProgressText.Text = "0%";

                    StatusText.Text =
                        $"Receiving {safeFileName} securely...";
                });

                destinationPath =
                    GetUniqueFilePath(
                        downloadFolder,
                        safeFileName);

                EnsurePathInsideFolder(
                    downloadFolder,
                    destinationPath);

                var progress =
                    new Progress<double>(
                        percentage =>
                        {
                            if (!IsTransferInProgress())
                                return;

                            TransferProgress.Value =
                                percentage;

                            ProgressText.Text =
                                $"{percentage:F0}%";
                        });

                string receivedSha256 =
                    await FileTransfer.ReceiveFileAsync(
                        sslStream,
                        destinationPath,
                        request.FileSize,
                        progress,
                        incomingToken);

                incomingToken.ThrowIfCancellationRequested();

                if (!IsValidSha256(
                        receivedSha256))
                {
                    DeleteFileSafely(
                        destinationPath);

                    throw new IOException(
                        "The receiver calculated an invalid SHA-256 value.");
                }

                bool hashMatches =
                    string.Equals(
                        request.Sha256,
                        receivedSha256,
                        StringComparison.OrdinalIgnoreCase);

                TransferCompletionResponse completion =
                    new TransferCompletionResponse
                    {
                        RequestId =
                            request.RequestId,

                        Verified =
                            hashMatches,

                        Sha256 =
                            receivedSha256,

                        Message =
                            hashMatches
                                ? "File received and SHA-256 " +
                                  "verified successfully."
                                : "SHA-256 verification failed."
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        completion),
                    incomingToken,
                    TransferCompletionTimeout,
                    "Sending the verification result timed out.");

                if (!hashMatches)
                {
                    DeleteFileSafely(
                        destinationPath);

                    Dispatcher.Invoke(() =>
                    {
                        ResetTransferProgress();

                        StatusText.Text =
                            "Integrity verification failed.";

                        MessageBox.Show(
                            $"The file was received, but SHA-256 " +
                            $"verification failed.\n\n" +
                            $"Expected:\n{request.Sha256}\n\n" +
                            $"Received:\n{receivedSha256}\n\n" +
                            "The unverified file was deleted.",
                            "LAN Transfer",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });

                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    TransferProgress.Value = 100;
                    ProgressText.Text = "100%";

                    StatusText.Text =
                        "File received and verified securely.";

                    MessageBox.Show(
                        $"File received successfully.\n\n" +
                        "TLS encrypted transfer.\n\n" +
                        "Mutual TLS certificate authentication.\n\n" +
                        $"SHA-256 verified:\n{receivedSha256}\n\n" +
                        $"Saved to:\n{destinationPath}",
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
            catch (OperationCanceledException)
            {
                DeleteFileSafely(
                    destinationPath);

                if (!initialResponseSent)
                {
                    await SendTransferRejectionAsync(
                        sslStream,
                        request?.RequestId ?? "");
                }
                else if (transferAccepted)
                {
                    await TrySendFailureAcknowledgementAsync(
                        sslStream,
                        request?.RequestId ?? "",
                        "The transfer was cancelled.");
                }

                Dispatcher.Invoke(() =>
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "Transfer cancelled.";
                });
            }
            catch (TimeoutException ex)
            {
                DeleteFileSafely(
                    destinationPath);

                if (!initialResponseSent)
                {
                    await SendTransferRejectionAsync(
                        sslStream,
                        request?.RequestId ?? "");
                }
                else if (transferAccepted)
                {
                    await TrySendFailureAcknowledgementAsync(
                        sslStream,
                        request?.RequestId ?? "",
                        ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "Incoming transfer timed out.";

                    MessageBox.Show(
                        "The incoming file transfer timed out.\n\n" +
                        "Any incomplete file was discarded.\n\n" +
                        ex.Message,
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
            catch (IOException ex)
            {
                DeleteFileSafely(
                    destinationPath);

                if (!initialResponseSent)
                {
                    await SendTransferRejectionAsync(
                        sslStream,
                        request?.RequestId ?? "");
                }
                else if (transferAccepted)
                {
                    await TrySendFailureAcknowledgementAsync(
                        sslStream,
                        request?.RequestId ?? "",
                        ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "Transfer connection interrupted.";

                    MessageBox.Show(
                        "The file transfer could not be completed.\n\n" +
                        "Any incomplete file was discarded.\n\n" +
                        ex.Message,
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                DeleteFileSafely(
                    destinationPath);

                if (!initialResponseSent)
                {
                    await SendTransferRejectionAsync(
                        sslStream,
                        request?.RequestId ?? "");
                }
                else if (transferAccepted)
                {
                    await TrySendFailureAcknowledgementAsync(
                        sslStream,
                        request?.RequestId ?? "",
                        "The receiver did not have permission " +
                        "to write the file.");
                }

                Dispatcher.Invoke(() =>
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "File could not be saved.";

                    MessageBox.Show(
                        "LANShare could not save the received file.\n\n" +
                        ex.Message,
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
            catch (Exception ex)
            {
                DeleteFileSafely(
                    destinationPath);

                if (!initialResponseSent)
                {
                    await SendTransferRejectionAsync(
                        sslStream,
                        request?.RequestId ?? "");
                }
                else if (transferAccepted)
                {
                    await TrySendFailureAcknowledgementAsync(
                        sslStream,
                        request?.RequestId ?? "",
                        "The receiver could not complete the transfer.");
                }

                Dispatcher.Invoke(() =>
                {
                    ResetTransferProgress();

                    StatusText.Text =
                        "File receive failed.";

                    MessageBox.Show(
                        "Could not receive the file.\n\n" +
                        ex.Message,
                        "LAN Transfer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
            finally
            {
                if (incomingTransferCts != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (ReferenceEquals(
                                activeTransferCts,
                                incomingTransferCts))
                        {
                            activeTransferCts = null;
                        }
                    });

                    incomingTransferCts.Dispose();
                }

                if (transferSlotReserved)
                    ReleaseTransferSlot();

                try
                {
                    sslStream.Close();
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

                Dispatcher.Invoke(() =>
                {
                    UpdatePairingStatus();
                    UpdateSendButton();
                });
            }
        }

        // ============================================================
        // INCOMING TRANSFER DIALOG TIMEOUT
        // ============================================================

        private (bool Accepted, bool TimedOut)
            ShowTransferRequestDialogWithTimeout(
                TransferRequest request,
                TimeSpan timeout)
        {
            bool accepted = false;
            bool timedOut = false;

            Dispatcher.Invoke(() =>
            {
                TransferRequestWindow dialog =
                    new TransferRequestWindow(
                        request);

                dialog.Owner = this;

                DispatcherTimer timer =
                    new DispatcherTimer
                    {
                        Interval = timeout
                    };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();

                    if (dialog.IsVisible)
                    {
                        timedOut = true;

                        try
                        {
                            dialog.Close();
                        }
                        catch
                        {
                        }
                    }
                };

                dialog.Closed += (_, _) =>
                {
                    timer.Stop();
                };

                timer.Start();

                dialog.ShowDialog();

                accepted =
                    dialog.Accepted;
            });

            return (
                accepted,
                timedOut);
        }

        // ============================================================
        // TRANSFER REQUEST VALIDATION
        // ============================================================

        private static void ValidateIncomingTransferRequest(
            TransferRequest request)
        {
            if (request == null)
                throw new IOException(
                    "The transfer request was empty.");

            if (!string.Equals(
                    request.Type,
                    "TRANSFER_REQUEST",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The transfer request type is invalid.");
            }

            if (string.IsNullOrWhiteSpace(
                    request.RequestId))
            {
                throw new IOException(
                    "The transfer request ID is missing.");
            }

            if (string.IsNullOrWhiteSpace(
                    request.SenderName))
            {
                throw new IOException(
                    "The sender device name is missing.");
            }

            if (string.IsNullOrWhiteSpace(
                    request.FileName))
            {
                throw new IOException(
                    "The file name is missing.");
            }

            if (request.FileSize < 0)
            {
                throw new IOException(
                    "The file size is invalid.");
            }

            if (!IsValidSha256(
                    request.Sha256))
            {
                throw new IOException(
                    "The supplied SHA-256 value is invalid.");
            }
        }

        private static string ValidateAndGetSafeFileName(
            string fileName)
        {
            if (string.IsNullOrWhiteSpace(
                    fileName))
            {
                throw new IOException(
                    "The received filename is empty.");
            }

            if (fileName !=
                Path.GetFileName(fileName))
            {
                throw new IOException(
                    "The received filename contains a path.");
            }

            if (fileName == "." ||
                fileName == "..")
            {
                throw new IOException(
                    "The received filename is invalid.");
            }

            foreach (char character in fileName)
            {
                if (char.IsControl(character))
                {
                    throw new IOException(
                        "The received filename contains " +
                        "invalid control characters.");
                }
            }

            char[] invalidCharacters =
                Path.GetInvalidFileNameChars();

            if (fileName.IndexOfAny(
                    invalidCharacters) >= 0)
            {
                throw new IOException(
                    "The received filename contains invalid characters.");
            }

            return fileName;
        }

        private static bool HasEnoughDiskSpace(
            string folder,
            long requiredBytes)
        {
            try
            {
                DirectoryInfo directory =
                    new DirectoryInfo(folder);

                string root =
                    directory.Root.FullName;

                DriveInfo drive =
                    new DriveInfo(root);

                const long safetyMargin =
                    50L * 1024L * 1024L;

                long requiredWithMargin;

                try
                {
                    requiredWithMargin =
                        checked(
                            requiredBytes +
                            safetyMargin);
                }
                catch (OverflowException)
                {
                    return false;
                }

                return
                    drive.AvailableFreeSpace >=
                    requiredWithMargin;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsurePathInsideFolder(
            string folder,
            string filePath)
        {
            string fullFolder =
                Path.GetFullPath(folder)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string fullFile =
                Path.GetFullPath(filePath);

            if (!fullFile.StartsWith(
                    fullFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The destination path is outside " +
                    "the LANShare download folder.");
            }
        }

        // ============================================================
        // FAILURE RESPONSES
        // ============================================================

        private static async Task
            SendTransferRejectionAsync(
                SslStream sslStream,
                string requestId)
        {
            if (string.IsNullOrWhiteSpace(
                    requestId))
            {
                return;
            }

            try
            {
                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                TransferResponse response =
                    new TransferResponse
                    {
                        RequestId =
                            requestId,

                        Accepted = false
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        response),
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5),
                    "Sending transfer rejection timed out.");
            }
            catch
            {
            }
        }

        private static async Task
            TrySendFailureAcknowledgementAsync(
                SslStream sslStream,
                string requestId,
                string message)
        {
            if (string.IsNullOrWhiteSpace(
                    requestId))
            {
                return;
            }

            try
            {
                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                TransferCompletionResponse failure =
                    new TransferCompletionResponse
                    {
                        RequestId =
                            requestId,

                        Verified = false,

                        Sha256 = "",

                        Message =
                            message
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        failure),
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5),
                    "Sending failure acknowledgement timed out.");
            }
            catch
            {
            }
        }

        private static void DeleteFileSafely(
            string? filePath)
        {
            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
            }
        }

        // ============================================================
        // PAIRING SERVER
        // ============================================================

        private void TransferServer_PairRequested(
            SslStream sslStream,
            TcpClient client,
            PairingRequest request)
        {
            _ = HandlePairingRequestAsync(
                sslStream,
                client,
                request);
        }

        private async Task HandlePairingRequestAsync(
            SslStream sslStream,
            TcpClient client,
            PairingRequest request)
        {
            try
            {
                if (request == null ||
                    string.IsNullOrWhiteSpace(
                        request.RequestId) ||
                    string.IsNullOrWhiteSpace(
                        request.DeviceName) ||
                    string.IsNullOrWhiteSpace(
                        request.CertificateBase64))
                {
                    client.Close();
                    return;
                }

                byte[] certificateBytes =
                    Convert.FromBase64String(
                        request.CertificateBase64);

                X509Certificate2 senderCertificate =
                    X509CertificateLoader.LoadCertificate(
                        certificateBytes);

                string senderFingerprint =
                    senderCertificate.GetCertHashString(
                        HashAlgorithmName.SHA256);

                using StreamReader reader =
                    new StreamReader(
                        sslStream,
                        Encoding.UTF8,
                        false,
                        4096,
                        true);

                using StreamWriter writer =
                    new StreamWriter(
                        sslStream,
                        Encoding.UTF8,
                        4096,
                        true);

                byte[] challenge =
                    RandomNumberGenerator.GetBytes(
                        32);

                PairingChallenge pairingChallenge =
                    new PairingChallenge
                    {
                        RequestId =
                            request.RequestId,

                        ChallengeBase64 =
                            Convert.ToBase64String(
                                challenge)
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        pairingChallenge),
                    CancellationToken.None,
                    PairingChallengeTimeout,
                    "Sending pairing challenge timed out.");

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        $"Verifying pairing request " +
                        $"from {request.DeviceName}...";
                });

                string? proofJson =
                    await ReadLineWithTimeoutAsync(
                        reader,
                        CancellationToken.None,
                        IncomingPairingProofTimeout,
                        "Waiting for pairing proof timed out.");

                if (string.IsNullOrWhiteSpace(
                        proofJson))
                {
                    client.Close();
                    return;
                }

                PairingProof? proof =
                    JsonSerializer.Deserialize<PairingProof>(
                        proofJson);

                if (proof == null ||
                    proof.RequestId !=
                        request.RequestId)
                {
                    client.Close();
                    return;
                }

                byte[] signature =
                    Convert.FromBase64String(
                        proof.SignatureBase64);

                using RSA? publicKey =
                    senderCertificate.GetRSAPublicKey();

                if (publicKey == null)
                {
                    client.Close();
                    return;
                }

                bool validSignature =
                    publicKey.VerifyData(
                        challenge,
                        signature,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                if (!validSignature)
                {
                    PairingResponse failedResponse =
                        new PairingResponse
                        {
                            RequestId =
                                request.RequestId,

                            Accepted = false,

                            CertificateFingerprint =
                                senderFingerprint,

                            Message =
                                "Cryptographic identity verification failed."
                        };

                    await WriteLineWithTimeoutAsync(
                        writer,
                        JsonSerializer.Serialize(
                            failedResponse),
                        CancellationToken.None,
                        PairingApprovalTimeout,
                        "Sending pairing rejection timed out.");

                    client.Close();
                    return;
                }

                bool accepted = false;

                Dispatcher.Invoke(() =>
                {
                    MessageBoxResult result =
                        MessageBox.Show(
                            $"A device wants to pair with this PC.\n\n" +
                            $"Device:\n{request.DeviceName}\n\n" +
                            $"Verified certificate fingerprint:\n\n" +
                            $"{FormatFingerprint(senderFingerprint)}\n\n" +
                            "The device proved possession of its " +
                            "private key.\n\n" +
                            "Allow pairing?",
                            "LANShare Device Pairing",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                    accepted =
                        result ==
                        MessageBoxResult.Yes;
                });

                if (accepted)
                {
                    bool saved =
                        pairingManager.AddOrUpdate(
                            request.DeviceName,
                            senderFingerprint);

                    if (!saved)
                        accepted = false;
                }

                X509Certificate2 localCertificate =
                    DeviceCertificate.GetOrCreateCertificate();

                PairingResponse response =
                    new PairingResponse
                    {
                        RequestId =
                            request.RequestId,

                        Accepted =
                            accepted,

                        CertificateFingerprint =
                            DeviceCertificate.GetFingerprint(
                                localCertificate),

                        Message =
                            accepted
                                ? "Pairing approved."
                                : "Pairing rejected or blocked because " +
                                  "the device already has a different " +
                                  "trusted certificate."
                    };

                await WriteLineWithTimeoutAsync(
                    writer,
                    JsonSerializer.Serialize(
                        response),
                    CancellationToken.None,
                    PairingApprovalTimeout,
                    "Sending pairing response timed out.");

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        accepted
                            ? $"Paired with {request.DeviceName}."
                            : "Pairing request rejected.";
                });
            }
            catch (TimeoutException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        "Pairing request timed out.";
                });

                System.Diagnostics.Debug.WriteLine(
                    ex);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        "Pairing request failed.";
                });

                System.Diagnostics.Debug.WriteLine(
                    ex);
            }
            finally
            {
                try
                {
                    sslStream.Close();
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

        // ============================================================
        // CERTIFICATE PINNING
        // ============================================================

        private static bool ValidatePinnedCertificate(
            X509Certificate? certificate,
            string expectedFingerprint,
            out string? presentedFingerprint)
        {
            presentedFingerprint = null;

            if (certificate == null)
                return false;

            try
            {
                using X509Certificate2 certificate2 =
                    X509CertificateLoader.LoadCertificate(
                        certificate.Export(
                            X509ContentType.Cert));

                presentedFingerprint =
                    certificate2.GetCertHashString(
                        HashAlgorithmName.SHA256);

                if (string.IsNullOrWhiteSpace(
                        expectedFingerprint))
                {
                    return false;
                }

                return string.Equals(
                    expectedFingerprint,
                    presentedFingerprint,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                presentedFingerprint = null;
                return false;
            }
        }

        // ============================================================
        // UI STATE
        // ============================================================

        private void UpdatePairingStatus()
        {
            if (IsTransferInProgress())
            {
                PairButton.IsEnabled = false;
                UnpairButton.IsEnabled = false;
                return;
            }

            PeerInfo? peer =
                GetSelectedPeer();

            if (peer == null)
            {
                PairingStatusText.Text =
                    "Select a device";

                PairButton.IsEnabled = false;
                UnpairButton.IsEnabled = false;

                return;
            }

            if (!peer.IsOnline)
            {
                PairingStatusText.Text =
                    "Device offline";

                PairButton.IsEnabled = false;

                bool offlinePaired =
                    !string.IsNullOrWhiteSpace(
                        peer.CertificateFingerprint) &&
                    pairingManager.IsPaired(
                        peer.Name,
                        peer.CertificateFingerprint);

                UnpairButton.IsEnabled =
                    offlinePaired;

                return;
            }

            string? fingerprint =
                peer.CertificateFingerprint;

            if (string.IsNullOrWhiteSpace(
                    fingerprint))
            {
                PairingStatusText.Text =
                    "Certificate fingerprint unavailable";

                PairButton.IsEnabled = false;
                UnpairButton.IsEnabled = false;

                return;
            }

            bool paired =
                pairingManager.IsPaired(
                    peer.Name,
                    fingerprint);

            if (paired)
            {
                PairingStatusText.Text =
                    "✓ Paired and trusted";

                PairButton.Content =
                    "Paired";

                PairButton.IsEnabled =
                    false;

                UnpairButton.IsEnabled =
                    true;
            }
            else
            {
                PairingStatusText.Text =
                    "Not paired — pair this device before transferring.";

                PairButton.Content =
                    "Pair";

                PairButton.IsEnabled =
                    true;

                UnpairButton.IsEnabled =
                    false;
            }
        }

        private void UpdateSendButton()
        {
            if (IsTransferInProgress())
            {
                SendButton.IsEnabled = false;
                return;
            }

            bool hasFile =
                !string.IsNullOrWhiteSpace(
                    selectedFile);

            PeerInfo? peer =
                GetSelectedPeer();

            bool paired =
                peer != null &&
                peer.IsOnline &&
                !string.IsNullOrWhiteSpace(
                    peer.CertificateFingerprint) &&
                pairingManager.IsPaired(
                    peer.Name,
                    peer.CertificateFingerprint);

            SendButton.IsEnabled =
                hasFile &&
                peer != null &&
                peer.IsOnline &&
                paired;
        }

        // ============================================================
        // PEER HELPERS
        // ============================================================

        private PeerInfo? FindPeer(
            string deviceName,
            string ipAddress)
        {
            return discoveredPeers.FirstOrDefault(
                peer =>
                    string.Equals(
                        peer.Name,
                        deviceName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        peer.IpAddress,
                        ipAddress,
                        StringComparison.OrdinalIgnoreCase));
        }

        private string? FindPeerFingerprint(
            string deviceName,
            string ipAddress)
        {
            PeerInfo? peer =
                FindPeer(
                    deviceName,
                    ipAddress);

            return peer?.CertificateFingerprint;
        }

        // ============================================================
        // GENERAL HELPERS
        // ============================================================

        private static bool IsValidSha256(
            string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash) ||
                hash.Length != 64)
            {
                return false;
            }

            foreach (char character in hash)
            {
                bool valid =
                    (character >= '0' &&
                     character <= '9') ||

                    (character >= 'A' &&
                     character <= 'F') ||

                    (character >= 'a' &&
                     character <= 'f');

                if (!valid)
                    return false;
            }

            return true;
        }

        private static string FormatFileSize(
            long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} bytes";

            double size =
                bytes;

            string[] units =
            {
                "bytes",
                "KB",
                "MB",
                "GB",
                "TB"
            };

            int unitIndex = 0;

            while (size >= 1024 &&
                   unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return
                $"{size:F2} {units[unitIndex]}";
        }

        private void StatusTextOnDispatcher(
            string text)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = text;
            });
        }

        private void MessageBoxOnDispatcher(
            string message,
            string title,
            MessageBoxImage image)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.OK,
                    image);
            });
        }

        private static string FormatFingerprint(
            string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(
                    fingerprint))
            {
                return "";
            }

            StringBuilder builder =
                new StringBuilder();

            string normalized =
                fingerprint
                    .Replace(":", "")
                    .Replace(" ", "")
                    .Trim();

            for (int i = 0;
                 i < normalized.Length;
                 i++)
            {
                if (i > 0 &&
                    i % 2 == 0)
                {
                    builder.Append(':');
                }

                builder.Append(
                    normalized[i]);
            }

            return builder.ToString();
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
                Path.GetExtension(
                    fileName);

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

        // ============================================================
        // SHUTDOWN
        // ============================================================

        protected override void OnClosed(
            EventArgs e)
        {
            try
            {
                activeTransferCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                discoveryCts.Cancel();
            }
            catch
            {
            }

            try
            {
                transferCts.Cancel();
            }
            catch
            {
            }

            try
            {
                peerDiscovery.Stop();
            }
            catch
            {
            }

            try
            {
                transferServer.Stop();
            }
            catch
            {
            }

            base.OnClosed(e);
        }
    }
}