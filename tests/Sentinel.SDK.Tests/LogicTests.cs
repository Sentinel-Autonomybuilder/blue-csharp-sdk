using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Pure logic tests — matches Go 45 + JS 75. NO chain calls, NO network.
/// </summary>
public class LogicTests
{
    // ═══ FORMAT P2P (3) ═══
    [Theory]
    [InlineData(40152030, "40.15 P2P")]
    [InlineData(1000000, "1.00 P2P")]
    [InlineData(0, "0.00 P2P")]
    public void FormatP2P(long udvpn, string expected) =>
        Assert.Equal(expected, Helpers.FormatP2P(udvpn));

    // ═══ FORMAT BYTES (4) ═══
    [Fact] public void FormatBytes_GB() => Assert.Contains("GB", Helpers.FormatBytes(1_500_000_000));
    [Fact] public void FormatBytes_MB() => Assert.Contains("MB", Helpers.FormatBytes(250_000_000));
    [Fact] public void FormatBytes_KB() => Assert.Contains("KB", Helpers.FormatBytes(50_000));
    [Fact] public void FormatBytes_Zero() => Assert.Equal("0 B", Helpers.FormatBytes(0));

    // ═══ FORMAT UPTIME (4) ═══
    [Fact] public void FormatUptime_Hours() => Assert.Contains("h", Helpers.FormatUptime(TimeSpan.FromMilliseconds(7350000)));
    [Fact] public void FormatUptime_Minutes() => Assert.Contains("m", Helpers.FormatUptime(TimeSpan.FromMilliseconds(90000)));
    [Fact] public void FormatUptime_Zero() => Assert.Equal("0m", Helpers.FormatUptime(TimeSpan.Zero));

    // ═══ SHORT ADDRESS (2) ═══
    [Fact] public void ShortAddress_Truncates() => Assert.Contains("...", Helpers.ShortAddress("sent1example9pqrse8q4m6lz8alxqv5hkx3fkxe0q"));
    [Fact] public void ShortAddress_Short() => Assert.Equal("sent1abc", Helpers.ShortAddress("sent1abc"));

    // ═══ COUNTRY MAP (11) ═══
    [Theory]
    [InlineData("The Netherlands", "NL")]
    [InlineData("Türkiye", "TR")]
    [InlineData("DR Congo", "CD")]
    [InlineData("Czechia", "CZ")]
    [InlineData("Russian Federation", "RU")]
    [InlineData("Viet Nam", "VN")]
    [InlineData("South Korea", "KR")]
    [InlineData("UAE", "AE")]
    [InlineData("us", "US")]
    public void CountryNameToCode(string name, string expected) =>
        Assert.Equal(expected, Constants.CountryNameToCode(name));

    [Fact] public void CountryNameToCode_Unknown() => Assert.Null(Constants.CountryNameToCode("Atlantis"));
    [Fact] public void CountryNameToCode_Null() => Assert.Null(Constants.CountryNameToCode(null));

    // ═══ FLAG URL (2) ═══
    [Fact] public void GetFlagUrl_US() => Assert.Contains("flagcdn.com", Constants.GetFlagUrl("US"));
    [Fact] public void GetFlagUrl_Null() => Assert.Equal("", Constants.GetFlagUrl(null));

    // ═══ DNS PRESETS (11) ═══
    [Fact] public void DnsDefault() => Assert.Equal("handshake", Constants.DnsPresets.DefaultPreset);
    [Fact] public void Dns3Presets() => Assert.Equal(3, Constants.DnsPresets.All.Count);
    [Fact] public void DnsFallbackOrder3() => Assert.Equal(3, Constants.DnsPresets.FallbackOrder.Length);
    [Fact] public void DnsHandshakeHasFallback() { var r = Constants.DnsPresets.Resolve("handshake"); Assert.Contains("103.196.38.38", r); Assert.Contains("8.8.8.8", r); Assert.Contains("1.1.1.1", r); }
    [Fact] public void DnsGoogleStartsWith() => Assert.StartsWith("8.8.8.8", Constants.DnsPresets.Resolve("google"));
    [Fact] public void DnsGoogleHasHandshake() => Assert.Contains("103.196.38.38", Constants.DnsPresets.Resolve("google"));
    [Fact] public void DnsCloudflareStartsWith() => Assert.StartsWith("1.1.1.1", Constants.DnsPresets.Resolve("cloudflare"));
    [Fact] public void DnsCustomHasFallbacks() => Assert.Contains("103.196.38.38", Constants.DnsPresets.Resolve("9.9.9.9"));
    [Fact] public void DnsNoDuplicates() { var s = Constants.DnsPresets.Resolve().Split(", "); Assert.Equal(s.Length, s.Distinct().Count()); }
    [Fact] public void DnsCaseInsensitive() => Assert.Equal(Constants.DnsPresets.Resolve("google"), Constants.DnsPresets.Resolve("GOOGLE"));
    [Fact] public void DnsHandshakeServers() { var p = Constants.DnsPresets.Handshake; Assert.Equal("103.196.38.38", p.Servers[0]); Assert.Equal("103.196.38.39", p.Servers[1]); }

