using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WpfApp3.Database
{
    public static class AppDatabase
    {
        private static readonly string DatabaseDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LANShare");

        private static readonly string DatabasePath =
            Path.Combine(DatabaseDirectory, "navlan.db");

        public static string ConnectionString =>
            $"Data Source={DatabasePath};";

        public static void Initialize()
        {
            Directory.CreateDirectory(DatabaseDirectory);

            using SqliteConnection connection = new(ConnectionString);
            connection.Open();

            // Enable WAL for better concurrency
            using (SqliteCommand wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }

            // Enable foreign keys
            using (SqliteCommand fk = connection.CreateCommand())
            {
                fk.CommandText = "PRAGMA foreign_keys=ON;";
                fk.ExecuteNonQuery();
            }

            CreateSchema(connection);
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    Role TEXT NOT NULL DEFAULT 'User',
    PasswordHash TEXT NOT NULL,
    PasswordSalt TEXT NOT NULL,
    ForcePasswordReset INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LastLoginAt TEXT
);

CREATE TABLE IF NOT EXISTS Devices (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceId TEXT NOT NULL UNIQUE COLLATE NOCASE,
    UserId INTEGER NOT NULL,
    DisplayName TEXT NOT NULL,
    CertificateFingerprint TEXT,
    IsAuthorized INTEGER NOT NULL DEFAULT 1,
    IsRevoked INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    LastSeenAt TEXT,
    FOREIGN KEY (UserId) REFERENCES Users(Id)
);

CREATE TABLE IF NOT EXISTS ChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MessageId TEXT NOT NULL UNIQUE,
    SenderDeviceId TEXT NOT NULL,
    SenderUsername TEXT NOT NULL,
    Message TEXT NOT NULL,
    Timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    ActorUsername TEXT,
    ActorDeviceId TEXT,
    Action TEXT NOT NULL,
    Target TEXT,
    Result TEXT
);

CREATE INDEX IF NOT EXISTS idx_devices_userid ON Devices(UserId);
CREATE INDEX IF NOT EXISTS idx_chat_timestamp  ON ChatMessages(Timestamp);
CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON AuditLogs(Timestamp);
";
            cmd.ExecuteNonQuery();
        }

        public static SqliteConnection OpenConnection()
        {
            SqliteConnection connection = new(ConnectionString);
            connection.Open();

            using SqliteCommand fk = connection.CreateCommand();
            fk.CommandText = "PRAGMA foreign_keys=ON;";
            fk.ExecuteNonQuery();

            return connection;
        }
    }
}
