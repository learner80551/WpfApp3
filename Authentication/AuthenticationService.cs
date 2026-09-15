using System;
using WpfApp3.Database;
using WpfApp3.Identity;

namespace WpfApp3.Authentication
{
    public enum LoginResultCode
    {
        Success,
        InvalidCredentials,
        AccountDisabled,
        DeviceNotAuthorized,
        DeviceRevoked,
        ForcePasswordReset
    }

    public class LoginResult
    {
        public LoginResultCode Code    { get; init; }
        public AppSession?     Session { get; init; }
        public string          Message { get; init; } = "";

        public bool IsSuccess => Code == LoginResultCode.Success ||
                                 Code == LoginResultCode.ForcePasswordReset;
    }

    public static class AuthenticationService
    {
        // ──────────────────────────────────────────────────
        // FIRST RUN
        // ──────────────────────────────────────────────────

        public static bool IsFirstRun() => !UserRepository.AdminExists();

        /// <summary>
        /// Creates the initial administrator account.
        /// Called only once during first-run setup.
        /// </summary>
        public static (bool Success, string Error, string DeviceId) CreateAdmin(
            string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return (false, "Username is required.", "");

            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                return (false, "Password must be at least 8 characters.", "");

            if (UserRepository.FindByUsername(username) != null)
                return (false, "Username already exists.", "");

            var (hash, salt) = PasswordHasher.HashPassword(password);

            long userId = UserRepository.CreateUser(username, "Admin", hash, salt);

            string deviceId = DeviceIdentity.GetOrCreateDeviceId();
            string? fingerprint = DeviceIdentity.GetCertificateFingerprint();

            UserRepository.CreateDevice(deviceId, userId, Environment.MachineName, fingerprint);

            AuditRepository.Log(
                action: "AdminCreated",
                actorUsername: username,
                actorDeviceId: deviceId,
                target: username,
                result: "Success");

            return (true, "", deviceId);
        }

        // ──────────────────────────────────────────────────
        // LOGIN
        // ──────────────────────────────────────────────────

        public static LoginResult Login(
            string username, string password, string deviceId)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                return Fail(LoginResultCode.InvalidCredentials, "Credentials are required.");
            }

            UserRecord? user = UserRepository.FindByUsername(username);

            if (user == null)
            {
                // Perform dummy hash to prevent username enumeration via timing
                PasswordHasher.VerifyPassword(
                    password,
                    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
                AuditRepository.Log("LoginFailure", username, deviceId,
                    username, "UserNotFound");
                return Fail(LoginResultCode.InvalidCredentials, "Invalid credentials.");
            }

            if (!user.IsActive)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    username, "AccountDisabled");
                return Fail(LoginResultCode.AccountDisabled,
                    "This account is disabled. Contact your administrator.");
            }

            bool passwordOk = PasswordHasher.VerifyPassword(
                password, user.PasswordHash, user.PasswordSalt);

            if (!passwordOk)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    username, "WrongPassword");
                return Fail(LoginResultCode.InvalidCredentials, "Invalid credentials.");
            }

            // Device authorization check
            DeviceRecord? device = UserRepository.FindDevice(deviceId);

            if (device == null)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    deviceId, "DeviceNotFound");
                return Fail(LoginResultCode.DeviceNotAuthorized,
                    "This device is not authorized. Contact your administrator.");
            }

            if (device.UserId != user.Id)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    deviceId, "DeviceUserMismatch");
                return Fail(LoginResultCode.DeviceNotAuthorized,
                    "This device is not registered to this account.");
            }

            if (device.IsRevoked)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    deviceId, "DeviceRevoked");
                return Fail(LoginResultCode.DeviceRevoked,
                    "This device has been revoked. Contact your administrator.");
            }

            if (!device.IsAuthorized)
            {
                AuditRepository.Log("LoginFailure", username, deviceId,
                    deviceId, "DeviceNotAuthorized");
                return Fail(LoginResultCode.DeviceNotAuthorized,
                    "This device is not authorized. Contact your administrator.");
            }

            // Update cert fingerprint in DB so admin can see it
            string? currentFingerprint = DeviceIdentity.GetCertificateFingerprint();
            if (!string.IsNullOrWhiteSpace(currentFingerprint))
                UserRepository.UpdateDeviceCertFingerprint(deviceId, currentFingerprint);

            UserRepository.RecordLogin(user.Id);
            UserRepository.UpdateDeviceLastSeen(deviceId);

            var session = SessionManager.CreateSession(
                user.Id, user.Username, user.Role, deviceId);

            AuditRepository.Log("LoginSuccess", username, deviceId,
                username, "Success");

            if (user.ForcePasswordReset)
            {
                return new LoginResult
                {
                    Code    = LoginResultCode.ForcePasswordReset,
                    Session = session,
                    Message = "Your password has been reset. Please choose a new password."
                };
            }

            return new LoginResult
            {
                Code    = LoginResultCode.Success,
                Session = session,
                Message = "Login successful."
            };
        }

        // ──────────────────────────────────────────────────
        // PASSWORD CHANGE
        // ──────────────────────────────────────────────────

        public static (bool Success, string Error) ChangePassword(
            long userId, string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
                return (false, "New password must be at least 8 characters.");

            UserRecord? user = UserRepository.FindById(userId);
            if (user == null)
                return (false, "User not found.");

            if (!PasswordHasher.VerifyPassword(currentPassword, user.PasswordHash, user.PasswordSalt))
                return (false, "Current password is incorrect.");

            var (newHash, newSalt) = PasswordHasher.HashPassword(newPassword);
            bool ok = UserRepository.UpdatePassword(userId, newHash, newSalt, forceReset: false);

            AuditRepository.Log("PasswordChanged",
                user.Username,
                SessionManager.CurrentSession?.DeviceId,
                user.Username,
                ok ? "Success" : "Failed");

            return ok ? (true, "") : (false, "Password update failed.");
        }

        // ──────────────────────────────────────────────────
        // ADMIN: RESET ACCESS
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Admin sets a new temporary password for a user and forces
        /// them to change it on next login. The admin does NOT see
        /// the new password after this method returns.
        /// Returns the temporary password to hand to the user.
        /// </summary>
        public static (bool Success, string Error, string TempPassword) AdminResetAccess(
            long adminUserId, long targetUserId)
        {
            AppSession? session = SessionManager.CurrentSession;
            if (session == null || session.UserId != adminUserId || !session.IsAdmin)
                return (false, "Unauthorized.", "");

            UserRecord? target = UserRepository.FindById(targetUserId);
            if (target == null)
                return (false, "User not found.", "");

            // Generate a random temporary password
            string tempPassword = GenerateTemporaryPassword();

            var (hash, salt) = PasswordHasher.HashPassword(tempPassword);
            bool ok = UserRepository.UpdatePassword(targetUserId, hash, salt, forceReset: true);

            AuditRepository.Log("AdminResetAccess",
                session.Username, session.DeviceId,
                target.Username, ok ? "Success" : "Failed");

            return ok
                ? (true, "", tempPassword)
                : (false, "Reset failed.", "");
        }

        private static string GenerateTemporaryPassword()
        {
            const string chars =
                "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";
            var buf = new char[16];
            byte[] rnd = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            for (int i = 0; i < buf.Length; i++)
                buf[i] = chars[rnd[i] % chars.Length];
            return new string(buf);
        }

        // ──────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────

        private static LoginResult Fail(LoginResultCode code, string message) =>
            new() { Code = code, Message = message };
    }
}
