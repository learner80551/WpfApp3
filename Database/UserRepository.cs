using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WpfApp3.Database;

namespace WpfApp3.Database
{
    public class UserRecord
    {
        public long Id { get; set; }
        public string Username { get; set; } = "";
        public string Role { get; set; } = "User";
        public string PasswordHash { get; set; } = "";
        public string PasswordSalt { get; set; } = "";
        public bool ForcePasswordReset { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class DeviceRecord
    {
        public long Id { get; set; }
        public string DeviceId { get; set; } = "";
        public long UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? CertificateFingerprint { get; set; }
        public bool IsAuthorized { get; set; }
        public bool IsRevoked { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        // Navigation
        public string Username { get; set; } = "";
        public string Role { get; set; } = "User";
        public bool UserIsActive { get; set; }
    }

    public static class UserRepository
    {
        // ──────────────────────────────────────────────────
        // QUERIES
        // ──────────────────────────────────────────────────

        public static bool AdminExists()
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Users WHERE Role='Admin';";
            long count = (long)(cmd.ExecuteScalar() ?? 0L);
            return count > 0;
        }

        public static UserRecord? FindByUsername(string username)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id,Username,Role,PasswordHash,PasswordSalt," +
                "ForcePasswordReset,IsActive,CreatedAt,UpdatedAt,LastLoginAt " +
                "FROM Users WHERE Username=@u LIMIT 1;";
            cmd.Parameters.AddWithValue("@u", username);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? ReadUser(r) : null;
        }

        public static UserRecord? FindById(long id)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id,Username,Role,PasswordHash,PasswordSalt," +
                "ForcePasswordReset,IsActive,CreatedAt,UpdatedAt,LastLoginAt " +
                "FROM Users WHERE Id=@id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", id);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? ReadUser(r) : null;
        }

        public static List<UserRecord> GetAllUsers()
        {
            var list = new List<UserRecord>();
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id,Username,Role,PasswordHash,PasswordSalt," +
                "ForcePasswordReset,IsActive,CreatedAt,UpdatedAt,LastLoginAt " +
                "FROM Users ORDER BY Role DESC, Username;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadUser(r));
            return list;
        }

        // ──────────────────────────────────────────────────
        // MUTATIONS
        // ──────────────────────────────────────────────────

