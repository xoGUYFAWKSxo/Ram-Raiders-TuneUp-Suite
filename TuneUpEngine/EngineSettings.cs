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

        // Engine (machine-wide) settings live in ProgramData next to the change journal. Older
        // versions kept them in the engine account's LocalAppData — SYSTEM's profile once the
        // engine runs as a service — so that file is read once as a fallback.
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RamRaidersTuneUpSuite", "engine_settings.json");

        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RamRaidersTuneUpSuite", "engine_settings.json");

        public static EngineSettings Load()
        {
            try
            {
                string path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
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
