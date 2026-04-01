namespace Sentinel.SDK.Core;

/// <summary>
/// Global constants for the Sentinel dVPN chain.
/// </summary>
public static class Constants
{
    /// <summary>Chain identifier for Sentinel mainnet.</summary>
    public const string ChainId = "sentinelhub-2";

    /// <summary>Micro-denomination used on-chain (1 P2P = 1,000,000 udvpn).</summary>
    public const string Denom = "udvpn";

    /// <summary>Gas price in udvpn per gas unit.</summary>
    public const string GasPrice = "0.2";

    /// <summary>Bech32 prefix for user accounts.</summary>
    public const string BechPrefix = "sent";

    /// <summary>Bech32 prefix for node operator accounts.</summary>
    public const string NodePrefix = "sentnode";

    /// <summary>Bech32 prefix for provider accounts.</summary>
    public const string ProviderPrefix = "sentprov";

    /// <summary>Default LCD (REST API) endpoints with fallback ordering.</summary>
    public static readonly string[] DefaultLcdUrls =
    {
        "https://lcd.sentinel.co",
        "https://api.sentinel.quokkastake.io",
        "https://sentinel-api.polkachu.com",
        "https://sentinel.api.trivium.network:1317",
    };

    /// <summary>Default RPC endpoints with fallback ordering.</summary>
    public static readonly string[] DefaultRpcUrls =
    {
        "https://rpc.sentinel.co:443",
        "https://sentinel-rpc.polkachu.com",
        "https://rpc.mathnodes.com",
    };

    // ─── Version Info ───

    /// <summary>SDK version string.</summary>
    public const string SdkVersion = "1.0.0";

    /// <summary>Compatible Sentinel chain version.</summary>
    public const string ChainVersion = "v12.0.0";

    /// <summary>Compatible Cosmos SDK version.</summary>
    public const string CosmosVersion = "0.47.17";

    /// <summary>Expected V2Ray binary version.</summary>
    public const string V2RayVersion = "5.2.1";

    // ─── Transport Success Rates ───

    /// <summary>
    /// Empirical success rates for V2Ray transport/security combinations.
    /// Based on 1000+ node audit. Key format: "transport/security".
    /// </summary>
    public static readonly Dictionary<string, double> TransportSuccessRates = new()
    {
        ["tcp/none"] = 1.0,
        ["websocket/none"] = 1.0,
        ["grpc/none"] = 0.87,
        ["grpc/tls"] = 0.0,
        ["gun/none"] = 0.85,
        ["http/none"] = 0.9,
        ["mkcp/none"] = 0.7,
        ["quic/none"] = 0.0,
        ["quic/tls"] = 0.0,
    };

    // ─── Default Timeouts (ms) ───

    /// <summary>
    /// Default timeout values in milliseconds for various operations.
    /// </summary>
    public static readonly Dictionary<string, int> DefaultTimeouts = new()
    {
        ["handshake"] = 90000,
        ["nodeStatus"] = 12000,
        ["lcdQuery"] = 15000,
        ["v2rayReady"] = 10000,
    };

    // ─── DNS Presets ───

    /// <summary>
    /// DNS server presets for WireGuard tunnel. Handshake is default — decentralized,
    /// censorship-resistant DNS that resolves both Handshake TLDs and ICANN domains.
    /// Matches Sentinel Shield mobile app behavior.
    /// </summary>
    public static class DnsPresets
    {
        /// <summary>Handshake decentralized DNS — censorship-resistant, resolves HNS + ICANN domains.</summary>
        public static readonly DnsPreset Handshake = new("Handshake",
            ["103.196.38.38", "103.196.38.39"],
            "Decentralized DNS — resolves Handshake + ICANN domains. Censorship-resistant.");

        /// <summary>Google Public DNS.</summary>
        public static readonly DnsPreset Google = new("Google",
            ["8.8.8.8", "8.8.4.4"],
            "Google Public DNS");

