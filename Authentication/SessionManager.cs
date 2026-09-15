using System;

namespace WpfApp3.Authentication
{
    public class AppSession
    {
        public long   UserId    { get; init; }
        public string Username  { get; init; } = "";
        public string Role      { get; init; } = "User";
        public string DeviceId  { get; init; } = "";
        public string SessionId { get; init; } = Guid.NewGuid().ToString();
        public DateTime CreatedAt  { get; init; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// In-memory singleton session. There is at most one active session at a time.
    /// The session is cleared on Logout.
    /// </summary>
    public static class SessionManager
    {
        private static AppSession? _current;
        private static readonly object _lock = new();

        public static AppSession? CurrentSession
        {
            get { lock (_lock) { return _current; } }
        }

        public static bool IsAuthenticated
        {
            get { lock (_lock) { return _current != null; } }
        }

        public static AppSession CreateSession(
            long userId, string username, string role, string deviceId)
        {
            var session = new AppSession
            {
                UserId   = userId,
                Username = username,
                Role     = role,
                DeviceId = deviceId
            };

            lock (_lock) { _current = session; }
            return session;
        }

        public static void InvalidateSession()
        {
            lock (_lock) { _current = null; }
        }

        public static void Touch()
        {
            lock (_lock)
            {
                if (_current != null)
                    _current.LastActivity = DateTime.UtcNow;
            }
        }
    }
}
