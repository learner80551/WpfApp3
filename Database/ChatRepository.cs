using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using WpfApp3.Chat;

namespace WpfApp3.Database
{
    public static class ChatRepository
    {
        private const int MaxHistoryMessages = 200;

        public static void Save(ChatMessage msg)
        {
            try
            {
                using SqliteConnection conn = AppDatabase.OpenConnection();
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO ChatMessages " +
                    "(MessageId,SenderDeviceId,SenderUsername,Message,Timestamp) " +
                    "VALUES (@mi,@sd,@su,@ms,@ts);";
                cmd.Parameters.AddWithValue("@mi", msg.MessageId);
                cmd.Parameters.AddWithValue("@sd", msg.SenderDeviceId);
                cmd.Parameters.AddWithValue("@su", msg.SenderUsername);
                cmd.Parameters.AddWithValue("@ms", msg.Text);
                cmd.Parameters.AddWithValue("@ts", msg.Timestamp.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static List<ChatMessage> LoadRecent()
        {
            var list = new List<ChatMessage>();
            try
            {
                using SqliteConnection conn = AppDatabase.OpenConnection();
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT MessageId,SenderDeviceId,SenderUsername,Message,Timestamp " +
                    "FROM ChatMessages ORDER BY Timestamp DESC LIMIT @lim;";
                cmd.Parameters.AddWithValue("@lim", MaxHistoryMessages);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new ChatMessage
                    {
                        MessageId      = r.GetString(0),
                        SenderDeviceId = r.GetString(1),
                        SenderUsername = r.GetString(2),
                        Text           = r.GetString(3),
                        Timestamp      = DateTime.Parse(r.GetString(4))
                    });
                }
                list.Reverse(); // oldest first
            }
            catch { }
            return list;
        }
    }
}
