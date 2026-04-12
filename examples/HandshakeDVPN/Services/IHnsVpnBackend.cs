// ─── Handshake dVPN Backend Interface ───
// No plan/subscription required. Direct connect to any node. Pay per GB or per hour.

namespace HandshakeDVPN.Services;

public interface IHnsVpnBackend : IDisposable
{
    // ─── Mode ───
    string? WalletAddress { get; }
    bool HasWallet { get; }

    // ─── Events ───
    event Action<string>? OnLog;
    event Action<string, string?>? OnProgress;

    // ─── Wallet ───
    Task<BalanceData?> GetBalanceAsync();
    Task<ImportData?> ImportWalletAsync(string mnemonic);
    Task<WalletData?> CreateWalletAsync(int strength = 128);

    // ─── Nodes ───
    Task<HnsNodesData?> GetAllNodesAsync(CancellationToken ct = default);

    // ─── Background enrichment ───
    event Action<List<HnsNodeInfo>>? OnNodesEnriched;

    // ─── Connection (direct — no plan) ───
    Task<ConnectData?> ConnectDirectAsync(string nodeAddress, int amount = 1, bool preferHourly = false);

    // ─── Subscriptions / Sessions ───
    Task<List<ActiveSession>?> GetActiveSessionsAsync();

    // ─── Plans ───
    Task<List<PlanInfo>?> DiscoverPlansAsync();
    Task<string?> SubscribeToPlanAsync(int planId);
    Task<ConnectData?> ConnectViaPlanAsync(ulong subscriptionId, string nodeAddress);
    Task<StatusData?> DisconnectAsync();
    Task<VpnStatusData?> GetStatusAsync();

    // ─── IP ───
    Task<string?> GetPublicIpAsync();

    // ─── Diagnostic ───
    Task<DiagnosticData?> GetDiagnosticAsync();
}

// ─── Data Models ───

public class BalanceData
{
    public long Udvpn { get; set; }
    public double P2P { get; set; }
    public string Display { get; set; } = "";
}

public class WalletData
{
    public string Address { get; set; } = "";
    public string Mnemonic { get; set; } = "";
}

public class ImportData
{
    public string Address { get; set; } = "";
    public bool Valid { get; set; }
}

public class HnsNodesData
{
    public List<HnsNodeInfo> Nodes { get; set; } = new();
    public int Total { get; set; }
}

