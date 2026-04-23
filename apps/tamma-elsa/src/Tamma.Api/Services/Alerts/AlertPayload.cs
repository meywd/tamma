namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Story 5.6 (Wave C.1) — typed input to <see cref="IAlertSink.RaiseAsync"/>.
/// Keeps the payload shape intentionally tight: everything the dispatcher
/// needs to decide channel fan-out, rate limiting, and audit-event emission.
///
/// <para><b>Canonical severities</b> are <c>"critical"</c>, <c>"warning"</c>,
/// <c>"info"</c>; violations trigger a validation failure in
/// <see cref="PostgresAlertSink.RaiseAsync"/> before any DB write. The
/// string-typed severity matches the CHECK constraint on
/// <see cref="Tamma.Data.Entities.Alert.Severity"/> — one source of truth,
/// one validation point.</para>
/// </summary>
/// <param name="Severity">One of <c>critical</c>, <c>warning</c>, <c>info</c>.</param>
/// <param name="Title">Short human-readable headline. Max 512 chars.</param>
/// <param name="Description">Multi-line body. No cap beyond Postgres
/// <c>text</c>; keep it reasonable.</param>
/// <param name="CorrelationId">Optional grouping id (e.g. workflow run id)
/// for cross-alert correlation on dashboards.</param>
/// <param name="TenantId">Optional tenant scope. Null = platform-wide
/// alert that only shows on the admin feed.</param>
/// <param name="RuleId">Optional forward-reference to Wave C.2's
/// <c>alert_rules</c>. Null in Wave C.1; skipping the rate limiter.</param>
/// <param name="Metadata">Free-form structured context stored as JSON.
/// Never contains secrets or credentials.</param>
public sealed record AlertPayload(
    string Severity,
    string Title,
    string Description,
    string? CorrelationId = null,
    Guid? TenantId = null,
    Guid? RuleId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// Severity constants. Keep these in sync with the CHECK constraint
/// on <c>alerts.severity</c>.
/// </summary>
public static class AlertSeverity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Info = "info";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Critical, Warning, Info,
    };

    public static bool IsValid(string severity) =>
        severity is Critical or Warning or Info;
}

/// <summary>
/// Status constants. Keep these in sync with the CHECK constraint
/// on <c>alerts.status</c>.
/// </summary>
public static class AlertStatus
{
    public const string Active = "active";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
}

/// <summary>
/// Channel type constants. Keep these in sync with the CHECK
/// constraint on <c>alert_channels.channel_type</c> and with
/// <see cref="IAlertChannel.ChannelType"/> values.
/// </summary>
public static class AlertChannelType
{
    public const string Email = "email";
    public const string Slack = "slack";
    public const string PagerDuty = "pagerduty";
    public const string Webhook = "webhook";

    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Email, Slack, PagerDuty, Webhook,
    };
}

/// <summary>
/// Delivery-attempt status constants. Keep in sync with the
/// CHECK constraint on <c>alert_delivery_attempts.status</c>.
/// </summary>
public static class AlertDeliveryStatus
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string DroppedRateLimit = "dropped_rate_limit";
}

/// <summary>
/// DCB event type constants emitted through the alert pipeline.
/// Every lifecycle transition and every delivery outcome emits one
/// of these for the audit trail.
/// </summary>
public static class AlertEventTypes
{
    public const string Raised = "ALERT.RAISED";
    public const string Acknowledged = "ALERT.ACKNOWLEDGED";
    public const string Resolved = "ALERT.RESOLVED";
    public const string DeliverySuccess = "ALERT.DELIVERY_SUCCESS";
    public const string DeliveryFailed = "ALERT.DELIVERY_FAILED";
    public const string DeliveryDropped = "ALERT.DELIVERY_DROPPED";
}
