using Tamma.Core.Enums;

namespace Tamma.Data.Entities;

/// <summary>
/// Story 34-1 — one typed quota row per <see cref="EntitlementMetricKey"/> per
/// <see cref="Plan"/> version. <see cref="LimitValue"/> = <c>NULL</c> means
/// unlimited. <see cref="MetricKey"/> is persisted as the snake_case string
/// via a value converter (never the ordinal) so metering / pricing /
/// enforcement key off the same wire form. CP-resident; immutable once the
/// owning plan version is active/deprecated.
///
/// <para>This story <b>stores</b> the limit; it does NOT enforce it — quota
/// enforcement is a later Epic 34 story that reads
/// <c>PlanSnapshot.Entitlements</c>.</para>
/// </summary>
public class PlanEntitlement
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning plan version.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Quota dimension — value-converted to snake_case text.</summary>
    public EntitlementMetricKey MetricKey { get; set; }

    /// <summary>The limit for this metric; <c>NULL</c> = unlimited.</summary>
    public long? LimitValue { get; set; }

    /// <summary>Reset window: <c>monthly</c> | <c>total</c>.</summary>
    public string Period { get; set; } = "monthly";

    /// <summary>Behaviour at the limit: <c>block</c> | <c>allow</c> | <c>meter</c>.</summary>
    public string OverageMode { get; set; } = "block";
}
