namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC9/AC10) — DCB event-type constants for the audit hash-chain.
/// Plane-routed like <c>AlertEventEmitter</c>: tenant-scope → the tenant's
/// <c>domain_events</c>; platform-scope → <c>platform_events</c>.
/// </summary>
public static class AuditChainEventTypes
{
    /// <summary>A clean verification of a chain range.</summary>
    public const string Verified = "AUDIT.CHAIN.VERIFIED";

    /// <summary>A tamper (broken link) was detected — raises a critical alert.</summary>
    public const string TamperDetected = "AUDIT.CHAIN.TAMPER_DETECTED";

    /// <summary>A signed checkpoint was written for a scope.</summary>
    public const string Checkpointed = "AUDIT.CHAIN.CHECKPOINTED";

    /// <summary>Built-in alert rule key seeded for the tamper event.</summary>
    public const string TamperAlertRuleKey = "audit-chain-tamper";
}
