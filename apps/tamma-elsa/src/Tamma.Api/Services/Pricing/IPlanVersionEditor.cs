using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — the ONLY write path into the plan price-book. Plans are
/// immutable, versioned rows: editing never mutates an <c>active</c>/
/// <c>deprecated</c> row in place. Extracted as an interface (Story 34-2 prep)
/// so consumers depend on the abstraction and the concrete
/// <see cref="PlanVersionEditor"/> is substitutable in tests.
/// </summary>
public interface IPlanVersionEditor
{
    /// <summary>
    /// Optional pre-flight immutability check. A <c>Plan</c> whose status is
    /// <c>active</c> or <c>deprecated</c> may NOT be mutated in place — only a
    /// <c>draft</c> row is writable (plus the controlled active→deprecated flip
    /// performed inside <see cref="CreateNewVersionAsync"/>). Throws
    /// <c>PLAN.VERSION.IMMUTABLE</c> (severity High) otherwise. This is a
    /// convenience that lets a caller reject early; it is NOT the authoritative
    /// guard — the <c>ControlPlaneDbContext</c> <c>SaveChanges</c> interceptor
    /// enforces the same invariant for every save path.
    /// </summary>
    void EnsureMutableOrThrow(Plan plan);

    /// <summary>
    /// Supersede the current <c>active</c> version of <paramref name="slug"/>
    /// with a new immutable version built from <paramref name="draft"/>. Returns
    /// the newly-created active <see cref="Plan"/> (with children attached).
    /// Throws <c>PLAN.VERSION.NO_ACTIVE</c> if the slug has no active version.
    /// </summary>
    Task<Plan> CreateNewVersionAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 — create the FIRST (v1, active) version of a brand-new plan
    /// <paramref name="slug"/>. Throws <c>PLAN.SLUG.EXISTS</c> if any version of
    /// the slug already exists (the caller must version via
    /// <see cref="VersionPlanAsync"/> instead). Emits
    /// <c>PLAN.CATALOG.UPDATED</c> (action=created).
    /// </summary>
    Task<Plan> CreateInitialVersionAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 — version an existing plan: a thin admin-surface wrapper over
    /// <see cref="CreateNewVersionAsync"/> (reusing ALL of the supersede/deprecate
    /// versioning logic and its <c>PLAN.VERSION.CREATED</c>/<c>PLAN.DEPRECATED</c>
    /// events) that additionally emits the admin-surface
    /// <c>PLAN.CATALOG.UPDATED</c> (action=versioned) event.
    /// </summary>
    Task<Plan> VersionPlanAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 — mint a bespoke enterprise plan (v1, active,
    /// <c>IsCustom = true</c>) bound to <paramref name="tenantId"/>. The slug is
    /// server-derived (<see cref="CustomPlanSlug"/>); the plan is excluded from
    /// the public catalog by construction. Emits <c>PLAN.CUSTOM.CREATED</c> with
    /// the bound tenant id in tags + data.
    /// </summary>
    Task<Plan> CreateCustomVersionAsync(
        Guid tenantId,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default);

    /// <summary>
    /// Story 34-2 — deprecate a specific plan version. Counts tenants whose
    /// assignment pins that version; when the count is &gt; 0 and
    /// <paramref name="force"/> is false NO write happens and the result reports
    /// <c>Deprecated = false</c> (the endpoint returns 409). With
    /// <paramref name="force"/> (or zero affected tenants) the version flips to
    /// <c>deprecated</c> — existing tenants stay pinned to it (immutability rule)
    /// — and <c>PLAN.CATALOG.UPDATED</c> (action=deprecated) is emitted. Throws
    /// <c>PLAN.VERSION.NOT_FOUND</c> for an unknown (slug, version).
    /// </summary>
    Task<PlanDeprecationResult> DeprecateVersionAsync(
        string slug,
        int version,
        bool force,
        PlanEditorPrincipal principal,
        CancellationToken ct = default);
}

/// <summary>
/// Story 34-2 — outcome of <see cref="IPlanVersionEditor.DeprecateVersionAsync"/>.
/// <see cref="Deprecated"/> is false ONLY on the blocked path (active
/// assignments + no force); <see cref="AffectedTenantCount"/> is the number of
/// tenants pinned to the version either way.
/// </summary>
public sealed record PlanDeprecationResult(bool Deprecated, int AffectedTenantCount);
