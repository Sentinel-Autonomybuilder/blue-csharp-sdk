using System.Net.NetworkInformation;

namespace Sentinel.SDK.Core;

/// <summary>Event args for network state changes detected by <see cref="NetworkMonitor"/>.</summary>
public class NetworkChangedEventArgs : EventArgs
{
    /// <summary>Reason for the change: "available", "unavailable", or "address_changed".</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Monitors system network changes (availability and address) and raises
/// <see cref="NetworkChanged"/> so VPN clients can react (e.g. disconnect).
/// </summary>
public class NetworkMonitor : IDisposable
{
    /// <summary>Raised when the system network state changes.</summary>
    public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

    public NetworkMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        NetworkChanged?.Invoke(this, new NetworkChangedEventArgs
        {
            Reason = e.IsAvailable ? "available" : "unavailable",
        });
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        NetworkChanged?.Invoke(this, new NetworkChangedEventArgs
        {
            Reason = "address_changed",
        });
    }

    /// <summary>Unsubscribe from system network events.</summary>
    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }
}
