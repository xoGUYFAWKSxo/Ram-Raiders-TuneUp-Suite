using System;
using System.Text.Json;

namespace TuneUpEngine
{
    // The engine's pipe protocol, shared by the engine and the UI (which references this assembly).
    // One JSON object per line in each direction.
    //
    // Command names live here as constants, so a typo is a compile error instead of an
    // "Unknown command" at runtime, and commands that take more than one value use typed payloads
    // (JSON) rather than "name|true"-style strings that broke on a '|' in a path.

    public class IpcRequest
    {
        public string Command { get; set; } = "";
        public string? Payload { get; set; }

        // Identifies this request on its connection, so the client can cancel it while it runs
        // (send IpcCommands.CancelRequest with this ID as the payload). Optional.
        public string? RequestId { get; set; }
    }

    public class IpcResponse
    {
        public string Status { get; set; } = ""; // IpcStatus.*
        public double ProgressPercent { get; set; }
        public string? Message { get; set; }
        public string? Payload { get; set; }
        public string? RequestId { get; set; }
    }

    public static class IpcStatus
    {
        public const string Success = "Success";
        public const string Error = "Error";
        public const string Progress = "Progress";
    }

    public static class IpcCommands
    {
        // Status / dashboard
        public const string GetEngineStats = "GetEngineStats";
        public const string ReportForeground = "ReportForeground";
        public const string SetGovernorState = "SetGovernorState";
        public const string SetAutoPurgeState = "SetAutoPurgeState";
        public const string SetGameModeState = "SetGameModeState";
        public const string PurgeStandbyList = "PurgeStandbyList";
        public const string ShutdownEngine = "ShutdownEngine";

        // Cleaner
        public const string ScanJunk = "ScanJunk";
        public const string ScanAdvancedEmptyFolders = "ScanAdvancedEmptyFolders";
        public const string ScanAdvancedDuplicates = "ScanAdvancedDuplicates";
        public const string CleanJunk = "CleanJunk";
        public const string UpdateCleanerDefinitions = "UpdateCleanerDefinitions";

        // Registry
        public const string ScanRegistry = "ScanRegistry";
        public const string RepairRegistry = "RepairRegistry";
        public const string GetRegistryTweaks = "GetRegistryTweaks";
        public const string SetRegistryTweak = "SetRegistryTweak";

        // Repair
        public const string GetSystemIntegrityStatus = "GetSystemIntegrityStatus";
        public const string RunSystemRepair = "RunSystemRepair";

        // Hardware / OS tweaks
        public const string CheckMsiCompliance = "CheckMsiCompliance";
        public const string ProvisionMSIInterrupts = "ProvisionMSIInterrupts";
        public const string RevertMSIInterrupts = "RevertMSIInterrupts";
        public const string GetCoreTopology = "GetCoreTopology";
        public const string GetDebloatStatus = "GetDebloatStatus";
        public const string SetServiceState = "SetServiceState";
        public const string RemoveAppxPackage = "RemoveAppxPackage";
        public const string GetNetworkOptimizationStatus = "GetNetworkOptimizationStatus";
        public const string ApplyNetworkOptimizations = "ApplyNetworkOptimizations";
        public const string RevertNetworkOptimizations = "RevertNetworkOptimizations";

        // Storage
        public const string AnalyzeMigration = "AnalyzeMigration";
        public const string MigrateDirectory = "MigrateDirectory";
        public const string UndoMigration = "UndoMigration";
        public const string GetMigrationHistory = "GetMigrationHistory";

        // Latency
        public const string StartLatencyAudit = "StartLatencyAudit";
        public const string StopLatencyAudit = "StopLatencyAudit";
        public const string GetLatencyReport = "GetLatencyReport";

        // Protocol: cancel a running request on the same connection (payload = its RequestId).
        public const string CancelRequest = "CancelRequest";
    }

    // ── Typed payloads ──────────────────────────────────────────────────────────

    public record SetTweakRequest(string Name, bool Enable);
    public record SetServiceRequest(string Name, bool Disable);
    public record MigrateRequest(string Source, string Destination);

    public static class IpcPayload
    {
        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

        // Returns null for a missing or malformed payload (the handler then replies with an error).
        public static T? Deserialize<T>(string? payload) where T : class
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try { return JsonSerializer.Deserialize<T>(payload); }
            catch (JsonException) { return null; }
        }
    }
}
