using Sentinel.SDK.Core;
using Sentinel.SDK.Tunnel.V2Ray;
using Xunit;

namespace Sentinel.SDK.Tests;

public class V2RayProcessTests
{
    // ─── Constructor ───

    [Fact]
    public void Constructor_InvalidPath_ThrowsSentinelException()
    {
        var ex = Assert.Throws<SentinelException>(
            () => new V2RayProcess(@"C:\nonexistent\v2ray.exe")
        );
        Assert.Equal("V2RAY_NOT_FOUND", ex.Code);
        Assert.Contains("v2ray.exe not found", ex.Message);
    }

    [Fact]
    public void Constructor_ValidPath_DoesNotThrow()
    {
        // Use a temp file as a stand-in for v2ray.exe
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.NotNull(process);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── IsRunning ───

    [Fact]
    public void IsRunning_ReturnsFalse_Initially()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.False(process.IsRunning);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── SocksPort ───

    [Fact]
    public void SocksPort_ReturnsZero_BeforeStart()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.Equal(0, process.SocksPort);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── SocksUser / SocksPass ───

    [Fact]
    public void SocksUser_IsNull_BeforeStart()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.Null(process.SocksUser);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SocksPass_IsNull_BeforeStart()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.Null(process.SocksPass);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── GetStderr ───

    [Fact]
    public void GetStderr_ReturnsEmpty_Initially()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.Equal("", process.GetStderr());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── Dispose ───

    [Fact]
    public void Dispose_DoesNotThrow_WhenNotRunning()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var process = new V2RayProcess(tempFile);
            process.Dispose();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var process = new V2RayProcess(tempFile);
            process.Dispose();
            process.Dispose(); // Should not throw
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Implements_IDisposable()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using var process = new V2RayProcess(tempFile);
            Assert.IsAssignableFrom<IDisposable>(process);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
