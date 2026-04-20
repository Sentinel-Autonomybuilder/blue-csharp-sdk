namespace Sentinel.SDK.Core;

// ─── App Type Validation Result ───

/// <summary>
/// Result of validating an app configuration against its type requirements.
/// Ported from js-sdk/app-types.js line 188-230.
/// </summary>
/// <param name="Valid">True when no errors were found.</param>
/// <param name="Errors">Hard validation failures (missing required config, unknown type).</param>
/// <param name="Warnings">Soft issues (misaligned options, missing optional feeGranter).</param>
/// <param name="TypeDescription">The matched app type description, or null when unknown.</param>
public record AppConfigValidation(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? TypeDescription);

// ─── App Configuration ───

/// <summary>
/// App-level configuration used by <see cref="AppTypeHelpers.ValidateAppConfig"/>
/// and <see cref="AppTypeHelpers.GetConnectDefaults"/>. Mirrors the JS config object.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Wallet mnemonic (required for all app types).</summary>
    public string? Mnemonic { get; init; }

    /// <summary>Plan ID (required for <see cref="Constants.AppTypes.WhiteLabel"/>).</summary>
    public long? PlanId { get; init; }

    /// <summary>Fee granter address (recommended for white-label so users don't pay gas).</summary>
    public string? FeeGranter { get; init; }

    /// <summary>DNS preset name or "handshake" (default).</summary>
    public string? Dns { get; init; }

    /// <summary>Default gigabytes for direct P2P sessions (default: 1).</summary>
    public int? DefaultGigabytes { get; init; }

    /// <summary>Prefer hourly pricing when both GB and hourly are available.</summary>
    public bool PreferHourly { get; init; }

    /// <summary>Route all traffic through VPN (default: true).</summary>
    public bool? FullTunnel { get; init; }

    /// <summary>Enable kill switch (default: false).</summary>
    public bool KillSwitch { get; init; }

    /// <summary>Optional country filter for auto-connect.</summary>
    public string[]? Countries { get; init; }
}

// ─── Connect Defaults ───

/// <summary>
/// Recommended connect options for an app type. Spread into your connect call.
/// Ported from js-sdk/app-types.js line 242-267.
/// </summary>
public sealed class ConnectDefaults
{
    /// <summary>DNS preset name.</summary>
    public string Dns { get; init; } = "handshake";

    /// <summary>Route all traffic through VPN.</summary>
    public bool FullTunnel { get; init; } = true;

    /// <summary>Kill switch enabled.</summary>
    public bool KillSwitch { get; init; }

    /// <summary>Plan ID (white-label only).</summary>
    public long? PlanId { get; init; }

    /// <summary>Fee granter address (white-label only).</summary>
    public string? FeeGranter { get; init; }

    /// <summary>Gigabytes to purchase (direct-p2p only).</summary>
    public int? Gigabytes { get; init; }

    /// <summary>Prefer hourly pricing (direct-p2p only).</summary>
    public bool PreferHourly { get; init; }
}

// ─── App Type Helpers ───

/// <summary>
/// Validation and defaults helpers for the three Sentinel app types.
/// Ported from js-sdk/app-types.js (validateAppConfig + getConnectDefaults, lines 181-267).
/// </summary>
public static class AppTypeHelpers
{
    /// <summary>
    /// Validate an app's configuration against its type requirements.
    /// Call at app startup to catch misconfigurations early.
    /// Ported from js-sdk/app-types.js line 195-230.
    /// </summary>
    public static AppConfigValidation ValidateAppConfig(string appType, AppConfig? config = null)
    {
        if (!Constants.AppTypes.All.Contains(appType))
        {
            return new AppConfigValidation(
                Valid: false,
                Errors: new[] { $"Unknown app type: \"{appType}\". Use: {string.Join(", ", Constants.AppTypes.All)}" },
                Warnings: Array.Empty<string>(),
                TypeDescription: null);
        }

        config ??= new AppConfig();
        var errors = new List<string>();
        var warnings = new List<string>();

        // All app types require a mnemonic.
        if (string.IsNullOrEmpty(config.Mnemonic))
            errors.Add($"Missing required config: \"mnemonic\" (required for {appType} apps)");

        if (appType == Constants.AppTypes.WhiteLabel)
        {
            if (!config.PlanId.HasValue || config.PlanId.Value <= 0)
                errors.Add("White-label apps MUST have a planId configured");
            if (string.IsNullOrEmpty(config.FeeGranter))
                warnings.Add("White-label apps should have a feeGranter so users don't pay gas. Without it, users need P2P tokens for gas fees.");
        }

        if (appType == Constants.AppTypes.DirectP2P && config.PlanId.HasValue && config.PlanId.Value > 0)
        {
            warnings.Add("planId is set but app type is direct_p2p — plan functions won't be used. Did you mean all_in_one?");
        }

        return new AppConfigValidation(
            Valid: errors.Count == 0,
            Errors: errors,
            Warnings: warnings,
            TypeDescription: DescribeType(appType));
    }

    /// <summary>
    /// Get the recommended connect options for an app type.
    /// Returns a base config that callers can customize per connection.
    /// Ported from js-sdk/app-types.js line 242-267.
    /// </summary>
    public static ConnectDefaults GetConnectDefaults(string appType, AppConfig? appConfig = null)
    {
        appConfig ??= new AppConfig();

        var dns = string.IsNullOrEmpty(appConfig.Dns) ? "handshake" : appConfig.Dns;
        var fullTunnel = appConfig.FullTunnel ?? true;

        if (appType == Constants.AppTypes.WhiteLabel)
        {
            return new ConnectDefaults
            {
                Dns = dns,
                FullTunnel = fullTunnel,
                KillSwitch = appConfig.KillSwitch,
                PlanId = appConfig.PlanId,
                FeeGranter = string.IsNullOrEmpty(appConfig.FeeGranter) ? null : appConfig.FeeGranter,
            };
        }

        if (appType == Constants.AppTypes.DirectP2P)
        {
            return new ConnectDefaults
            {
                Dns = dns,
                FullTunnel = fullTunnel,
                KillSwitch = appConfig.KillSwitch,
                Gigabytes = appConfig.DefaultGigabytes ?? 1,
                PreferHourly = appConfig.PreferHourly,
            };
        }

        // ALL_IN_ONE or unknown — caller decides per-connection.
        return new ConnectDefaults
        {
            Dns = dns,
            FullTunnel = fullTunnel,
            KillSwitch = appConfig.KillSwitch,
        };
    }

    private static string DescribeType(string appType) => appType switch
    {
        Constants.AppTypes.WhiteLabel => "White-label dVPN — branded app with pre-loaded plan + fee grant",
        Constants.AppTypes.DirectP2P => "Direct P2P — users pay nodes directly per-GB or per-hour",
        Constants.AppTypes.AllInOne => "All-in-one — plan subscriptions + direct P2P in one app",
        _ => appType,
    };
}
