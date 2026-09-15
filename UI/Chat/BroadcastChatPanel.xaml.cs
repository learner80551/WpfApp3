using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfApp3.Authentication;
using WpfApp3.Chat;

namespace WpfApp3.UI.Chat
{
    public class ChatMessageViewModel
    {
        public string SenderUsername { get; set; } = "";
        public string SenderDeviceId { get; set; } = "";
        public string Text           { get; set; } = "";
        public string TimeDisplay    { get; set; } = "";
        public bool   IsSelf         { get; set; }
    }

    public partial class BroadcastChatPanel : UserControl
    {
        public BroadcastChatService? ChatService { get; set; }

        // Peer list delegate — set by MainWindow to provide current online peers
        public Func<IEnumerable<PeerInfo>>? GetOnlinePeers { get; set; }

        private readonly ObservableCollection<ChatMessageViewModel> _messages = new();

        public BroadcastChatPanel()
        {
            InitializeComponent();
            MessagesList.ItemsSource = _messages;
        }

        public void Initialize(BroadcastChatService chatService,
                               Func<IEnumerable<PeerInfo>> getOnlinePeers)
        {
            ChatService    = chatService;
            GetOnlinePeers = getOnlinePeers;

            // Subscribe to incoming messages
            chatService.MessageReceived += OnMessageReceived;

            // Load history
            var history = chatService.LoadHistory();
            foreach (var msg in history)
                AddMessageToUI(msg);
        }

        private void OnMessageReceived(ChatMessage msg)
        {
            Dispatcher.Invoke(() => AddMessageToUI(msg));
        }

        private void AddMessageToUI(ChatMessage msg)
        {
            string? selfDeviceId = SessionManager.CurrentSession?.DeviceId;
            bool isSelf = string.Equals(
                msg.SenderDeviceId, selfDeviceId,
                StringComparison.OrdinalIgnoreCase);

            _messages.Add(new ChatMessageViewModel
            {
                SenderUsername = msg.SenderUsername,
                SenderDeviceId = msg.SenderDeviceId,
                Text           = msg.Text,
                TimeDisplay    = msg.Timestamp.ToLocalTime().ToString("HH:mm"),
                IsSelf         = isSelf
            });

            // Auto-scroll to bottom
            MessagesScroll.ScrollToEnd();
        }

        private async void SendChatBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void ChatInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                await SendMessageAsync();
            }
        }

        private async System.Threading.Tasks.Task SendMessageAsync()
        {
            string text = ChatInputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (ChatService == null) return;
            if (SessionManager.CurrentSession == null) return;

            ChatInputBox.Clear();
            SendChatBtn.IsEnabled = false;

            try
            {
                IEnumerable<PeerInfo> peers =
                    GetOnlinePeers?.Invoke() ?? Array.Empty<PeerInfo>();

                await ChatService.SendToAllPeersAsync(text, peers);
            }
            catch { /* Chat failures are silent */ }
            finally
            {
                SendChatBtn.IsEnabled = true;
                ChatInputBox.Focus();
            }
        }
    }
}
