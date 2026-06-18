namespace Tamma.Data.Entities;

/// <summary>
/// Story 34-1 — a single typed feature flag on a <see cref="Plan"/> version.
/// Either a boolean capability flag (e.g. <c>byok_allowed = true</c>) or a
/// string feature value (e.g. <c>support_tier = priority</c>). One row per
/// <c>(PlanId, FeatureKey)</c>. CP-resident; immutable once its plan version
/// is <c>active</c>/<c>deprecated</c> (the immutability guard lives in
/// <c>PlanVersionEditor</c>).
/// </summary>
public class PlanFeature
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning plan version.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Feature key (e.g. <c>byok_allowed</c>, <c>support_tier</c>).</summary>
    public string FeatureKey { get; set; } = null!;

    /// <summary>Boolean value for capability flags; NULL when string-valued.</summary>
    public bool? BoolValue { get; set; }

    /// <summary>String value for non-boolean features; NULL when bool-valued.</summary>
    public string? StringValue { get; set; }
}
