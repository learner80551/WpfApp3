using System;
using System.Collections.Generic;
using WpfApp3.Authentication;
using WpfApp3.Database;
using WpfApp3.Identity;

namespace WpfApp3.Admin
{
    public enum AdminActionResult
    {
        Success,
        Unauthorized,
        UserNotFound,
        DeviceNotFound,
        UsernameTaken,
        DeviceIdTaken,
        ValidationError,
        DatabaseError
    }

    public class AdminResult
    {
        public AdminActionResult Code    { get; init; }
        public string            Message { get; init; } = "";
        public bool IsSuccess => Code == AdminActionResult.Success;
    }

    public static class AdminService
    {
        private static bool IsAdmin(AppSession? session) =>
            session != null && session.IsAdmin;

        // ──────────────────────────────────────────────────
        // USER MANAGEMENT
        // ──────────────────────────────────────────────────

        public static AdminResult CreateUser(
            AppSession? session,
            string username,
            string deviceId,
            string initialPassword,
            string displayName = "")
        {
            if (!IsAdmin(session))
                return Fail(AdminActionResult.Unauthorized, "Admin access required.");

            if (string.IsNullOrWhiteSpace(username))
                return Fail(AdminActionResult.ValidationError, "Username is required.");

            if (string.IsNullOrWhiteSpace(deviceId))
                return Fail(AdminActionResult.ValidationError, "Device ID is required.");

            if (string.IsNullOrWhiteSpace(initialPassword) || initialPassword.Length < 8)
                return Fail(AdminActionResult.ValidationError, "Password must be at least 8 characters.");

            if (UserRepository.FindByUsername(username) != null)
                return Fail(AdminActionResult.UsernameTaken, $"Username '{username}' already exists.");

            if (UserRepository.FindDevice(deviceId) != null)
                return Fail(AdminActionResult.DeviceIdTaken, $"Device ID '{deviceId}' is already registered.");

            var (hash, salt) = Authentication.PasswordHasher.HashPassword(initialPassword);
            long userId = UserRepository.CreateUser(username, "User", hash, salt);

            string dn = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            UserRepository.CreateDevice(deviceId, userId, dn, null);

            AuditRepository.Log("UserCreated",
                session!.Username, session.DeviceId,
                username, "Success");

            return Ok($"User '{username}' created with device '{deviceId}'.");
        }

        public static AdminResult DisableUser(AppSession? session, long targetUserId)
        {
            if (!IsAdmin(session))
                return Fail(AdminActionResult.Unauthorized, "Admin access required.");

            UserRecord? target = UserRepository.FindById(targetUserId);
            if (target == null)
                return Fail(AdminActionResult.UserNotFound, "User not found.");

            // Prevent admin from disabling themselves
            if (target.Id == session!.UserId)
                return Fail(AdminActionResult.ValidationError, "You cannot disable your own account.");

            bool ok = UserRepository.SetActive(targetUserId, false);
            AuditRepository.Log("UserDisabled",
                session.Username, session.DeviceId,
                target.Username, ok ? "Success" : "Failed");

            return ok ? Ok($"User '{target.Username}' disabled.")
                      : Fail(AdminActionResult.DatabaseError, "Failed to disable user.");
        }

        public static AdminResult EnableUser(AppSession? session, long targetUserId)
        {
            if (!IsAdmin(session))
                return Fail(AdminActionResult.Unauthorized, "Admin access required.");

            UserRecord? target = UserRepository.FindById(targetUserId);
            if (target == null)
                return Fail(AdminActionResult.UserNotFound, "User not found.");

            bool ok = UserRepository.SetActive(targetUserId, true);
            AuditRepository.Log("UserEnabled",
                session!.Username, session.DeviceId,
                target.Username, ok ? "Success" : "Failed");

            return ok ? Ok($"User '{target.Username}' enabled.")
                      : Fail(AdminActionResult.DatabaseError, "Failed to enable user.");
        }

        public static AdminResult RevokeDevice(AppSession? session, string deviceId)
        {
            if (!IsAdmin(session))
                return Fail(AdminActionResult.Unauthorized, "Admin access required.");

            DeviceRecord? device = UserRepository.FindDevice(deviceId);
            if (device == null)
                return Fail(AdminActionResult.DeviceNotFound, "Device not found.");

            // Prevent admin from revoking their own device
            if (deviceId == session!.DeviceId)
                return Fail(AdminActionResult.ValidationError,
                    "You cannot revoke your own device.");

            bool ok = UserRepository.SetDeviceRevoked(deviceId, true);
            AuditRepository.Log("DeviceRevoked",
                session.Username, session.DeviceId,
                deviceId, ok ? "Success" : "Failed");

            return ok ? Ok($"Device '{deviceId}' revoked.")
                      : Fail(AdminActionResult.DatabaseError, "Failed to revoke device.");
        }

        public static AdminResult RestoreDevice(AppSession? session, string deviceId)
        {
            if (!IsAdmin(session))
                return Fail(AdminActionResult.Unauthorized, "Admin access required.");

            bool ok = UserRepository.SetDeviceRevoked(deviceId, false);
            AuditRepository.Log("DeviceRestored",
                session!.Username, session.DeviceId,
                deviceId, ok ? "Success" : "Failed");

            return ok ? Ok($"Device '{deviceId}' authorization restored.")
                      : Fail(AdminActionResult.DatabaseError, "Failed to restore device.");
        }

        /// <summary>
        /// Resets a user's password to a random temporary value and
        /// forces them to change it on next login.
        /// Returns the temporary password — the admin must convey this
        /// securely to the user (e.g., verbally).
        /// </summary>
        public static (AdminResult Result, string TempPassword) ResetUserAccess(
            AppSession? session, long targetUserId)
        {
            if (!IsAdmin(session))
                return (Fail(AdminActionResult.Unauthorized, "Admin access required."), "");

            var (ok, error, tempPwd) = AuthenticationService.AdminResetAccess(
                session!.UserId, targetUserId);

            return ok
                ? (Ok("Access reset. Provide the temporary password to the user."), tempPwd)
                : (Fail(AdminActionResult.DatabaseError, error), "");
        }

        // ──────────────────────────────────────────────────
        // QUERIES
        // ──────────────────────────────────────────────────

        public static List<UserRecord> GetAllUsers(AppSession? session)
        {
            if (!IsAdmin(session)) return new();
            return UserRepository.GetAllUsers();
        }

        public static List<DeviceRecord> GetAllDevices(AppSession? session)
        {
            if (!IsAdmin(session)) return new();
            return UserRepository.GetAllDevices();
        }

        // ──────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────

        private static AdminResult Ok(string msg) =>
            new() { Code = AdminActionResult.Success, Message = msg };

        private static AdminResult Fail(AdminActionResult code, string msg) =>
            new() { Code = code, Message = msg };
    }
}
