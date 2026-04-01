using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for CredentialStore — credential persistence, round-trip serialization,
/// clear operations, and max-entry pruning.
/// Uses real file I/O against the platform state directory.
/// </summary>
public class CredentialStoreTests : IDisposable
{
    /// <summary>
    /// Clean up all saved credentials after each test.
    /// </summary>
    public void Dispose()
    {
        CredentialStore.ClearAll();
        GC.SuppressFinalize(this);
    }

    // ─── SavedCredentials Record: WireGuard ───

    [Fact]
    public void SavedCredentials_WireGuard_AllFields()
    {
        var creds = new SavedCredentials
        {
            SessionId = "12345",
            ServiceType = "wireguard",
            NodeAddress = "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            WgPrivateKey = "dGVzdFByaXZhdGVLZXk=",
            WgServerPubKey = "dGVzdFNlcnZlclB1YktleQ==",
            WgAssignedAddrs = ["10.8.0.2/24", "fd1d::2/128"],
            WgServerEndpoint = "1.2.3.4:51820",
            SavedAt = "2026-03-18T12:00:00Z",
        };

        Assert.Equal("12345", creds.SessionId);
        Assert.Equal("wireguard", creds.ServiceType);
        Assert.Equal("sentnode1abcdefghijklmnopqrstuvwxyz01234abc", creds.NodeAddress);
        Assert.Equal("dGVzdFByaXZhdGVLZXk=", creds.WgPrivateKey);
        Assert.Equal("dGVzdFNlcnZlclB1YktleQ==", creds.WgServerPubKey);
        Assert.Equal(2, creds.WgAssignedAddrs!.Length);
        Assert.Equal("10.8.0.2/24", creds.WgAssignedAddrs[0]);
        Assert.Equal("fd1d::2/128", creds.WgAssignedAddrs[1]);
        Assert.Equal("1.2.3.4:51820", creds.WgServerEndpoint);
        Assert.Null(creds.V2RayUuid);
        Assert.Null(creds.V2RayTransport);
        Assert.Null(creds.V2RayProtocol);
        Assert.Null(creds.V2RayTls);
        Assert.Null(creds.V2RayPort);
    }

    // ─── SavedCredentials Record: V2Ray ───

    [Fact]
    public void SavedCredentials_V2Ray_AllFields()
    {
        var creds = new SavedCredentials
        {
            SessionId = "67890",
            ServiceType = "v2ray",
            NodeAddress = "sentnode1abcdefghijklmnopqrstuvwxyz01234abc",
            V2RayUuid = "550e8400-e29b-41d4-a716-446655440000",
            V2RayTransport = 3,
            V2RayProtocol = 1,
            V2RayTls = 0,
            V2RayPort = 443,
            SavedAt = "2026-03-18T13:00:00Z",
        };

        Assert.Equal("67890", creds.SessionId);
        Assert.Equal("v2ray", creds.ServiceType);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", creds.V2RayUuid);
        Assert.Equal(3, creds.V2RayTransport);
        Assert.Equal(1, creds.V2RayProtocol);
        Assert.Equal(0, creds.V2RayTls);
        Assert.Equal(443, creds.V2RayPort);
        Assert.Null(creds.WgPrivateKey);
        Assert.Null(creds.WgServerPubKey);
        Assert.Null(creds.WgAssignedAddrs);
        Assert.Null(creds.WgServerEndpoint);
    }

    // ─── Save Then Load Round-Trip ───

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var nodeAddress = "sentnode1abcdefghijklmnopqrstuvwxyz01234abc";
        var original = new SavedCredentials
        {
            SessionId = "42",
            ServiceType = "wireguard",
            NodeAddress = nodeAddress,
            WgPrivateKey = "cHJpdmF0ZUtleUJ5dGVz",
            WgServerPubKey = "c2VydmVyUHViS2V5",
            WgAssignedAddrs = ["10.8.0.5/24"],
            WgServerEndpoint = "5.6.7.8:51820",
            SavedAt = "2026-03-18T10:00:00Z",
        };

        CredentialStore.Save(nodeAddress, original);
        var loaded = CredentialStore.Load(nodeAddress);