        /// <summary>Cloudflare Public DNS.</summary>
        public static readonly DnsPreset Cloudflare = new("Cloudflare",
            ["1.1.1.1", "1.0.0.1"],
            "Cloudflare Public DNS");

        /// <summary>Default preset name.</summary>
        public const string DefaultPreset = "handshake";

        /// <summary>All available presets keyed by lowercase name.</summary>
        public static readonly Dictionary<string, DnsPreset> All = new(StringComparer.OrdinalIgnoreCase)
        {
            ["handshake"] = Handshake,
            ["google"] = Google,
            ["cloudflare"] = Cloudflare,
        };

        /// <summary>Fallback order: handshake → google → cloudflare.</summary>
        public static readonly string[] FallbackOrder = ["handshake", "google", "cloudflare"];

        /// <summary>
        /// Resolve a DNS option into a comma-separated string for WireGuard config.
        /// Includes fallback DNS servers — if the primary DNS fails, the OS tries the next ones.
        /// </summary>
        /// <param name="dns">
        /// Preset name ("handshake", "google", "cloudflare"), custom IP string, or null for default (Handshake).
        /// </param>
        /// <returns>DNS string with fallbacks (e.g. "103.196.38.38, 103.196.38.39, 8.8.8.8, 1.1.1.1").</returns>
        public static string Resolve(string? dns = null)
        {
            var primary = ResolvePrimary(dns);
            return AppendFallbacks(primary);
        }

        /// <summary>
        /// Resolve a custom DNS server array into a comma-separated string with fallbacks.
        /// </summary>
        /// <param name="servers">Custom DNS server IPs. Null or empty returns Handshake default.</param>
        /// <returns>DNS string with fallbacks.</returns>
        public static string Resolve(string[]? servers)
        {
            var primary = (servers is null || servers.Length == 0)
                ? new List<string>(Handshake.Servers)
                : new List<string>(servers);
            return AppendFallbacks(primary);
        }

        /// <summary>Resolve just the primary servers (no fallbacks).</summary>
        private static List<string> ResolvePrimary(string? dns)
        {
            if (string.IsNullOrEmpty(dns))
                return new List<string>(Handshake.Servers);

            if (All.TryGetValue(dns, out var preset))
                return new List<string>(preset.Servers);

            // Treat as raw IP string
            return [dns];
        }

        /// <summary>Append one fallback server from each preset not already in the list.</summary>
        private static string AppendFallbacks(List<string> primary)
        {
            var seen = new HashSet<string>(primary);
            foreach (var name in FallbackOrder)
            {
                if (!All.TryGetValue(name, out var preset)) continue;
                foreach (var server in preset.Servers)
                {
                    if (seen.Add(server))
                    {
                        primary.Add(server);
                        break; // one per preset is enough for fallback
                    }
                }
            }
            return string.Join(", ", primary);
        }
    }

    /// <summary>A named DNS server preset.</summary>
    /// <param name="Name">Display name (e.g. "Handshake").</param>
    /// <param name="Servers">DNS server IP addresses.</param>
    /// <param name="Description">Human-readable description.</param>
    public record DnsPreset(string Name, string[] Servers, string Description);

    // ─── App Types ───

    /// <summary>
    /// Three types of dVPN applications that can be built on Sentinel.
    /// Each type has different SDK functions, UI requirements, and user flows.
    /// </summary>
    public static class AppTypes
    {
        /// <summary>
        /// White-label dVPN — branded app with pre-loaded plan + fee grant.
        /// Users click "Connect", done. Operator pays gas via fee grant.
        /// Users never see chain details, never pick nodes, never pay gas.
        /// SDK: <c>ConnectViaPlanAsync()</c>, <c>SubscribeToPlanAsync()</c>.
        /// </summary>
        public const string WhiteLabel = "white_label";

        /// <summary>
        /// Direct P2P — users pay nodes directly per-GB or per-hour.
        /// They browse nodes, pick pricing model, select duration, pay per session.
        /// SDK: <c>ConnectAsync()</c>, <c>GetActiveNodesAsync()</c>.
        /// </summary>
        public const string DirectP2P = "direct_p2p";

