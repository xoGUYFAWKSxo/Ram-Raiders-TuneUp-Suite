using System;
using System.IO;
using System.Text.Json;

namespace TuneUpEngine
{
    // Persists the engine's feature-toggle state so choices survive a restart. Previously the engine
    // hard-coded ProBalance=on / AutoPurge=off / GameMode=on at every launch, so a user who turned a
    // feature off would find it back on after rebooting. These defaults match that historical
    // behaviour for a first run (no file yet).
    public class EngineSettings
    {
        public bool ProBalanceEnabled { get; set; } = true;
        public bool AutoPurgeEnabled { get; set; } = false;
        public bool GameModeEnabled { get; set; } = true;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RamRaidersTuneUpSuite", "engine_settings.json");

        public static EngineSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<EngineSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt or unreadable file — fall back to defaults rather than failing to start.
            }
            return new EngineSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Persisting settings is best-effort; never let it crash the engine.
            }
        }
    }
}
