using Sentinel.SDK.Core;
using Xunit;

#pragma warning disable CS0618 // Testing obsolete Mnemonic property intentionally

namespace Sentinel.SDK.Tests;

public class WalletTests
{
    [Fact]
    public void Generate_CreatesValidWallet()
    {
        var wallet = SentinelWallet.Generate();
        Assert.NotNull(wallet);
        Assert.StartsWith("sent1", wallet.Address);
        Assert.InRange(wallet.Address.Length, 42, 46);
        Assert.NotEmpty(wallet.Mnemonic);
        Assert.True(wallet.Mnemonic.Split(' ').Length >= 12);
    }

    [Fact]
    public void Generate_256Bit_Creates24Words()
    {
        var wallet = SentinelWallet.Generate(256);
        Assert.Equal(24, wallet.Mnemonic.Split(' ').Length);
    }

    [Fact]
    public void FromMnemonic_DerivesSameAddress()
    {
        var wallet1 = SentinelWallet.Generate();
        var wallet2 = SentinelWallet.FromMnemonic(wallet1.Mnemonic);
        Assert.Equal(wallet1.Address, wallet2.Address);
    }

    [Fact]
    public void FromMnemonic_RejectsInvalid()
    {
        Assert.Throws<SentinelException>(() => SentinelWallet.FromMnemonic("invalid"));
        Assert.Throws<SentinelException>(() => SentinelWallet.FromMnemonic(""));
    }

    [Fact]
    public void Sign_ProducesValidSignature()
    {
        var wallet = SentinelWallet.Generate();
        // uint256 requires exactly 32 bytes
        var message = new byte[32];
        Array.Fill(message, (byte)0xAB);
        var signature = wallet.Sign(message);
        Assert.NotNull(signature);
        Assert.Equal(64, signature.Length); // compact secp256k1 signature (r + s), no recovery byte
    }

    [Fact]
    public void GetPublicKeyCompressed_Returns33Bytes()
    {
        var wallet = SentinelWallet.Generate();
        var pubKey = wallet.GetPublicKeyCompressed();
        Assert.Equal(33, pubKey.Length);
        Assert.True(pubKey[0] == 0x02 || pubKey[0] == 0x03); // compressed prefix
    }

    [Fact]
    public void ToSentnode_ConvertsPrefix()
    {
        var wallet = SentinelWallet.Generate();
        var nodeAddr = wallet.ToSentnode();
        Assert.StartsWith("sentnode1", nodeAddr);
    }

    [Fact]
    public void ToSentprov_ConvertsPrefix()
    {
        var wallet = SentinelWallet.Generate();
        var provAddr = wallet.ToSentprov();
        Assert.StartsWith("sentprov1", provAddr);
    }

    [Fact]
    public void IsSameKey_MatchesCrossPrefixes()
    {
        var wallet = SentinelWallet.Generate();
        Assert.True(SentinelWallet.IsSameKey(wallet.Address, wallet.ToSentnode()));
        Assert.True(SentinelWallet.IsSameKey(wallet.Address, wallet.ToSentprov()));
    }

    [Fact]
    public void IsSameKey_RejectsDifferentKeys()
    {
        var wallet1 = SentinelWallet.Generate();
        var wallet2 = SentinelWallet.Generate();
        Assert.False(SentinelWallet.IsSameKey(wallet1.Address, wallet2.Address));
    }
}

public class ConstantsTests
{
    [Fact]
    public void ChainId_IsCorrect()
    {
        Assert.Equal("sentinelhub-2", Constants.ChainId);
    }

    [Fact]
    public void Denom_IsCorrect()
    {
        Assert.Equal("udvpn", Constants.Denom);
    }

