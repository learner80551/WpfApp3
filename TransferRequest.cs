using System;

namespace WpfApp3
{
    public class TransferRequest
    {
        public string Type { get; set; } =
            "TRANSFER_REQUEST";

        public string FileName { get; set; } = "";

        public long FileSize { get; set; }

        public string SenderName { get; set; } = "";

        public string Sha256 { get; set; } = "";

        public string RequestId { get; set; } =
            Guid.NewGuid().ToString();
    }

    // ============================================================
    // INITIAL TRANSFER RESPONSE
    // Receiver uses this to Accept or Reject the transfer.
    // ============================================================

    public class TransferResponse
    {
        public string Type { get; set; } =
            "TRANSFER_RESPONSE";

        public string RequestId { get; set; } = "";

        public bool Accepted { get; set; }
    }

    // ============================================================
    // TRANSFER COMPLETION RESPONSE
    //
    // Receiver sends this only AFTER:
    //
    // 1. All bytes were received
    // 2. SHA-256 was calculated
    // 3. SHA-256 was compared with the sender's hash
    //
    // The sender must receive Verified=true before it reports
    // the transfer as successfully completed.
    // ============================================================

    public class TransferCompletionResponse
    {
        public string Type { get; set; } =
            "TRANSFER_COMPLETION";

        public string RequestId { get; set; } = "";

        public bool Verified { get; set; }

        public string Sha256 { get; set; } = "";

        public string Message { get; set; } = "";
    }
}