        public static long CreateUser(
            string username, string role,
            string passwordHash, string passwordSalt)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            string now = DateTime.UtcNow.ToString("O");
            cmd.CommandText =
                "INSERT INTO Users " +
                "(Username,Role,PasswordHash,PasswordSalt,ForcePasswordReset,IsActive,CreatedAt,UpdatedAt) " +
                "VALUES (@u,@r,@ph,@ps,0,1,@ca,@ua); " +
                "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@u",  username);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@ph", passwordHash);
            cmd.Parameters.AddWithValue("@ps", passwordSalt);
            cmd.Parameters.AddWithValue("@ca", now);
            cmd.Parameters.AddWithValue("@ua", now);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }

        public static bool UpdatePassword(
            long userId, string newHash, string newSalt,
            bool forceReset = false)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Users SET PasswordHash=@ph, PasswordSalt=@ps, " +
                "ForcePasswordReset=@fr, UpdatedAt=@ua WHERE Id=@id;";
            cmd.Parameters.AddWithValue("@ph", newHash);
            cmd.Parameters.AddWithValue("@ps", newSalt);
            cmd.Parameters.AddWithValue("@fr", forceReset ? 1 : 0);
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", userId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool SetActive(long userId, bool active)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Users SET IsActive=@a, UpdatedAt=@ua WHERE Id=@id;";
            cmd.Parameters.AddWithValue("@a",  active ? 1 : 0);
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", userId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool RecordLogin(long userId)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Users SET LastLoginAt=@ll, UpdatedAt=@ua WHERE Id=@id;";
            cmd.Parameters.AddWithValue("@ll", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@ua", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", userId);
            return cmd.ExecuteNonQuery() > 0;
        }

        // ──────────────────────────────────────────────────
        // DEVICE RECORDS
        // ──────────────────────────────────────────────────

        public static string CreateDevice(
            string deviceId, long userId, string displayName,
            string? certFingerprint)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            string now = DateTime.UtcNow.ToString("O");
            cmd.CommandText =
                "INSERT INTO Devices " +
                "(DeviceId,UserId,DisplayName,CertificateFingerprint," +
                "IsAuthorized,IsRevoked,CreatedAt) " +
                "VALUES (@di,@ui,@dn,@cf,1,0,@ca);";
            cmd.Parameters.AddWithValue("@di", deviceId);
            cmd.Parameters.AddWithValue("@ui", userId);
            cmd.Parameters.AddWithValue("@dn", displayName);
            cmd.Parameters.AddWithValue("@cf", (object?)certFingerprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ca", now);
            cmd.ExecuteNonQuery();
            return deviceId;
        }

        public static DeviceRecord? FindDevice(string deviceId)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT d.Id,d.DeviceId,d.UserId,d.DisplayName,d.CertificateFingerprint," +
                "d.IsAuthorized,d.IsRevoked,d.CreatedAt,d.LastSeenAt," +
                "u.Username,u.Role,u.IsActive " +
                "FROM Devices d JOIN Users u ON u.Id=d.UserId " +
                "WHERE d.DeviceId=@di LIMIT 1;";
            cmd.Parameters.AddWithValue("@di", deviceId);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? ReadDevice(r) : null;
        }

        public static DeviceRecord? FindDeviceByUserId(long userId)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT d.Id,d.DeviceId,d.UserId,d.DisplayName,d.CertificateFingerprint," +
                "d.IsAuthorized,d.IsRevoked,d.CreatedAt,d.LastSeenAt," +
                "u.Username,u.Role,u.IsActive " +
                "FROM Devices d JOIN Users u ON u.Id=d.UserId " +
                "WHERE d.UserId=@uid ORDER BY d.Id LIMIT 1;";
            cmd.Parameters.AddWithValue("@uid", userId);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? ReadDevice(r) : null;
        }

        public static List<DeviceRecord> GetAllDevices()
        {
            var list = new List<DeviceRecord>();
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT d.Id,d.DeviceId,d.UserId,d.DisplayName,d.CertificateFingerprint," +
                "d.IsAuthorized,d.IsRevoked,d.CreatedAt,d.LastSeenAt," +
                "u.Username,u.Role,u.IsActive " +
                "FROM Devices d JOIN Users u ON u.Id=d.UserId " +
                "ORDER BY u.Role DESC, u.Username;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadDevice(r));
            return list;
        }

        public static bool SetDeviceRevoked(string deviceId, bool revoked)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Devices SET IsRevoked=@r, IsAuthorized=@a " +
                "WHERE DeviceId=@di;";
            cmd.Parameters.AddWithValue("@r",  revoked ? 1 : 0);
            cmd.Parameters.AddWithValue("@a",  revoked ? 0 : 1);
            cmd.Parameters.AddWithValue("@di", deviceId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool UpdateDeviceCertFingerprint(
            string deviceId, string fingerprint)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Devices SET CertificateFingerprint=@cf, LastSeenAt=@ls " +
                "WHERE DeviceId=@di;";
            cmd.Parameters.AddWithValue("@cf", fingerprint);
            cmd.Parameters.AddWithValue("@ls", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@di", deviceId);
            return cmd.ExecuteNonQuery() > 0;
        }

        public static bool UpdateDeviceLastSeen(string deviceId)
        {
            using SqliteConnection conn = AppDatabase.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE Devices SET LastSeenAt=@ls WHERE DeviceId=@di;";
            cmd.Parameters.AddWithValue("@ls", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@di", deviceId);
            return cmd.ExecuteNonQuery() > 0;
        }

        // ──────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────

        private static UserRecord ReadUser(SqliteDataReader r) => new()
        {
            Id                = r.GetInt64(0),
            Username          = r.GetString(1),
            Role              = r.GetString(2),
            PasswordHash      = r.GetString(3),
            PasswordSalt      = r.GetString(4),
            ForcePasswordReset= r.GetInt64(5) != 0,
            IsActive          = r.GetInt64(6) != 0,
            CreatedAt         = DateTime.Parse(r.GetString(7)),
            UpdatedAt         = DateTime.Parse(r.GetString(8)),
            LastLoginAt       = r.IsDBNull(9) ? null : DateTime.Parse(r.GetString(9))
        };

        private static DeviceRecord ReadDevice(SqliteDataReader r) => new()
        {
            Id                    = r.GetInt64(0),
            DeviceId              = r.GetString(1),
            UserId                = r.GetInt64(2),
            DisplayName           = r.GetString(3),
            CertificateFingerprint= r.IsDBNull(4) ? null : r.GetString(4),
            IsAuthorized          = r.GetInt64(5) != 0,
            IsRevoked             = r.GetInt64(6) != 0,
            CreatedAt             = DateTime.Parse(r.GetString(7)),
            LastSeenAt            = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)),
            Username              = r.GetString(9),
            Role                  = r.GetString(10),
            UserIsActive          = r.GetInt64(11) != 0
        };
    }
}
