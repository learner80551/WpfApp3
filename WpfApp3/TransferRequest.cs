using System;

namespace WpfApp3
{
    public class TransferRequest
    {
        public string Type { get; set; } = "TRANSFER_REQUEST";

        public string FileName { get; set; } = "";

        public long FileSize { get; set; }

        public string SenderName { get; set; } = "";

        public string RequestId { get; set; } =
            Guid.NewGuid().ToString();
    }

    public class TransferResponse
    {
        public string Type { get; set; } = "TRANSFER_RESPONSE";

        public string RequestId { get; set; } = "";

        public bool Accepted { get; set; }
    }
}