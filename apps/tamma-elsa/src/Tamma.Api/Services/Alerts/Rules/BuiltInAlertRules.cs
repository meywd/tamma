namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — canonical list of built-in alert rule
/// specs. Seeded into <c>alert_rules</c> by
/// <see cref="BuiltInAlertRuleSeeder"/> on app startup with idempotent
/// upserts keyed by <see cref="BuiltInKey"/>.
///
/// <para>Built-in rules ship with an empty <c>ChannelIds</c> array —
/// an admin links them to a specific channel via the Wave C.3 UI
/// (delivery doesn't happen until then). This avoids auto-spamming a
/// default channel the operator never configured.</para>
/// </summary>
public sealed record BuiltInAlertRuleSpec(
    string BuiltInKey,
    string Name,
    string Description,
    string Severity,
    string EventType,
    string Predicate,
    int ThrottleSeconds);

public static class BuiltInAlertRules
{
    /// <summary>
    /// The five Wave C.2 built-ins per the brief. Ordering here is
    /// the insertion order the seeder will use — not significant at
    /// runtime (lookup goes via <c>built_in_key</c>).
    /// </summary>
    public static readonly IReadOnlyList<BuiltInAlertRuleSpec> All = new[]
    {
        new BuiltInAlertRuleSpec(
            BuiltInKey: "budget-exhausted",
            Name: "budget-exhausted",
            Description:
                "A tenant has hit its usage cap. Workflows for tenant " +
                "{tenantId} will be rejected until the budget is " +
                "extended or the period rolls over.",
            Severity: AlertSeverity.Warning,
            EventType: "BUDGET.EXHAUSTED",
            Predicate: """{"op":"always"}""",
            ThrottleSeconds: 60),

        new BuiltInAlertRuleSpec(
            BuiltInKey: "agent-dispatch-failed-3x-5min",
            Name: "agent-dispatch-failed-3x-5min",
            Description:
                "3 or more AGENT.DISPATCH.FAILED events observed for " +
                "the same tenant within a 5-minute window. Check " +
                "provider health / budget / credentials for tenant " +
                "{tenantId}.",
            Severity: AlertSeverity.Warning,
            EventType: "AGENT.DISPATCH.FAILED",
            Predicate:
                """{"op":"count_gte","window_seconds":300,"threshold":3}""",
            ThrottleSeconds: 300),

        new BuiltInAlertRuleSpec(
            BuiltInKey: "workflow-retry-exceeded",
            Name: "workflow-retry-exceeded",
            Description:
                "A workflow exhausted its retry envelope and failed " +
                "permanently. CorrelationId: {correlationId}.",
            Severity: AlertSeverity.Critical,
            EventType: "WORKFLOW.RETRY_EXCEEDED",
            Predicate: """{"op":"always"}""",
            ThrottleSeconds: 0),

        new BuiltInAlertRuleSpec(
            BuiltInKey: "platform-api-unhealthy",
            Name: "platform-api-unhealthy",
            Description:
                "The Tamma platform API is observing sustained 5xx " +
                "rates. Check the central deployment / circuit " +
                "breaker state.",
            Severity: AlertSeverity.Critical,
            EventType: "PLATFORM.API.UNHEALTHY",
            Predicate: """{"op":"always"}""",
            ThrottleSeconds: 600),

        new BuiltInAlertRuleSpec(
            BuiltInKey: "secret-rotation-failed",
            Name: "secret-rotation-failed",
            Description:
                "A secret-rotation saga hit its compensation path. " +
                "The target credential is in an unknown state — " +
                "manual reconciliation required.",
            Severity: AlertSeverity.Critical,
            EventType: "SECRET.ROTATION.FAILED",
            Predicate: """{"op":"always"}""",
            ThrottleSeconds: 0),

        // Story 37-2 (AC10) — audit hash-chain tamper. Fires on every
        // AUDIT.CHAIN.TAMPER_DETECTED so detection fans out with no manual
        // rule setup. Throttled to avoid a storm if a scan re-detects the
        // same break on repeated verifies.
        new BuiltInAlertRuleSpec(
            BuiltInKey: "audit-chain-tamper",
            Name: "audit-chain-tamper",
            Description:
                "The tamper-evident audit hash-chain detected a broken link " +
                "(a record was modified, deleted, reordered, or a checkpoint " +
                "signature failed). Scope {scope}, sequence {chainSequence}. " +
                "Treat as a potential integrity incident.",
            Severity: AlertSeverity.Critical,
            EventType: "AUDIT.CHAIN.TAMPER_DETECTED",
            Predicate: """{"op":"always"}""",
            ThrottleSeconds: 300),
    };
}
