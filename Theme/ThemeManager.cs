using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace WpfApp3.Theme
{
    public static class ThemeManager
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LANShare", "settings.json");

        private static bool _isDark = true;
        public static bool IsDark => _isDark;

        // ──────────────────────────────────────────────────
        // PERSISTENCE
        // ──────────────────────────────────────────────────

        public static bool LoadThemePreference()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return true; // default dark
                string json = File.ReadAllText(SettingsPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("IsDark", out var el))
                    return el.GetBoolean();
            }
            catch { }
            return true;
        }

        public static void SaveThemePreference(bool isDark)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string json = JsonSerializer.Serialize(
                    new { IsDark = isDark },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        // ──────────────────────────────────────────────────
        // APPLY
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Applies dark or light theme by REPLACING resource entries with NEW brush
        /// instances — never mutates frozen brushes.
        /// Works on Application.Current.Resources so all windows inherit the theme.
        /// </summary>
        public static void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            SaveThemePreference(isDark);

            var res = Application.Current.Resources;

            if (isDark)
            {
                // ── Dark palette ───────────────────────────────────
                Set(res, "AppBackground",       "#1A1F2C");
                Set(res, "SidebarBackground",   "#14171F");
                Set(res, "CardBackground",       "#22283A");
                Set(res, "CardBorder",           "#2E3550");
                Set(res, "PrimaryText",          "#E8EAF0");
                Set(res, "SecondaryText",        "#8B93A8");
                Set(res, "AccentColor",          "#4F8EF7");
                Set(res, "AccentHover",          "#6FA3FF");
                Set(res, "ButtonBackground",     "#2E3550");
                Set(res, "ButtonForeground",     "#E8EAF0");
                Set(res, "InputBackground",      "#1E2435");
                Set(res, "InputBorder",          "#3A4060");
                Set(res, "InputForeground",      "#E8EAF0");
                Set(res, "SuccessColor",         "#4CAF82");
                Set(res, "DangerColor",          "#F75A5A");
                Set(res, "WarnColor",            "#F7A840");
                Set(res, "NavSelectedBg",        "#2A3050");
                Set(res, "NavSelectedFg",        "#4F8EF7");
                Set(res, "NavNormalFg",          "#8B93A8");
                Set(res, "ListItemHover",        "#2A3050");
                Set(res, "SeparatorColor",       "#2E3550");
                Set(res, "StatusBarBg",          "#0F1218");
                Set(res, "ChatBubbleSelf",       "#2A3A6A");
                Set(res, "ChatBubbleOther",      "#22283A");
            }
            else
            {
                // ── Light palette ─────────────────────────────────
                Set(res, "AppBackground",       "#F0F2F8");
                Set(res, "SidebarBackground",   "#FFFFFF");
                Set(res, "CardBackground",       "#FFFFFF");
                Set(res, "CardBorder",           "#DDE2EE");
                Set(res, "PrimaryText",          "#1A1F2C");
                Set(res, "SecondaryText",        "#5A6378");
                Set(res, "AccentColor",          "#2B6ECC");
                Set(res, "AccentHover",          "#1A5AB8");
                Set(res, "ButtonBackground",     "#EEF0F8");
                Set(res, "ButtonForeground",     "#1A1F2C");
                Set(res, "InputBackground",      "#FFFFFF");
                Set(res, "InputBorder",          "#C5CCDC");
                Set(res, "InputForeground",      "#1A1F2C");
                Set(res, "SuccessColor",         "#2E7D5A");
                Set(res, "DangerColor",          "#C0392B");
                Set(res, "WarnColor",            "#D4830A");
                Set(res, "NavSelectedBg",        "#EBF1FF");
                Set(res, "NavSelectedFg",        "#2B6ECC");
                Set(res, "NavNormalFg",          "#5A6378");
                Set(res, "ListItemHover",        "#EBF1FF");
                Set(res, "SeparatorColor",       "#DDE2EE");
                Set(res, "StatusBarBg",          "#E2E6F0");
                Set(res, "ChatBubbleSelf",       "#D6E4FF");
                Set(res, "ChatBubbleOther",      "#FFFFFF");
            }
        }

        private static void Set(ResourceDictionary res, string key, string hexColor)
        {
            // Always create a NEW SolidColorBrush — never mutate a frozen one.
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // Freeze for performance and thread safety
            res[key] = brush;
        }
    }
}