        /// <summary>
        /// All-in-one — plan subscriptions + direct P2P in one app.
        /// Users can browse plans, subscribe, OR connect directly to any node.
        /// SDK: All plan functions + all direct connect functions.
        /// </summary>
        public const string AllInOne = "all_in_one";

        /// <summary>All valid app type strings.</summary>
        public static readonly string[] All = [WhiteLabel, DirectP2P, AllInOne];
    }

    // ─── Country Utilities ───

    /// <summary>
    /// Comprehensive country name → ISO 3166-1 alpha-2 code mapping.
    /// Handles standard names, chain variants ("The Netherlands", "Türkiye", "DR Congo"),
    /// and short codes. 183 entries matching JS SDK exactly.
    /// </summary>
    public static readonly Dictionary<string, string> CountryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Standard names
        ["united states"] = "US", ["germany"] = "DE", ["france"] = "FR", ["united kingdom"] = "GB",
        ["netherlands"] = "NL", ["canada"] = "CA", ["japan"] = "JP", ["singapore"] = "SG",
        ["australia"] = "AU", ["brazil"] = "BR", ["india"] = "IN", ["south korea"] = "KR",
        ["turkey"] = "TR", ["romania"] = "RO", ["poland"] = "PL", ["spain"] = "ES",
        ["italy"] = "IT", ["sweden"] = "SE", ["norway"] = "NO", ["finland"] = "FI",
        ["switzerland"] = "CH", ["austria"] = "AT", ["ireland"] = "IE", ["portugal"] = "PT",
        ["czech republic"] = "CZ", ["hungary"] = "HU", ["bulgaria"] = "BG", ["greece"] = "GR",
        ["ukraine"] = "UA", ["russia"] = "RU", ["hong kong"] = "HK", ["taiwan"] = "TW",
        ["thailand"] = "TH", ["vietnam"] = "VN", ["indonesia"] = "ID", ["philippines"] = "PH",
        ["mexico"] = "MX", ["argentina"] = "AR", ["chile"] = "CL", ["colombia"] = "CO",
        ["south africa"] = "ZA", ["israel"] = "IL", ["united arab emirates"] = "AE",
        ["nigeria"] = "NG", ["latvia"] = "LV", ["lithuania"] = "LT", ["estonia"] = "EE",
        ["croatia"] = "HR", ["serbia"] = "RS", ["denmark"] = "DK", ["belgium"] = "BE",
        ["luxembourg"] = "LU", ["malta"] = "MT", ["cyprus"] = "CY", ["iceland"] = "IS",
        ["new zealand"] = "NZ", ["malaysia"] = "MY", ["bangladesh"] = "BD", ["pakistan"] = "PK",
        ["egypt"] = "EG", ["kenya"] = "KE", ["morocco"] = "MA", ["peru"] = "PE",
        ["venezuela"] = "VE", ["georgia"] = "GE", ["guatemala"] = "GT", ["puerto rico"] = "PR",
        ["china"] = "CN", ["saudi arabia"] = "SA", ["kazakhstan"] = "KZ", ["mongolia"] = "MN",
        ["slovakia"] = "SK", ["albania"] = "AL", ["moldova"] = "MD", ["jamaica"] = "JM",
        ["bolivia"] = "BO", ["ecuador"] = "EC", ["uruguay"] = "UY", ["bahrain"] = "BH",
        ["dr congo"] = "CD", ["costa rica"] = "CR", ["panama"] = "PA", ["paraguay"] = "PY",
        ["dominican republic"] = "DO", ["el salvador"] = "SV", ["honduras"] = "HN",
        ["nicaragua"] = "NI", ["cuba"] = "CU", ["haiti"] = "HT", ["trinidad and tobago"] = "TT",

