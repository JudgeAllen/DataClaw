using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace TwsHistoryGui
{
    /// <summary>Persisted GUI settings (JSON next to the exe).</summary>
    public sealed class AppSettings
    {
        public string DataDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TwsHistoryData");

        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7497;
        public int ClientId { get; set; } = 0;
        public string Timezone { get; set; } = "Asia/Shanghai";

        public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) return s;
                }
            }
            catch { /* fall through to defaults */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal */ }
        }
    }
}