public class HnsNodeInfo
{
    public string Address { get; set; } = "";
    public string? Moniker { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? ServiceType { get; set; }
    public int? Peers { get; set; }
    public double? ClockDriftSec { get; set; }
    public double? BandwidthDown { get; set; }
    public double? BandwidthUp { get; set; }

    // ─── Pricing ───
    public string? GbPriceUdvpn { get; set; }
    public string? GbPriceDisplay { get; set; }
    public string? HourlyPriceUdvpn { get; set; }
    public string? HourlyPriceDisplay { get; set; }
    public bool HasGbPrice => GbPriceDisplay != null;
    public bool HasHourlyPrice => HourlyPriceDisplay != null;
}

public class ConnectData
{
    public string Status { get; set; } = "";
    public string? SessionId { get; set; }
    public string? NodeAddress { get; set; }
    public string? Protocol { get; set; }
    public string? VpnIp { get; set; }
    public int? SocksPort { get; set; }
}

public class StatusData
{
    public string Status { get; set; } = "";
}

public class VpnStatusData
{
    public bool Connected { get; set; }
    public string? NodeAddress { get; set; }
    public string? SessionId { get; set; }
    public string? Protocol { get; set; }
    public long? UptimeMs { get; set; }
    public string? UptimeFormatted { get; set; }
    public string? VpnIp { get; set; }
}

// ─── Node Test Result ───

public class NodeTestResult
{
    public string Address { get; set; } = "";
    public string? Moniker { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Connected { get; set; }
    public string? Protocol { get; set; }
    public string? SessionId { get; set; }
    public double ConnectSeconds { get; set; }
    public double? SpeedMbps { get; set; }
    public string? SpeedMethod { get; set; }
    public string? Transport { get; set; }
    public bool? GoogleAccessible { get; set; }
    public int? GoogleLatencyMs { get; set; }
    public int? Peers { get; set; }
    public bool InPlan { get; set; }
    public double? ReportedBandwidth { get; set; }
    public bool Pass { get; set; }
    public bool Pass10Mbps => (SpeedMbps ?? 0) >= 10;
    public string? Error { get; set; }

    public string StatusDisplay => Pass ? $"{SpeedMbps:F1} Mbps" : Error ?? "Failed";
    public string ConnectDisplay => $"{ConnectSeconds:F1}s";
}

// ─── Test Run History ───

public class TestRunSummary
{
    public string FolderName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public int Run { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Fast { get; set; }
    public double AvgSpeed { get; set; }
    public double? Baseline { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string Duration { get; set; } = "";

    public string DisplayLabel => $"{FolderName}  —  {Total} nodes, {Passed} pass, {AvgSpeed:F1} Mbps avg";

    public static string RunsDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "runs");

    public static List<TestRunSummary> ScanAll()
    {
        var runs = new List<TestRunSummary>();
        var dir = RunsDir;
        if (!System.IO.Directory.Exists(dir)) return runs;
        foreach (var folder in System.IO.Directory.GetDirectories(dir).OrderByDescending(d => d))
        {
            var summaryPath = System.IO.Path.Combine(folder, "summary.json");
            if (!System.IO.File.Exists(summaryPath)) continue;
            try
            {
                var json = System.IO.File.ReadAllText(summaryPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                runs.Add(new TestRunSummary
                {
                    FolderName = System.IO.Path.GetFileName(folder),
                    FolderPath = folder,
                    Run = root.TryGetProperty("run", out var r) ? r.GetInt32() : 0,
                    Total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0,
                    Passed = root.TryGetProperty("passed", out var p) ? p.GetInt32() : 0,
                    Failed = root.TryGetProperty("failed", out var f) ? f.GetInt32() : 0,
                    Fast = root.TryGetProperty("fast", out var fa) ? fa.GetInt32() : 0,
                    AvgSpeed = root.TryGetProperty("avgSpeed", out var a) ? a.GetDouble() : 0,
                    Baseline = root.TryGetProperty("baseline", out var b) && b.ValueKind != System.Text.Json.JsonValueKind.Null ? b.GetDouble() : null,
                    StartTime = root.TryGetProperty("startTime", out var st) ? st.GetString() ?? "" : "",
                    EndTime = root.TryGetProperty("endTime", out var et) ? et.GetString() ?? "" : "",
                    Duration = root.TryGetProperty("duration", out var d) ? d.GetString() ?? "" : "",
                });
            }
            catch { /* corrupt summary — skip */ }
        }
        return runs;
    }

    public static List<NodeTestResult>? LoadResults(string folderPath)
    {
        var path = System.IO.Path.Combine(folderPath, "results.json");
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<List<NodeTestResult>>(json);
        }
        catch { return null; }
    }
}

public class DiagnosticData
{
    public bool Ok { get; set; }
    public List<DiagnosticIssue> Issues { get; set; } = new();
}

public class DiagnosticIssue
{
    public string Type { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public class PlanInfo
{
    public int Id { get; set; }
    public int Subscribers { get; set; }
    public int NodeCount { get; set; }
    public string PriceDisplay { get; set; } = "";
    public string PriceUdvpn { get; set; } = "";
    public bool IsSubscribed { get; set; }
    public string? SubscriptionId { get; set; }
    public bool HasFeeGrant { get; set; }
    public string? ExpiresAt { get; set; }
    public string? ExpiresDisplay { get; set; }
}

public class ActiveSession
{
    public string SessionId { get; set; } = "";
    public string NodeAddress { get; set; } = "";
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public long MaxBytes { get; set; }
    public string Status { get; set; } = "";
    public string? InactiveAt { get; set; }
    public string? SubscriptionPlanId { get; set; }
    public string PayMode { get; set; } = "gb"; // "gb" or "hr" — tracked locally
    public double UsedPercent => MaxBytes > 0 ? (DownloadBytes + UploadBytes) * 100.0 / MaxBytes : 0;
    public string UsedDisplay => FormatBytes(DownloadBytes + UploadBytes);
    public string MaxDisplay => MaxBytes > 0 ? FormatBytes(MaxBytes) : "time-based";
    public string RemainingDisplay
    {
        get
        {
            if (MaxBytes > 0) return FormatBytes(Math.Max(0, MaxBytes - DownloadBytes - UploadBytes));
            if (InactiveAt != null && DateTime.TryParse(InactiveAt, out var exp))
            {
                var left = exp - DateTime.UtcNow;
                if (left.TotalDays > 1) return $"{(int)left.TotalDays}d {left.Hours}h left";
                if (left.TotalHours > 1) return $"{(int)left.TotalHours}h {left.Minutes}m left";
                if (left.TotalMinutes > 0) return $"{(int)left.TotalMinutes}m left";
                return "expired";
            }
            return "active";
        }
    }
    public bool IsGbBased => MaxBytes > 0;

    private static string FormatBytes(long b)
    {
        if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:F2} GB";
        if (b >= 1_048_576) return $"{b / 1_048_576.0:F1} MB";
        if (b >= 1024) return $"{b / 1024.0:F0} KB";
        return $"{b} B";
    }
}

// ─── Local Session Tracker (persists payment mode per session) ───

public static class SessionTracker
{
    private static readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "session-modes.json");

