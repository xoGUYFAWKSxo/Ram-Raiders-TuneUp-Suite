using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TuneUpEngine
{
    public class Winapp2Downloader
    {
        // The canonical community winapp2.ini (the previous BleachBit URL returns 404).
        private const string Winapp2Url = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";
        private readonly HttpClient _httpClient;

        public event Action<string>? OnDownloadLog;

        public Winapp2Downloader()
        {
            _httpClient = new HttpClient();
            // User-Agent is required by GitHub API/content endpoints to prevent 403 Forbidden
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RamRaidersTuneUp/2026");
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<bool> SyncLatestDefinitionsAsync(string destinationPath)
        {
            OnDownloadLog?.Invoke("[Cleaner Update] Initiating online winapp2.ini definitions update sync...");

            try
            {
                var response = await _httpClient.GetAsync(Winapp2Url);
                if (!response.IsSuccessStatusCode)
                {
                    OnDownloadLog?.Invoke($"[Cleaner Update] Server returned status error: {response.StatusCode}");
                    return false;
                }

                string content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(content) || !content.Contains("[") || !content.Contains("]"))
                {
                    OnDownloadLog?.Invoke("[Cleaner Update] Downloaded file contains invalid or corrupted headers.");
                    return false;
                }

                // Ensure directory exists
                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Write file to disk
                await File.WriteAllTextAsync(destinationPath, content);
                
                long sizeKb = new FileInfo(destinationPath).Length / 1024;
                OnDownloadLog?.Invoke($"[Cleaner Update] Winapp2.ini synced successfully! Size: {sizeKb} KB. Thousands of community rules loaded.");
                return true;
            }
            catch (Exception ex)
            {
                OnDownloadLog?.Invoke($"[Cleaner Update] Failed to download winapp2.ini: {ex.Message}");
                return false;
            }
        }
    }
}
