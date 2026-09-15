using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using WpfApp3.Database;

namespace WpfApp3.Database
{
    public static class AuditRepository
    {
        public static void Log(
            string action,
            string? actorUsername = null,
            string? actorDeviceId = null,
            string? target = null,
            string? result = null)
        {
            try
            {
                using SqliteConnection conn = AppDatabase.OpenConnection();
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO AuditLogs " +
                    "(Timestamp,ActorUsername,ActorDeviceId,Action,Target,Result) " +
                    "VALUES (@ts,@au,@ad,@ac,@tg,@re);";
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@au", (object?)actorUsername ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ad", (object?)actorDeviceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ac", action);
                cmd.Parameters.AddWithValue("@tg", (object?)target ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@re", (object?)result ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Audit log failures must never crash the application.
            }
        }
    }
}