    [Fact]
    public void DefaultLcdUrls_NotEmpty()
    {
        Assert.NotEmpty(Constants.DefaultLcdUrls);
        Assert.All(Constants.DefaultLcdUrls, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void DefaultRpcUrls_NotEmpty()
    {
        Assert.NotEmpty(Constants.DefaultRpcUrls);
        Assert.All(Constants.DefaultRpcUrls, url => Assert.StartsWith("https://", url));
    }

    // ─── DNS Presets ───

    [Fact]
    public void DnsPresets_HandshakeIsDefault()
    {
        Assert.Equal("handshake", Constants.DnsPresets.DefaultPreset);
    }

    [Fact]
    public void DnsPresets_AllThreePresetsExist()
    {
        Assert.True(Constants.DnsPresets.All.ContainsKey("handshake"));
        Assert.True(Constants.DnsPresets.All.ContainsKey("google"));
        Assert.True(Constants.DnsPresets.All.ContainsKey("cloudflare"));
        Assert.Equal(3, Constants.DnsPresets.All.Count);
    }

    [Fact]
    public void DnsPresets_HandshakeServers()
    {
        Assert.Equal(["103.196.38.38", "103.196.38.39"], Constants.DnsPresets.Handshake.Servers);
    }

    [Fact]
    public void DnsPresets_GoogleServers()
    {
        Assert.Equal(["8.8.8.8", "8.8.4.4"], Constants.DnsPresets.Google.Servers);
    }

    [Fact]
    public void DnsPresets_CloudflareServers()
    {
        Assert.Equal(["1.1.1.1", "1.0.0.1"], Constants.DnsPresets.Cloudflare.Servers);
    }

    [Fact]
    public void DnsResolve_NullReturnsHandshakeWithFallbacks()
    {
        var resolved = Constants.DnsPresets.Resolve((string?)null);
        Assert.StartsWith("103.196.38.38, 103.196.38.39", resolved);
        Assert.Contains("8.8.8.8", resolved);
        Assert.Contains("1.1.1.1", resolved);
    }

    [Fact]
    public void DnsResolve_HandshakePreset()
    {
        var resolved = Constants.DnsPresets.Resolve("handshake");
        Assert.StartsWith("103.196.38.38, 103.196.38.39", resolved);
        // Fallbacks appended
        Assert.Contains("8.8.8.8", resolved);
        Assert.Contains("1.1.1.1", resolved);
    }

    [Fact]
    public void DnsResolve_GooglePreset()
    {
        var resolved = Constants.DnsPresets.Resolve("google");
        Assert.StartsWith("8.8.8.8, 8.8.4.4", resolved);
        // Handshake fallback
        Assert.Contains("103.196.38.38", resolved);
        // Cloudflare fallback
        Assert.Contains("1.1.1.1", resolved);
    }

    [Fact]
    public void DnsResolve_CloudflarePreset()
    {
        var resolved = Constants.DnsPresets.Resolve("cloudflare");
        Assert.StartsWith("1.1.1.1, 1.0.0.1", resolved);
        Assert.Contains("103.196.38.38", resolved);
        Assert.Contains("8.8.8.8", resolved);
    }

    [Fact]
    public void DnsResolve_CaseInsensitive()
    {
        var upper = Constants.DnsPresets.Resolve("GOOGLE");
        var lower = Constants.DnsPresets.Resolve("google");
        Assert.Equal(upper, lower);
    }

    [Fact]
    public void DnsResolve_CustomStringPassedThrough()
    {
        var resolved = Constants.DnsPresets.Resolve("9.9.9.9");
        Assert.StartsWith("9.9.9.9", resolved);
        // Fallbacks still appended
        Assert.Contains("103.196.38.38", resolved);
    }

    [Fact]
    public void DnsResolve_CustomArrayWithFallbacks()
    {
        var resolved = Constants.DnsPresets.Resolve(new[] { "9.9.9.9", "149.112.112.112" });
        Assert.StartsWith("9.9.9.9, 149.112.112.112", resolved);
        Assert.Contains("103.196.38.38", resolved);
        Assert.Contains("8.8.8.8", resolved);
    }

    [Fact]
    public void DnsResolve_EmptyArrayReturnsHandshake()
    {
        var resolved = Constants.DnsPresets.Resolve(Array.Empty<string>());
        Assert.StartsWith("103.196.38.38", resolved);
    }

    [Fact]
    public void DnsResolve_NoDuplicatesInFallback()
    {
        // Google preset already has 8.8.8.8 — fallback should NOT add it again
        var resolved = Constants.DnsPresets.Resolve("google");
        var servers = resolved.Split(", ");
        Assert.Equal(servers.Length, servers.Distinct().Count());
    }

    [Fact]
    public void DnsFallbackOrder_HasAllPresets()
    {
        Assert.Equal(["handshake", "google", "cloudflare"], Constants.DnsPresets.FallbackOrder);
    }

    // ─── Country Utilities ───

    [Theory]
    [InlineData("United States", "US")]
    [InlineData("Germany", "DE")]
    [InlineData("The Netherlands", "NL")]
    [InlineData("Türkiye", "TR")]
    [InlineData("Czechia", "CZ")]
    [InlineData("Russian Federation", "RU")]
    [InlineData("Viet Nam", "VN")]
    [InlineData("DR Congo", "CD")]
    [InlineData("South Korea", "KR")]
    [InlineData("UAE", "AE")]
    [InlineData("UK", "GB")]
    [InlineData("us", "US")]
    public void CountryNameToCode_StandardAndVariants(string name, string expected)
    {
        Assert.Equal(expected, Constants.CountryNameToCode(name));
    }

    [Fact]
    public void CountryNameToCode_NullReturnsNull()
    {
        Assert.Null(Constants.CountryNameToCode(null));
        Assert.Null(Constants.CountryNameToCode(""));
    }

    [Fact]
    public void GetFlagUrl_ReturnsCorrectUrl()
    {
        Assert.Equal("https://flagcdn.com/w40/us.png", Constants.GetFlagUrl("US"));
        Assert.Equal("https://flagcdn.com/w80/de.png", Constants.GetFlagUrl("DE", 80));
        Assert.Equal("", Constants.GetFlagUrl(null));
    }

    [Fact]
    public void GbOptions_ContainsExpectedValues()
    {
        Assert.Contains(1, Constants.GbOptions);
        Assert.Contains(10, Constants.GbOptions);
    }

    [Fact]
    public void HourOptions_ContainsExpectedValues()
    {
        Assert.Contains(1, Constants.HourOptions);
        Assert.Contains(24, Constants.HourOptions);
    }
}
