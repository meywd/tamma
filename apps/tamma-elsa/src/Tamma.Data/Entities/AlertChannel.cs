namespace Tamma.Data.Entities;

/// <summary>
/// Story 5.6 / Story 1.5-37 (Wave C.1) — one row per configured
/// delivery target on the control-plane <c>alert_channels</c> table.
/// Platform-scoped channels (<see cref="TenantId"/> is <c>null</c>)
/// fan out to every alert that doesn't carry a <c>TenantId</c>;
/// tenant-scoped channels only match alerts for their own tenant.
///
/// <para><b>Secret isolation invariant</b>: the <see cref="Config"/>
/// column stores non-secret routing information only (target email,
/// subject prefix, severity filter, etc.). Every secret credential
/// — webhook URL, PagerDuty routing_key, SMTP password, webhook HMAC
/// shared secret — is resolved through
/// <see cref="CredentialsSecretId"/> by looking up the plaintext via
/// Story 29-1's <c>ISecretStoreBackend</c>. Tests enforce that
/// <see cref="Config"/> does not carry any field matching the
/// conventional secret names; violations are a deployment bug.</para>
/// </summary>
public class AlertChannel
{
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant owning this channel, or <c>null</c> for a
    /// platform-scoped channel. Platform-scoped channels deliver
    /// platform-scoped alerts; tenant-scoped channels deliver only
    /// alerts for their own tenant.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>Human-friendly label shown on the admin UI.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// One of <c>email</c>, <c>slack</c>, <c>pagerduty</c>,
    /// <c>webhook</c>. Enforced by a CHECK constraint in the
    /// migration; <c>IAlertChannelRegistry</c> resolves an
    /// <c>IAlertChannel</c> implementation by this string.
    /// </summary>
    public string ChannelType { get; set; } = null!;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Non-secret configuration blob. Stored as <c>jsonb</c>.
    /// Schema depends on <see cref="ChannelType"/>:
    /// <list type="bullet">
    ///   <item><description>email: <c>{"toAddress":"ops@…","subjectPrefix":"[ALERT]"}</c></description></item>
    ///   <item><description>slack: <c>{"severityFilter":["critical","warning"]}</c></description></item>
    ///   <item><description>pagerduty: <c>{"severityFilter":["critical"]}</c></description></item>
    ///   <item><description>webhook: <c>{"url":"https://…","severityFilter":["critical","warning"]}</c></description></item>
    /// </list>
    /// </summary>
    public string Config { get; set; } = "{}";

    /// <summary>
    /// FK into Story 29-1's secret store. Holds the Slack webhook
    /// URL / PagerDuty routing_key / webhook HMAC secret for this
    /// channel. <c>null</c> when the channel type doesn't require
    /// a credential (email channels route through the shared SMTP
    /// sender whose credentials live in the cabinet already).
    /// </summary>
    public Guid? CredentialsSecretId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