        // Chain variant names
        ["the netherlands"] = "NL", ["türkiye"] = "TR", ["turkiye"] = "TR",
        ["czechia"] = "CZ", ["russian federation"] = "RU", ["viet nam"] = "VN",
        ["korea"] = "KR", ["republic of korea"] = "KR", ["uae"] = "AE", ["uk"] = "GB", ["usa"] = "US",
        ["democratic republic of the congo"] = "CD", ["congo"] = "CD",

        // Short codes (some nodes return these directly)
        ["us"] = "US", ["de"] = "DE", ["fr"] = "FR", ["gb"] = "GB", ["nl"] = "NL", ["ca"] = "CA",
        ["jp"] = "JP", ["sg"] = "SG", ["au"] = "AU", ["br"] = "BR", ["in"] = "IN", ["kr"] = "KR",
        ["tr"] = "TR", ["ro"] = "RO", ["pl"] = "PL", ["es"] = "ES", ["it"] = "IT", ["se"] = "SE",
        ["no"] = "NO", ["fi"] = "FI", ["ch"] = "CH", ["at"] = "AT", ["ie"] = "IE", ["pt"] = "PT",
        ["cz"] = "CZ", ["hu"] = "HU", ["bg"] = "BG", ["gr"] = "GR", ["ua"] = "UA", ["ru"] = "RU",
        ["hk"] = "HK", ["tw"] = "TW", ["th"] = "TH", ["vn"] = "VN", ["id"] = "ID", ["ph"] = "PH",
        ["mx"] = "MX", ["ar"] = "AR", ["cl"] = "CL", ["co"] = "CO", ["za"] = "ZA", ["il"] = "IL",
        ["ae"] = "AE", ["ng"] = "NG", ["lv"] = "LV", ["lt"] = "LT", ["ee"] = "EE", ["hr"] = "HR",
        ["rs"] = "RS", ["dk"] = "DK", ["be"] = "BE", ["lu"] = "LU", ["mt"] = "MT", ["cy"] = "CY",
        ["is"] = "IS", ["nz"] = "NZ", ["my"] = "MY", ["bd"] = "BD", ["pk"] = "PK", ["eg"] = "EG",
        ["ke"] = "KE", ["ma"] = "MA", ["pe"] = "PE", ["ve"] = "VE", ["ge"] = "GE", ["gt"] = "GT",
        ["pr"] = "PR", ["cn"] = "CN", ["sa"] = "SA", ["kz"] = "KZ", ["mn"] = "MN", ["sk"] = "SK",
        ["al"] = "AL", ["md"] = "MD", ["jm"] = "JM", ["bo"] = "BO", ["ec"] = "EC", ["uy"] = "UY",
        ["bh"] = "BH", ["cd"] = "CD",
    };

    /// <summary>
    /// Convert a country name to ISO 3166-1 alpha-2 code.
    /// Handles standard names, chain variants, short codes, and fuzzy matching.
    /// </summary>
    /// <param name="name">Country name from node status (e.g. "United States", "The Netherlands", "Türkiye").</param>
    /// <returns>ISO code (e.g. "US") or null if unknown.</returns>
    public static string? CountryNameToCode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lower = name.Trim().ToLowerInvariant();

        if (CountryMap.TryGetValue(lower, out var code)) return code;
        if (lower.Length == 2) return lower.ToUpperInvariant();

        // Fuzzy: find key containing or contained by input
        foreach (var (key, val) in CountryMap)
        {
            if (key.Length > 2 && (lower.Contains(key) || key.Contains(lower)))
                return val;
        }
        return null;
    }

    /// <summary>
    /// Get flag image URL from flagcdn.com (for WPF/native apps where emoji flags don't render).
    /// </summary>
    /// <param name="code">ISO 3166-1 alpha-2 code (e.g. "US").</param>
    /// <param name="width">Image width in pixels (default 40).</param>
    /// <returns>URL to PNG flag image.</returns>
    public static string GetFlagUrl(string? code, int width = 40)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2) return "";
        return $"https://flagcdn.com/w{width}/{code.ToLowerInvariant()}.png";
    }

    /// <summary>Common hour options for hourly session selection UI.</summary>
    public static readonly int[] HourOptions = [1, 2, 4, 8, 12, 24];

    /// <summary>Common GB options for per-GB session selection UI.</summary>
    public static readonly int[] GbOptions = [1, 2, 5, 10, 25, 50];

    // ─── Protobuf Message Types ───

    /// <summary>
    /// Cosmos/Sentinel protobuf message type URLs used in transaction construction.
    /// </summary>
    public static readonly Dictionary<string, string> MsgTypes = new()
    {
        ["START_SESSION"] = "/sentinel.node.v3.MsgStartSessionRequest",
        ["END_SESSION"] = "/sentinel.session.v3.MsgCancelSessionRequest",
        ["START_SUBSCRIPTION"] = "/sentinel.subscription.v3.MsgStartSubscriptionRequest",
        ["SUB_START_SESSION"] = "/sentinel.subscription.v3.MsgStartSessionRequest",
        ["PLAN_START_SESSION"] = "/sentinel.plan.v3.MsgStartSessionRequest",
        ["CREATE_PLAN"] = "/sentinel.plan.v3.MsgCreatePlanRequest",
        ["UPDATE_PLAN_STATUS"] = "/sentinel.plan.v3.MsgUpdatePlanStatusRequest",
        ["LINK_NODE"] = "/sentinel.plan.v3.MsgLinkNodeRequest",
        ["UNLINK_NODE"] = "/sentinel.plan.v3.MsgUnlinkNodeRequest",
        ["REGISTER_PROVIDER"] = "/sentinel.provider.v3.MsgRegisterProviderRequest",
        ["UPDATE_PROVIDER_DETAILS"] = "/sentinel.provider.v3.MsgUpdateProviderDetailsRequest",
        ["UPDATE_PROVIDER_STATUS"] = "/sentinel.provider.v3.MsgUpdateProviderStatusRequest",
        ["UPDATE_PLAN_DETAILS"] = "/sentinel.plan.v3.MsgUpdatePlanDetailsRequest",
        ["START_LEASE"] = "/sentinel.lease.v1.MsgStartLeaseRequest",
        ["END_LEASE"] = "/sentinel.lease.v1.MsgEndLeaseRequest",
        // Subscription management (v3)
        ["CANCEL_SUBSCRIPTION"] = "/sentinel.subscription.v3.MsgCancelSubscriptionRequest",
        ["RENEW_SUBSCRIPTION"] = "/sentinel.subscription.v3.MsgRenewSubscriptionRequest",
        ["SHARE_SUBSCRIPTION"] = "/sentinel.subscription.v3.MsgShareSubscriptionRequest",
        ["UPDATE_SUBSCRIPTION"] = "/sentinel.subscription.v3.MsgUpdateSubscriptionRequest",
        // Session management (v3)
        ["UPDATE_SESSION"] = "/sentinel.session.v3.MsgUpdateSessionRequest",
        // Node operator (v3)
        ["REGISTER_NODE"] = "/sentinel.node.v3.MsgRegisterNodeRequest",
        ["UPDATE_NODE_DETAILS"] = "/sentinel.node.v3.MsgUpdateNodeDetailsRequest",
        ["UPDATE_NODE_STATUS"] = "/sentinel.node.v3.MsgUpdateNodeStatusRequest",
        ["SEND"] = "/cosmos.bank.v1beta1.MsgSend",
        ["GRANT_FEE"] = "/cosmos.feegrant.v1beta1.MsgGrantAllowance",
        ["REVOKE_FEE"] = "/cosmos.feegrant.v1beta1.MsgRevokeAllowance",
        ["AUTHZ_GRANT"] = "/cosmos.authz.v1beta1.MsgGrant",
        ["AUTHZ_REVOKE"] = "/cosmos.authz.v1beta1.MsgRevoke",
        ["AUTHZ_EXEC"] = "/cosmos.authz.v1beta1.MsgExec",
    };
}
