namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-2 — the server-derived slug convention for a custom (bespoke
/// enterprise) plan: <c>custom-{tenantId:N}-{seq}</c>. Encoding the full tenant
/// id (32 hex) into the slug makes the plan→tenant binding recoverable and
/// queryable WITHOUT a dedicated <c>CustomTenantId</c> column (kept additive-
/// migration-free per the catalog serialisation rule); the <c>{seq}</c> suffix
/// lets a tenant hold more than one bespoke plan. Total length ≤ 46 chars, well
/// under the <c>plans.Slug</c> 64-char limit.
///
/// <para>The public catalog excludes custom plans by construction
/// (<c>IsCustom == false</c> filter), so the slug is never resolvable through
/// <c>/api/pricing/plans*</c> regardless of its shape.</para>
/// </summary>
public static class CustomPlanSlug
{
    /// <summary>Prefix that every custom plan for <paramref name="tenantId"/> shares.</summary>
    public static string PrefixFor(Guid tenantId) => $"custom-{tenantId:N}-";

    /// <summary>Mint a fresh, unique-by-construction custom slug for a tenant.</summary>
    public static string New(Guid tenantId) =>
        PrefixFor(tenantId) + Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// True when <paramref name="slug"/> is a custom plan slug bound to
    /// <paramref name="tenantId"/>.
    /// </summary>
    public static bool IsBoundTo(string slug, Guid tenantId) =>
        slug is not null && slug.StartsWith(PrefixFor(tenantId), StringComparison.Ordinal);
}
