using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for <see cref="DynamicTransportRates"/> — runtime transport success/failure tracking.
/// </summary>
public class DynamicTransportRatesTests : IDisposable
{
    public DynamicTransportRatesTests()
    {
        // Start each test with a clean slate (memory only, don't touch disk)
        DynamicTransportRates.Reset(persist: false);
    }

    public void Dispose()
    {
        // Clean up after each test
        DynamicTransportRates.Reset(persist: false);
    }

    [Fact]
    public void RecordResult_IncrementsCounts()
    {
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", false);

        var all = DynamicTransportRates.GetAll();
        Assert.True(all.ContainsKey("tcp"));
        Assert.Equal(3, all["tcp"].Samples);
    }

    [Fact]
    public void GetRate_ReturnsNull_ForLessThan2Samples()
    {
        // No samples at all
        Assert.Null(DynamicTransportRates.GetRate("grpc"));

        // Only 1 sample
        DynamicTransportRates.RecordResult("grpc", true);
        Assert.Null(DynamicTransportRates.GetRate("grpc"));
    }

    [Fact]
    public void GetRate_ReturnsCorrectRate_After2PlusSamples()
    {
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", false);

        var rate = DynamicTransportRates.GetRate("tcp");
        Assert.NotNull(rate);
        Assert.Equal(2.0 / 3.0, rate!.Value, precision: 4);
    }

    [Fact]
    public void GetRate_Returns100Percent_AllSuccesses()
    {
        DynamicTransportRates.RecordResult("websocket", true);
        DynamicTransportRates.RecordResult("websocket", true);

        var rate = DynamicTransportRates.GetRate("websocket");
        Assert.NotNull(rate);
        Assert.Equal(1.0, rate!.Value);
    }

    [Fact]
    public void GetRate_Returns0Percent_AllFailures()
    {
        DynamicTransportRates.RecordResult("quic", false);
        DynamicTransportRates.RecordResult("quic", false);

        var rate = DynamicTransportRates.GetRate("quic");
        Assert.NotNull(rate);
        Assert.Equal(0.0, rate!.Value);
    }

    [Fact]
    public void GetAll_ReturnsAllRecordedRates()
    {
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", false);
        DynamicTransportRates.RecordResult("grpc", true);
        DynamicTransportRates.RecordResult("grpc", true);
        DynamicTransportRates.RecordResult("grpc", true);

        var all = DynamicTransportRates.GetAll();
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("tcp"));
        Assert.True(all.ContainsKey("grpc"));
        Assert.Equal(2, all["tcp"].Samples);
        Assert.Equal(3, all["grpc"].Samples);
        Assert.Equal(0.5, all["tcp"].Rate);
        Assert.Equal(1.0, all["grpc"].Rate);
    }

    [Fact]
    public void GetAll_ReturnsEmpty_WhenNoData()
    {
        var all = DynamicTransportRates.GetAll();
        Assert.Empty(all);
    }

    [Fact]
    public void Reset_ClearsAllData()
    {
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("grpc", false);

        DynamicTransportRates.Reset();

        Assert.Null(DynamicTransportRates.GetRate("tcp"));
        Assert.Null(DynamicTransportRates.GetRate("grpc"));
        Assert.Empty(DynamicTransportRates.GetAll());
    }

    [Fact]
    public void Reset_WithPersist_ClearsDiskFile()
    {
        // Record something to create the file
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", true);

        // Reset with persist — should clear the disk file
        DynamicTransportRates.Reset(persist: true);

        Assert.Null(DynamicTransportRates.GetRate("tcp"));
        Assert.Empty(DynamicTransportRates.GetAll());
    }

    [Fact]
    public void RecordResult_ThrowsOnNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => DynamicTransportRates.RecordResult("", true));
        Assert.Throws<ArgumentNullException>(() => DynamicTransportRates.RecordResult(null!, true));
    }

    [Fact]
    public void GetRate_ThrowsOnNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => DynamicTransportRates.GetRate(""));
        Assert.Throws<ArgumentNullException>(() => DynamicTransportRates.GetRate(null!));
    }

    [Fact]
    public void MultipleTransports_IndependentTracking()
    {
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("tcp", true);
        DynamicTransportRates.RecordResult("grpc/none", false);
        DynamicTransportRates.RecordResult("grpc/none", false);

        Assert.Equal(1.0, DynamicTransportRates.GetRate("tcp"));
        Assert.Equal(0.0, DynamicTransportRates.GetRate("grpc/none"));
    }
}
