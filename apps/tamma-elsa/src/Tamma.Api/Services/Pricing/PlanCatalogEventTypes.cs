namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — DCB event type names emitted by <see cref="PlanVersionEditor"/>
/// to the control-plane <c>platform_events</c> store. CP-resident (the
/// <c>AlertRuleEvaluator</c> already polls that table, so a later story can
/// alert on catalog drift with no new plumbing). Pattern:
/// <c>AGGREGATE.ACTION.STATUS</c>.
/// </summary>
public static class PlanCatalogEventTypes
{
    /// <summary>A new immutable plan version was activated.</summary>
    public const string VersionCreated = "PLAN.VERSION.CREATED";

    /// <summary>A prior plan version was flipped to <c>deprecated</c>.</summary>
    public const string Deprecated = "PLAN.DEPRECATED";
}
