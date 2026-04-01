using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for display helpers — FormatP2P, ShortAddress, FormatBytes,
/// FormatExpiry, FormatUptime, and ParseChainDuration.
/// </summary>
public class HelpersTests
{
    // ─── FormatP2P ───

    [Fact]
    public void FormatP2P_OneMillion_ReturnsOnePTwoP()
    {
        var result = Helpers.FormatP2P(1_000_000);
        Assert.Equal("1.00 P2P", result);
    }

    [Fact]
    public void FormatP2P_Zero_ReturnsZero()
    {
        var result = Helpers.FormatP2P(0);
        Assert.Equal("0.00 P2P", result);
    }

    [Theory]
    [InlineData(500_000, 2, "0.50 P2P")]
    [InlineData(1_500_000, 2, "1.50 P2P")]
    [InlineData(123_456_789, 2, "123.46 P2P")]
    [InlineData(100, 2, "0.00 P2P")]
    public void FormatP2P_VariousAmounts(long udvpn, int decimals, string expected)
    {
        var result = Helpers.FormatP2P(udvpn, decimals);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatP2P_WithCustomDecimals()
    {
        var result = Helpers.FormatP2P(40_152_030, 2);
        Assert.Equal("40.15 P2P", result);
    }

    [Fact]
    public void FormatP2P_FourDecimals()
    {
        var result = Helpers.FormatP2P(1_234_567, 4);
        Assert.Equal("1.2346 P2P", result);
    }

    [Fact]
    public void FormatP2P_LargeAmount()
    {
        // 1 billion udvpn = 1000 P2P
        var result = Helpers.FormatP2P(1_000_000_000);
        Assert.Equal("1000.00 P2P", result);
    }

    [Fact]
    public void FormatP2P_VerySmallAmount()
    {
        // 1 udvpn = 0.000001 P2P, rounds to 0.00 at 2 decimals
        var result = Helpers.FormatP2P(1, 2);
        Assert.Equal("0.00 P2P", result);
    }

    [Fact]
    public void FormatP2P_VerySmallAmountWithHighPrecision()
    {
        var result = Helpers.FormatP2P(1, 6);
        Assert.Equal("0.000001 P2P", result);
    }

    [Fact]
    public void FormatP2P_AlwaysContainsP2P()
    {
        var result = Helpers.FormatP2P(42_000_000);
        Assert.Contains("P2P", result);
        Assert.DoesNotContain("DVPN", result);
        Assert.DoesNotContain("dvpn", result);
    }

    // ─── ShortAddress ───

    [Fact]
    public void ShortAddress_TruncatesLongAddress()
    {
        var addr = "sent1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc5lzv7xu";
        var result = Helpers.ShortAddress(addr);

        Assert.StartsWith("sent1", result);
        Assert.Contains("...", result);
        Assert.True(result.Length < addr.Length);
    }

    [Fact]
    public void ShortAddress_ShortStringUnchanged()
    {
        var shortAddr = "sent1abc";
        var result = Helpers.ShortAddress(shortAddr);

        Assert.Equal(shortAddr, result);
    }

    [Fact]
    public void ShortAddress_EmptyStringReturnsEmpty()
    {
        var result = Helpers.ShortAddress("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ShortAddress_PreservesPrefix()
    {
        var sentAddr = "sent1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc5lzv7xu";
        var nodeAddr = "sentnode1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc5abc";

        var sentResult = Helpers.ShortAddress(sentAddr);
        var nodeResult = Helpers.ShortAddress(nodeAddr);

        Assert.StartsWith("sent1", sentResult);
        Assert.StartsWith("sentnode1", nodeResult);
    }

    [Fact]
    public void ShortAddress_PreservesSuffix()
    {
        var addr = "sent1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc5lzv7xu";
        var result = Helpers.ShortAddress(addr);

        // Default suffix is 6 characters
        var suffix = addr[^6..];
        Assert.EndsWith(suffix, result);
    }

    [Theory]
    [InlineData(null)]
    public void ShortAddress_NullReturnsEmptyOrNull(string? input)
    {
        var result = Helpers.ShortAddress(input ?? "");
        Assert.True(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void ShortAddress_CustomPrefixAndSuffixLength()
    {
        var addr = "sent1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc5lzv7xu";
        var result = Helpers.ShortAddress(addr, 10, 6);

        // Should be prefix(10) + "..." + suffix(6)
        Assert.Equal(10 + 3 + 6, result.Length);
    }

    // ─── FormatBytes ───

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500, "500 B")]
    [InlineData(1023, "1023 B")]
    public void FormatBytes_ByteRange(long bytes, string expected)
    {
        var result = Helpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10.0 KB")]
    public void FormatBytes_KilobyteRange(long bytes, string expected)
    {
        var result = Helpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(10485760, "10.0 MB")]
    [InlineData(524288000, "500.0 MB")]
    public void FormatBytes_MegabyteRange(long bytes, string expected)
    {
        var result = Helpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(5368709120, "5.0 GB")]
    [InlineData(10737418240, "10.0 GB")]
    public void FormatBytes_GigabyteRange(long bytes, string expected)
    {
        var result = Helpers.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBytes_TerabyteRange()
    {
        var result = Helpers.FormatBytes(1099511627776);
        Assert.Equal("1.0 TB", result);
    }

    [Fact]
    public void FormatBytes_NegativeReturnsZero()
    {
        var result = Helpers.FormatBytes(-100);
        Assert.Equal("0 B", result);
    }

    // ─── FormatExpiry ───

    [Fact]
    public void FormatExpiry_PastDate_ReturnsExpired()
    {
        var pastDate = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var result = Helpers.FormatExpiry(pastDate);

        Assert.Equal("expired", result);
    }

    [Fact]
    public void FormatExpiry_FutureDate_ReturnsTimeRemaining()
    {
        var futureDate = DateTimeOffset.UtcNow.AddDays(30).ToString("o");
        var result = Helpers.FormatExpiry(futureDate);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.NotEqual("expired", result);
    }

    [Fact]
    public void FormatExpiry_FutureDate_ContainsLeftSuffix()
    {
        var futureDate = DateTimeOffset.UtcNow.AddDays(5).ToString("o");
        var result = Helpers.FormatExpiry(futureDate);

        // Should contain "left" suffix (e.g. "5d left", "4h left")
        Assert.Contains("left", result);
    }

    [Fact]
    public void FormatExpiry_EmptyString_ReturnsUnknown()
    {
        var result = Helpers.FormatExpiry("");
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void FormatExpiry_InvalidDate_ReturnsUnknown()
    {
        var result = Helpers.FormatExpiry("not-a-date");
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void FormatExpiry_DistantFuture_ShowsDaysLeft()
    {
        var futureDate = DateTimeOffset.UtcNow.AddDays(365).ToString("o");
        var result = Helpers.FormatExpiry(futureDate);

        // Should contain "d left" (e.g. "365d left" or "364d left")
        Assert.Contains("d left", result);
    }

    [Fact]
    public void FormatExpiry_HoursAway_ShowsHoursLeft()
    {
        var futureDate = DateTimeOffset.UtcNow.AddHours(5).ToString("o");
        var result = Helpers.FormatExpiry(futureDate);

        // Should contain "h left" (e.g. "4h left" or "5h left")
        Assert.Contains("h left", result);
    }

    [Fact]
    public void FormatExpiry_MinutesAway_ShowsMinutesLeft()
    {
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(30).ToString("o");
        var result = Helpers.FormatExpiry(futureDate);

        // Should contain "m left" (e.g. "29m left")
        Assert.Contains("m left", result);
    }

    // ─── FormatUptime ───

    [Fact]
    public void FormatUptime_DaysAndHours()
    {
        var result = Helpers.FormatUptime(TimeSpan.FromHours(50));
        Assert.Equal("2d 2h", result);
    }

    [Fact]
    public void FormatUptime_HoursAndMinutes()
    {
        var result = Helpers.FormatUptime(TimeSpan.FromMinutes(135));
        Assert.Equal("2h 15m", result);
    }

    [Fact]
    public void FormatUptime_MinutesOnly()
    {
        var result = Helpers.FormatUptime(TimeSpan.FromMinutes(45));
        Assert.Equal("45m", result);
    }

    [Fact]
    public void FormatUptime_Zero()
    {
        var result = Helpers.FormatUptime(TimeSpan.Zero);
        Assert.Equal("0m", result);
    }

    // ─── ParseChainDuration ───

    [Fact]
    public void ParseChainDuration_StandardFormat()
    {
        // 557817.727s = 154.949 hours
        var result = Helpers.ParseChainDuration("557817.727s");

        Assert.True(result.Seconds > 557817 && result.Seconds < 557818,
            $"Expected ~557817.727 seconds, got {result.Seconds}");
        Assert.True(result.Hours >= 154 && result.Hours <= 155,
            $"Expected ~154 hours, got {result.Hours}");
    }

    [Fact]
    public void ParseChainDuration_WholeSeconds()
    {
        // 3600s = exactly 1 hour
        var result = Helpers.ParseChainDuration("3600s");

        Assert.Equal(3600.0, result.Seconds, 2);
        Assert.Equal(1, result.Hours);
        Assert.Equal(0, result.Minutes);
    }

    [Fact]
    public void ParseChainDuration_SmallValue()
    {
        // 60s = 1 minute
        var result = Helpers.ParseChainDuration("60s");

        Assert.Equal(60.0, result.Seconds, 2);
        Assert.Equal(0, result.Hours);
        Assert.Equal(1, result.Minutes);
    }

    [Fact]
    public void ParseChainDuration_Zero()
    {
        var result = Helpers.ParseChainDuration("0s");

        Assert.Equal(0, result.Seconds);
        Assert.Equal(0, result.Hours);
        Assert.Equal(0, result.Minutes);
    }

    [Fact]
    public void ParseChainDuration_LargeValue()
    {
        // 86400s = 24 hours
        var result = Helpers.ParseChainDuration("86400s");

        Assert.Equal(86400.0, result.Seconds, 2);
        Assert.Equal(24, result.Hours);
    }

    [Fact]
    public void ParseChainDuration_WithDecimal()
    {
        var result = Helpers.ParseChainDuration("1.5s");

        Assert.Equal(1.5, result.Seconds, 2);
    }

    [Fact]
    public void ParseChainDuration_EmptyString_ReturnsZero()
    {
        var result = Helpers.ParseChainDuration("");

        Assert.Equal(0, result.Seconds);
        Assert.Equal(0, result.Hours);
        Assert.Equal(0, result.Minutes);
        Assert.Equal("0m", result.Formatted);
    }

    [Fact]
    public void ParseChainDuration_InvalidFormat_ReturnsZero()
    {
        var result = Helpers.ParseChainDuration("invalid");

        Assert.Equal(0, result.Seconds);
        Assert.Equal(0, result.Hours);
        Assert.Equal(0, result.Minutes);
        Assert.Equal("0m", result.Formatted);
    }

    [Fact]
    public void ParseChainDuration_FormattedOutput()
    {
        // 3600s = "1h 0m"
        var result = Helpers.ParseChainDuration("3600s");
        Assert.Equal("1h 0m", result.Formatted);
    }

    [Fact]
    public void ParseChainDuration_FormattedOutput_MinutesOnly()
    {
        // 300s = 5 minutes = "5m"
        var result = Helpers.ParseChainDuration("300s");
        Assert.Equal("5m", result.Formatted);
    }

    [Theory]
    [InlineData("7200s", 2)]
    [InlineData("10800s", 3)]
    [InlineData("36000s", 10)]
    public void ParseChainDuration_MultipleValues(string input, int expectedHours)
    {
        var result = Helpers.ParseChainDuration(input);
        Assert.Equal(expectedHours, result.Hours);
    }

    // ─── Edge Cases ───

    [Fact]
    public void FormatP2P_MaxLongValue_DoesNotThrow()
    {
        // Ensure no overflow
        var result = Helpers.FormatP2P(long.MaxValue);
        Assert.Contains("P2P", result);
    }

    [Fact]
    public void FormatBytes_MaxLongValue_DoesNotThrow()
    {
        var result = Helpers.FormatBytes(long.MaxValue);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
