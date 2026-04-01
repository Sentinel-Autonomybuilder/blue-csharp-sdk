using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for VpnSettings — persistent VPN user settings with
/// JSON serialization, atomic writes, and corrupt-file resilience.
/// </summary>
public class VpnSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public VpnSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sentinel-sdk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private string TempFile(string name = "settings.json")
        => Path.Combine(_tempDir, name);

    // ─── Load Returns Defaults When File Doesn't Exist ───

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var path = TempFile("nonexistent.json");
        Assert.False(File.Exists(path));

        var settings = VpnSettings.Load(path);

        Assert.NotNull(settings);
        Assert.True(settings.FullTunnel);
        Assert.True(settings.SystemProxy);
        Assert.False(settings.AutoConnect);
        Assert.Null(settings.PreferredCountry);
        Assert.Null(settings.LastNodeAddress);
    }

    // ─── Save Then Load Round-Trips All Properties ───

    [Fact]
    public void Save_ThenLoad_RoundTripsAllProperties()
    {
        var path = TempFile();

        var original = new VpnSettings
        {
            PreferredCountry = "DE",
            AutoConnect = true,
            KillSwitch = true,
            StartWithWindows = true,
            LastNodeAddress = "sentnode1abc123",
            LastServiceType = "wireguard",
            FullTunnel = false,
            SystemProxy = false,
        };

        original.Save(path);
        var loaded = VpnSettings.Load(path);

        Assert.Equal("DE", loaded.PreferredCountry);
        Assert.True(loaded.AutoConnect);
        Assert.True(loaded.KillSwitch);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("sentnode1abc123", loaded.LastNodeAddress);
        Assert.Equal("wireguard", loaded.LastServiceType);
        Assert.False(loaded.FullTunnel);
        Assert.False(loaded.SystemProxy);
    }

    // ─── Default FullTunnel Is True ───

    [Fact]
    public void Default_FullTunnel_IsTrue()
    {
        var settings = new VpnSettings();
        Assert.True(settings.FullTunnel);
    }

    // ─── Default SystemProxy Is True ───

    [Fact]
    public void Default_SystemProxy_IsTrue()
    {
        var settings = new VpnSettings();
        Assert.True(settings.SystemProxy);
    }

    // ─── Default AutoConnect Is False ───

    [Fact]
    public void Default_AutoConnect_IsFalse()
    {
        var settings = new VpnSettings();
        Assert.False(settings.AutoConnect);
    }

    // ─── Load Handles Corrupt JSON Gracefully ───

    [Fact]
    public void Load_HandlesCorruptJson_ReturnsDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not valid json !@#$% }");

        var settings = VpnSettings.Load(path);

        Assert.NotNull(settings);
        Assert.True(settings.FullTunnel); // defaults
        Assert.True(settings.SystemProxy);
        Assert.False(settings.AutoConnect);
    }

    [Fact]
    public void Load_HandlesEmptyFile_ReturnsDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "");

        var settings = VpnSettings.Load(path);

        Assert.NotNull(settings);
        Assert.True(settings.FullTunnel);
    }

    [Fact]
    public void Load_HandlesTruncatedJson_ReturnsDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "{\"preferredCountry\": \"US\"");

        var settings = VpnSettings.Load(path);

        Assert.NotNull(settings);
        // Should return defaults since JSON is truncated/invalid
        Assert.True(settings.FullTunnel);
    }

    // ─── Save Creates Directory If Missing ───

    [Fact]
    public void Save_CreatesDirectory_IfMissing()
    {
        var nestedDir = Path.Combine(_tempDir, "deep", "nested", "dir");
        var path = Path.Combine(nestedDir, "settings.json");

        Assert.False(Directory.Exists(nestedDir));

        var settings = new VpnSettings { PreferredCountry = "FR" };
        settings.Save(path);

        Assert.True(File.Exists(path));

        var loaded = VpnSettings.Load(path);
        Assert.Equal("FR", loaded.PreferredCountry);
    }

    // ─── DefaultPath Is Not Empty ───

    [Fact]
    public void DefaultPath_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(VpnSettings.DefaultPath));
    }

    [Fact]
    public void DefaultPath_EndsWithSettingsJson()
    {
        Assert.EndsWith("settings.json", VpnSettings.DefaultPath);
    }

    // ─── Save Overwrites Existing File ───

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = TempFile();

        var first = new VpnSettings { PreferredCountry = "US" };
        first.Save(path);

        var second = new VpnSettings { PreferredCountry = "JP" };
        second.Save(path);

        var loaded = VpnSettings.Load(path);
        Assert.Equal("JP", loaded.PreferredCountry);
    }

    // ─── Default KillSwitch Is False ───

    [Fact]
    public void Default_KillSwitch_IsFalse()
    {
        var settings = new VpnSettings();
        Assert.False(settings.KillSwitch);
    }

    // ─── Default StartWithWindows Is False ───

    [Fact]
    public void Default_StartWithWindows_IsFalse()
    {
        var settings = new VpnSettings();
        Assert.False(settings.StartWithWindows);
    }

    // ─── Null Properties Omitted in JSON ───

    [Fact]
    public void Save_OmitsNullProperties()
    {
        var path = TempFile();

        var settings = new VpnSettings(); // All nullable properties are null
        settings.Save(path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("preferredCountry", json);
        Assert.DoesNotContain("lastNodeAddress", json);
        Assert.DoesNotContain("lastServiceType", json);
    }

    // ─── JSON Is Indented (human-readable) ───

    [Fact]
    public void Save_ProducesIndentedJson()
    {
        var path = TempFile();

        var settings = new VpnSettings { AutoConnect = true };
        settings.Save(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\n", json); // Indented JSON has newlines
    }

    // ─── Load With Partial JSON Returns Partial Defaults ───

    [Fact]
    public void Load_WithPartialValidJson_FillsDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "{\"autoConnect\": true}");

        var settings = VpnSettings.Load(path);

        Assert.True(settings.AutoConnect);
        Assert.True(settings.FullTunnel); // default
        Assert.True(settings.SystemProxy); // default
        Assert.Null(settings.PreferredCountry); // default
    }
}
