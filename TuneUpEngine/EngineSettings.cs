using System;
using System.IO;
using System.Text.Json;

namespace TuneUpEngine
{
    // Persists the engine's feature-toggle state so choices survive a restart. Previously the engine
    // hard-coded ProBalance=on / AutoPurge=off / GameMode=on at every launch, so a user who turned a
    // feature off would find it back on after rebooting. On a first run (no file yet) everything is
    // off: Game Mode changes system settings and ProBalance changes other programs' priorities, so
    // the user switches them on rather than the install doing it for them.
    public class EngineSettings
    {
        public bool ProBalanceEnabled { get; set; } = false;
        public bool AutoPurgeEnabled { get; set; } = false;
        public bool GameModeEnabled { get; set; } = false;

        // Engine (machine-wide) settings live in ProgramData next to the change journal. Older
        // versions kept them in the engine account's LocalAppData — SYSTEM's profile once the
        // engine runs as a service — so that file is read once as a fallback.
        private static string SettingsPath => Path.Combine(
            AppFolders.Root(Environment.SpecialFolder.CommonApplicationData), "engine_settings.json");

        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolders.LegacyName, "engine_settings.json");

        public static EngineSettings Load() => Load(SettingsPath, LegacySettingsPath);

        // The legacy file is moved to ProgramData the first time it is read. Left in place, it was
        // read again whenever ProgramData had no settings (after an uninstall, which deletes them),
        // so a reinstall came back with ProBalance and Game Mode on (found in end-to-end testing).
        internal static EngineSettings Load(string settingsPath, string legacyPath)
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    // Already moved by an earlier version that left the old file behind.
                    if (File.Exists(legacyPath)) File.Delete(legacyPath);
                    var loaded = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(settingsPath));
                    if (loaded != null) return loaded;
                }
                else if (File.Exists(legacyPath))
                {
                    var legacy = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(legacyPath));
                    if (legacy != null && legacy.Save(settingsPath)) File.Delete(legacyPath);
                    if (legacy != null) return legacy;
                }
            }
            catch
            {
                // Corrupt or unreadable file — fall back to defaults rather than failing to start.
            }
            return new EngineSettings();
        }

        public void Save() => Save(SettingsPath);

        internal bool Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch
            {
                // Persisting settings is best-effort; never let it crash the engine.
                return false;
            }
        }
    }
}
