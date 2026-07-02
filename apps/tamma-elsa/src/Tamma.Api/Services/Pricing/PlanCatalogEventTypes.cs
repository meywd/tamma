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

    /// <summary>
    /// Story 34-2 — admin-surface catalog mutation (create / version /
    /// deprecate). Carries an <c>action</c> tag (<c>created|versioned|deprecated</c>)
    /// so a single event type covers the whole admin write surface; the
    /// lower-level <see cref="VersionCreated"/> / <see cref="Deprecated"/>
    /// lifecycle events (34-1) are still emitted by the version path.
    /// </summary>
    public const string CatalogUpdated = "PLAN.CATALOG.UPDATED";

    /// <summary>
    /// Story 34-2 — a bespoke enterprise plan was minted and bound to a single
    /// tenant. Carries the bound <c>tenantId</c> in both tags and data.
    /// </summary>
    public const string CustomCreated = "PLAN.CUSTOM.CREATED";
}