    private static Dictionary<string, string> _modes = new();

    static SessionTracker()
    {
        try
        {
            if (System.IO.File.Exists(_path))
                _modes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    System.IO.File.ReadAllText(_path)) ?? new();
        }
        catch { _modes = new(); }
    }

    public static void Track(string sessionId, string mode)
    {
        _modes[sessionId] = mode;
        Save();
    }

    public static string GetMode(string sessionId) =>
        _modes.TryGetValue(sessionId, out var m) ? m : "gb";

    private static void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(_modes));
        }
        catch { }
    }
}

// ─── App Settings (persisted to disk) ───

public class AppSettings
{
    // ─── Network ───
    public string DnsPreset { get; set; } = "handshake";
    public string CustomDns { get; set; } = "";
    public string CustomLcd { get; set; } = "";
    public string CustomRpc { get; set; } = "";

    // ─── Tunnel ───
    public bool FullTunnel { get; set; } = true;
    public bool SystemProxy { get; set; } = true;
    public int WgMtu { get; set; } = 1280;
    public int WgKeepalive { get; set; } = 15;
    public int V2RaySocksPort { get; set; } = 10808;

    // ─── Session ───
    public int DefaultGb { get; set; } = 1;
    public bool PreferHourly { get; set; } = false;

    // ─── Polling Intervals (seconds) ───
    public int StatusPollSec { get; set; } = 3;
    public int IpCheckSec { get; set; } = 60;
    public int BalanceCheckSec { get; set; } = 300;
    public int AllocationCheckSec { get; set; } = 120;

    // ─── Plan Discovery ───
    public int PlanProbeMax { get; set; } = 500;

    public string GetDnsString() => DnsPreset switch
    {
        "handshake" => "103.196.38.38,103.196.38.39",
        "google" => "8.8.8.8,8.8.4.4",
        "cloudflare" => "1.1.1.1,1.0.0.1",
        "custom" => CustomDns,
        _ => "103.196.38.38,103.196.38.39",
    };

    public string GetDnsDisplay() => DnsPreset switch
    {
        "handshake" => "Handshake (103.196.38.38)",
        "google" => "Google (8.8.8.8)",
        "cloudflare" => "Cloudflare (1.1.1.1)",
        "custom" => $"Custom ({CustomDns})",
        _ => "Handshake",
    };

    private static readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "settings.json");

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(_path)) return new();
            return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(_path)) ?? new();
        }
        catch { return new(); }
    }
}