        Assert.NotNull(loaded);
        Assert.Equal(original.SessionId, loaded!.SessionId);
        Assert.Equal(original.ServiceType, loaded.ServiceType);
        Assert.Equal(original.NodeAddress, loaded.NodeAddress);
        Assert.Equal(original.WgPrivateKey, loaded.WgPrivateKey);
        Assert.Equal(original.WgServerPubKey, loaded.WgServerPubKey);
        Assert.Equal(original.WgAssignedAddrs, loaded.WgAssignedAddrs);
        Assert.Equal(original.WgServerEndpoint, loaded.WgServerEndpoint);
        Assert.Equal(original.SavedAt, loaded.SavedAt);
    }

    [Fact]
    public void Save_ThenLoad_V2Ray_RoundTrips()
    {
        var nodeAddress = "sentnode1zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        var original = new SavedCredentials
        {
            SessionId = "99",
            ServiceType = "v2ray",
            NodeAddress = nodeAddress,
            V2RayUuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            V2RayTransport = 7,
            V2RayProtocol = 2,
            V2RayTls = 1,
            V2RayPort = 8443,
            SavedAt = "2026-03-18T14:00:00Z",
        };

        CredentialStore.Save(nodeAddress, original);
        var loaded = CredentialStore.Load(nodeAddress);

        Assert.NotNull(loaded);
        Assert.Equal(original.SessionId, loaded!.SessionId);
        Assert.Equal(original.ServiceType, loaded.ServiceType);
        Assert.Equal(original.V2RayUuid, loaded.V2RayUuid);
        Assert.Equal(original.V2RayTransport, loaded.V2RayTransport);
        Assert.Equal(original.V2RayProtocol, loaded.V2RayProtocol);
        Assert.Equal(original.V2RayTls, loaded.V2RayTls);
        Assert.Equal(original.V2RayPort, loaded.V2RayPort);
    }

    // ─── Load Returns Null When Nothing Saved ───

    [Fact]
    public void Load_ReturnsNull_WhenNothingSaved()
    {
        CredentialStore.ClearAll();

        var loaded = CredentialStore.Load("sentnode1doesnotexistxxxxxxxxxxxxxxxxx00");

        Assert.Null(loaded);
    }

    // ─── Clear Removes Specific Node ───

    [Fact]
    public void Clear_RemovesSpecificNode()
    {
        var node1 = "sentnode1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var node2 = "sentnode1bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        CredentialStore.Save(node1, new SavedCredentials
        {
            SessionId = "1",
            ServiceType = "wireguard",
            NodeAddress = node1,
            SavedAt = DateTime.UtcNow.ToString("o"),
        });
        CredentialStore.Save(node2, new SavedCredentials
        {
            SessionId = "2",
            ServiceType = "wireguard",
            NodeAddress = node2,
            SavedAt = DateTime.UtcNow.ToString("o"),
        });

        // Verify both exist
        Assert.NotNull(CredentialStore.Load(node1));
        Assert.NotNull(CredentialStore.Load(node2));

        // Clear only node1
        CredentialStore.Clear(node1);

        Assert.Null(CredentialStore.Load(node1));
        Assert.NotNull(CredentialStore.Load(node2));
    }

    // ─── ClearAll Removes Everything ───

    [Fact]
    public void ClearAll_RemovesEverything()
    {
        var node1 = "sentnode1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var node2 = "sentnode1bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        CredentialStore.Save(node1, new SavedCredentials
        {
            SessionId = "10",
            ServiceType = "wireguard",
            NodeAddress = node1,
            SavedAt = DateTime.UtcNow.ToString("o"),
        });
        CredentialStore.Save(node2, new SavedCredentials
        {
            SessionId = "20",
            ServiceType = "v2ray",
            NodeAddress = node2,
            SavedAt = DateTime.UtcNow.ToString("o"),
        });

        Assert.NotNull(CredentialStore.Load(node1));
        Assert.NotNull(CredentialStore.Load(node2));

        CredentialStore.ClearAll();

        Assert.Null(CredentialStore.Load(node1));
        Assert.Null(CredentialStore.Load(node2));
    }

    // ─── Max 100 Entries (Prune Oldest) ───

    [Fact]
    public void Save_PrunesOldestWhenOver100Entries()
    {
        // Save 101 entries — the oldest should be pruned
        for (var i = 0; i < 101; i++)
        {
            var nodeAddr = $"sentnode1{i:D38}";
            CredentialStore.Save(nodeAddr, new SavedCredentials
            {
                SessionId = i.ToString(),
                ServiceType = "wireguard",
                NodeAddress = nodeAddr,
                // Use ascending timestamps so entry 0 is the oldest
                SavedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i).ToString("o"),
            });
        }

        // Count should be capped at 100
        Assert.Equal(100, CredentialStore.Count());

        // The oldest entry (i=0) should have been pruned
        var oldest = CredentialStore.Load("sentnode100000000000000000000000000000000000000");
        Assert.Null(oldest);

        // The newest entry (i=100) should still exist
        var newest = CredentialStore.Load($"sentnode1{100:D38}");
        Assert.NotNull(newest);
    }

    // ─── Save Overwrites Existing Entry ───

    [Fact]
    public void Save_OverwritesExistingEntry()
    {
        var nodeAddress = "sentnode1abcdefghijklmnopqrstuvwxyz01234abc";

        CredentialStore.Save(nodeAddress, new SavedCredentials
        {
            SessionId = "100",
            ServiceType = "wireguard",
            NodeAddress = nodeAddress,
            WgPrivateKey = "b2xk",
            SavedAt = "2026-03-18T10:00:00Z",
        });

        CredentialStore.Save(nodeAddress, new SavedCredentials
        {
            SessionId = "200",
            ServiceType = "wireguard",
            NodeAddress = nodeAddress,
            WgPrivateKey = "bmV3",
            SavedAt = "2026-03-18T11:00:00Z",
        });

        var loaded = CredentialStore.Load(nodeAddress);
        Assert.NotNull(loaded);
        Assert.Equal("200", loaded!.SessionId);
        Assert.Equal("bmV3", loaded.WgPrivateKey);
    }

    // ─── Count Returns Correct Value ───

    [Fact]
    public void Count_ReturnsCorrectValue()
    {
        CredentialStore.ClearAll();

        Assert.Equal(0, CredentialStore.Count());

        CredentialStore.Save("sentnode1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new SavedCredentials
        {
            SessionId = "1",
            ServiceType = "wireguard",
            NodeAddress = "sentnode1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SavedAt = DateTime.UtcNow.ToString("o"),
        });

        Assert.Equal(1, CredentialStore.Count());

        CredentialStore.Save("sentnode1bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new SavedCredentials
        {
            SessionId = "2",
            ServiceType = "v2ray",
            NodeAddress = "sentnode1bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            SavedAt = DateTime.UtcNow.ToString("o"),
        });

        Assert.Equal(2, CredentialStore.Count());
    }
}