    // ═══ ERROR CODES (15) ═══
    [Fact] public void ErrorCodes_Count() => Assert.True(typeof(ErrorCodes).GetFields().Length >= 20);
    [Fact] public void UserMsg_InsufficientBalance() => Assert.Contains("P2P", Helpers.UserMessage("INSUFFICIENT_BALANCE"));
    [Fact] public void UserMsg_NodeOffline() => Assert.Contains("offline", Helpers.UserMessage("NODE_OFFLINE"));
    [Fact] public void UserMsg_V2RayNotFound() => Assert.Contains("V2Ray", Helpers.UserMessage("V2RAY_NOT_FOUND"));
    [Fact] public void UserMsg_Aborted() => Assert.Contains("cancelled", Helpers.UserMessage("ABORTED"));
    [Fact] public void UserMsg_ChainLag() => Assert.Contains("confirmed", Helpers.UserMessage("CHAIN_LAG"));
    [Fact] public void UserMsg_InvalidAssignedIP() => Assert.Contains("invalid", Helpers.UserMessage("INVALID_ASSIGNED_IP"));
    [Fact] public void UserMsg_Unknown() => Assert.Equal("An unexpected error occurred.", Helpers.UserMessage("FAKE"));
    [Fact] public void UserMsg_WgNotAvailable() => Assert.Contains("WireGuard", Helpers.UserMessage("WG_NOT_AVAILABLE"));
    [Fact] public void UserMsg_TlsCertChanged() => Assert.Contains("certificate", Helpers.UserMessage("TLS_CERT_CHANGED"));
    [Fact] public void UserMsg_AllNodesFailed() => Assert.Contains("network", Helpers.UserMessage("ALL_NODES_FAILED"));
    [Fact] public void UserMsg_InvalidMnemonic() => Assert.Contains("12 or 24", Helpers.UserMessage("INVALID_MNEMONIC"));
    [Fact] public void UserMsg_SessionPoisoned() => Assert.Contains("poisoned", Helpers.UserMessage("SESSION_POISONED"));
    [Fact] public void UserMsg_NodeDatabaseCorrupt() => Assert.Contains("corrupted", Helpers.UserMessage("NODE_DATABASE_CORRUPT"));
    [Fact] public void UserMsg_BroadcastFailed() => Assert.Contains("balance", Helpers.UserMessage("BROADCAST_FAILED"));

    // ═══ APP TYPES (6) ═══
    [Fact] public void AppTypes_3() => Assert.Equal(3, Constants.AppTypes.All.Length);
    [Fact] public void AppTypes_WhiteLabel() => Assert.Equal("white_label", Constants.AppTypes.WhiteLabel);
    [Fact] public void AppTypes_DirectP2P() => Assert.Equal("direct_p2p", Constants.AppTypes.DirectP2P);
    [Fact] public void AppTypes_AllInOne() => Assert.Equal("all_in_one", Constants.AppTypes.AllInOne);
    [Fact] public void GbOptions() { Assert.Contains(1, Constants.GbOptions); Assert.Contains(50, Constants.GbOptions); }
    [Fact] public void HourOptions() { Assert.Contains(1, Constants.HourOptions); Assert.Contains(24, Constants.HourOptions); }

