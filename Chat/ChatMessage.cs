using System;

namespace WpfApp3.Chat
{
    public class ChatMessage
    {
        public string   MessageId      { get; set; } = Guid.NewGuid().ToString();
        public string   SenderDeviceId { get; set; } = "";
        public string   SenderUsername { get; set; } = "";
        public string   Text           { get; set; } = "";
        public DateTime Timestamp      { get; set; } = DateTime.UtcNow;

        // The JSON type field used on the wire
        public string Type => "CHAT_MESSAGE";
    }

    /// <summary>Wire format for chat messages sent over the TLS channel.</summary>
    public class ChatMessagePacket
    {
        public string Type           { get; set; } = "CHAT_MESSAGE";
        public string MessageId      { get; set; } = Guid.NewGuid().ToString();
        public string SenderDeviceId { get; set; } = "";
        public string SenderUsername { get; set; } = "";
        public string Text           { get; set; } = "";
        public string Timestamp      { get; set; } = DateTime.UtcNow.ToString("O");
    }
}
