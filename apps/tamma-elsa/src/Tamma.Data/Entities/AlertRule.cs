namespace Tamma.Data.Entities;

/// <summary>
/// Story 5.6 (Wave C.2) — one row per alert rule on the
/// control-plane <c>alert_rules</c> table. A rule subscribes to a
/// single DCB <see cref="EventType"/> and, for every matching event,
/// runs its <see cref="Predicate"/> to decide whether an
/// <c>AlertPayload</c> should be handed to <c>IAlertSink.RaiseAsync</c>.
///
/// <para><b>Built-in rules</b> (see <c>BuiltInAlertRules</c>) are seeded
/// on app startup by <c>BuiltInAlertRuleSeeder</c>, which keys idempotent
/// upserts off <see cref="BuiltInKey"/>. Admins cannot delete a
/// built-in row (409) and cannot edit <see cref="EventType"/>,
/// <see cref="Predicate"/>, <see cref="BuiltInKey"/>, or
/// <see cref="IsBuiltIn"/>. They MAY disable the rule
/// (<see cref="IsEnabled"/> = false), link channels, or override
/// severity / throttle.</para>
///
/// <para><b>Correlation</b>: rules using the <c>count_gte</c> predicate
/// correlate events by the <c>tenantId</c> tag by default. The
/// predicate root carries a <c>group_by</c> list to override (e.g.
/// <c>["workflowId"]</c> to count workflow-specific retry storms).</para>
/// </summary>
public class AlertRule
{
    public Guid Id { get; set; }

    /// <summary>Unique human-readable name (e.g. <c>budget-exhausted</c>).</summary>
    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// One of <c>critical</c>, <c>warning</c>, <c>info</c>. Drives the
    /// emitted <c>AlertPayload.Severity</c>.
    /// </summary>
    public string Severity { get; set; } = null!;

    /// <summary>
    /// The DCB event type this rule subscribes to. <c>*</c> matches
    /// every event — reserved for future; not used by any built-in.
    /// </summary>
    public string EventType { get; set; } = null!;

    /// <summary>
    /// Predicate DSL as raw JSON. Validated on write against the
    /// grammar in <c>AlertRulePredicate</c>.
    /// </summary>
    public string Predicate { get; set; } = "{}";

    /// <summary>
    /// Minimum gap between successive firings of this rule (per
    /// correlation group). The in-process evaluator enforces this in
    /// addition to the token-bucket rate limiter on the sink.
    /// </summary>
    public int ThrottleSeconds { get; set; }

    /// <summary>
    /// Target channels. Empty array = no fan-out (built-ins ship with
    /// an empty array until an admin links a channel via the UI).
    /// </summary>
    public Guid[] ChannelIds { get; set; } = Array.Empty<Guid>();

    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Stable key for idempotent seeder re-upserts. Unique partial
    /// index WHERE not null. Null for admin-created custom rules.
    /// </summary>
    public string? BuiltInKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