    // ═══ PRICE ENTRY (3) ═══
    [Fact] public void PriceEntry_UdvpnAmount() => Assert.Equal(40152030, new PriceEntry("udvpn", "0.00004", "40152030").UdvpnAmount);
    [Fact] public void PriceEntry_DisplayPrice() => Assert.Contains("P2P", new PriceEntry("udvpn", "0", "40152030").DisplayPrice);
    [Fact] public void PriceEntry_ZeroQuote() => Assert.Equal(0, new PriceEntry("udvpn", "0", "0").UdvpnAmount);

    // ═══ ESTIMATE SESSION PRICE (2) ═══
    [Fact]
    public void EstimateGB()
    {
        var node = new ChainNode("test", ["1.2.3.4:8585"], "1.2.3.4:8585",
            [new PriceEntry("udvpn", "0", "40152030")], [new PriceEntry("udvpn", "0", "18384000")], 1);
        var r = Helpers.EstimateSessionPrice(node, "gb", 5);
        Assert.True(r.CostUdvpn > 0);
        Assert.Equal("GB", r.Unit);
    }

    [Fact]
    public void EstimateHour()
    {
        var node = new ChainNode("test", ["1.2.3.4:8585"], "1.2.3.4:8585",
            [new PriceEntry("udvpn", "0", "40152030")], [new PriceEntry("udvpn", "0", "18384000")], 1);
        var r = Helpers.EstimateSessionPrice(node, "hour", 4);
        Assert.True(r.CostUdvpn > 0);
        Assert.Equal("hours", r.Unit);
    }

    // ═══ PARSE CHAIN ERROR (3) ═══
    [Fact] public void ParseChainError_Sequence() => Assert.Contains("sequence", Helpers.ParseChainError("account sequence mismatch"));
    [Fact] public void ParseChainError_Funds() => Assert.Contains("Insufficient", Helpers.ParseChainError("insufficient funds"));
    [Fact] public void ParseChainError_Empty() => Assert.Contains("no error", Helpers.ParseChainError(""));

    // ═══ CHAIN DURATION (2) ═══
    [Fact] public void ParseChainDuration_Valid() { var (s, h, m, f) = Helpers.ParseChainDuration("557817.72s"); Assert.True(s > 0); Assert.Contains("h", f); }
    [Fact] public void ParseChainDuration_Zero() { var (s, _, _, f) = Helpers.ParseChainDuration("0s"); Assert.Equal(0, s); }

    // ═══ VALIDATE CIDR (3) ═══
    [Fact] public void ValidateCIDR_IPv4() => Assert.True(Helpers.ValidateCIDR("10.8.0.1/32"));
    [Fact] public void ValidateCIDR_IPv6() => Assert.True(Helpers.ValidateCIDR("fd00::/64"));
    [Fact] public void ValidateCIDR_Invalid() => Assert.False(Helpers.ValidateCIDR("not-a-cidr"));

    // ═══ FORMAT EXPIRY (2) ═══
    [Fact] public void FormatExpiry_Future() => Assert.Contains("left", Helpers.FormatExpiry(DateTime.UtcNow.AddDays(10).ToString("O")));
    [Fact] public void FormatExpiry_Past() => Assert.Equal("expired", Helpers.FormatExpiry(DateTime.UtcNow.AddDays(-1).ToString("O")));

    // ═══ SESSION ALLOCATION (3) ═══
    [Fact]
    public void Allocation_GB()
    {
        var s = new ChainSession("1", "a", "b", "500000000", "100000000", "1000000000", "44s", "0s", "active", null, null);
        var a = Helpers.ComputeSessionAllocation(s);
        Assert.Equal(60.0, a.UsedPercent);
        Assert.True(a.IsGbBased);
    }

    [Fact]
    public void Allocation_Hourly()
    {
        var s = new ChainSession("1", "a", "b", "100", "50", "1000000000", "10s", "3600s", "active", null, null);
        var a = Helpers.ComputeSessionAllocation(s);
        Assert.True(a.IsHourlyBased);
    }

    [Fact]
    public void Allocation_Displays()
    {
        var s = new ChainSession("1", "a", "b", "1500000000", "500000000", "5000000000", null, "0s", "active", null, null);
        var a = Helpers.ComputeSessionAllocation(s);
        Assert.Contains("GB", a.UsedDisplay);
        Assert.Contains("GB", a.MaxDisplay);
    }
}
