namespace Sentinel.SDK.Core;

/// <summary>
/// Cosmos protobuf type URL constants for all Sentinel message types.
/// These are the values used in the typeUrl field of Any-wrapped messages.
/// Matches the JS SDK's TYPE_URLS export exactly.
/// </summary>
public static class TypeUrls
{
    // ─── Node Sessions ─────────────────────────────────────────────
    public const string StartSession = "/sentinel.node.v3.MsgStartSessionRequest";
    public const string CancelSession = "/sentinel.session.v3.MsgCancelSessionRequest";
    public const string UpdateSession = "/sentinel.session.v3.MsgUpdateSessionRequest";

    // ─── Subscriptions ─────────────────────────────────────────────
    public const string StartSubscription = "/sentinel.subscription.v3.MsgStartSubscriptionRequest";
    public const string CancelSubscription = "/sentinel.subscription.v3.MsgCancelSubscriptionRequest";
    public const string RenewSubscription = "/sentinel.subscription.v3.MsgRenewSubscriptionRequest";
    public const string ShareSubscription = "/sentinel.subscription.v3.MsgShareSubscriptionRequest";
    public const string UpdateSubscription = "/sentinel.subscription.v3.MsgUpdateSubscriptionRequest";
    public const string SubStartSession = "/sentinel.subscription.v3.MsgStartSessionRequest";

    // ─── Plans ─────────────────────────────────────────────────────
    public const string PlanStartSession = "/sentinel.plan.v3.MsgStartSessionRequest";
    public const string CreatePlan = "/sentinel.plan.v3.MsgCreatePlanRequest";
    public const string UpdatePlanDetails = "/sentinel.plan.v3.MsgUpdatePlanDetailsRequest";
    public const string UpdatePlanStatus = "/sentinel.plan.v3.MsgUpdatePlanStatusRequest";
    public const string LinkNode = "/sentinel.plan.v3.MsgLinkNodeRequest";
    public const string UnlinkNode = "/sentinel.plan.v3.MsgUnlinkNodeRequest";

    // ─── Provider ──────────────────────────────────────────────────
    public const string RegisterProvider = "/sentinel.provider.v3.MsgRegisterProviderRequest";
    public const string UpdateProvider = "/sentinel.provider.v3.MsgUpdateProviderDetailsRequest";
    public const string UpdateProviderStatus = "/sentinel.provider.v3.MsgUpdateProviderStatusRequest";

    // ─── Lease ─────────────────────────────────────────────────────
    public const string StartLease = "/sentinel.lease.v1.MsgStartLeaseRequest";
    public const string EndLease = "/sentinel.lease.v1.MsgEndLeaseRequest";

    // ─── Node Operator ─────────────────────────────────────────────
    public const string RegisterNode = "/sentinel.node.v3.MsgRegisterNodeRequest";
    public const string UpdateNodeDetails = "/sentinel.node.v3.MsgUpdateNodeDetailsRequest";
    public const string UpdateNodeStatus = "/sentinel.node.v3.MsgUpdateNodeStatusRequest";

    // ─── Cosmos Standard ───────────────────────────────────────────
    public const string Send = "/cosmos.bank.v1beta1.MsgSend";
    public const string GrantFeeAllowance = "/cosmos.feegrant.v1beta1.MsgGrantAllowance";
    public const string RevokeFeeAllowance = "/cosmos.feegrant.v1beta1.MsgRevokeAllowance";
    public const string AuthzGrant = "/cosmos.authz.v1beta1.MsgGrant";
    public const string AuthzRevoke = "/cosmos.authz.v1beta1.MsgRevoke";
    public const string AuthzExec = "/cosmos.authz.v1beta1.MsgExec";
}
