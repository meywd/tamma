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
}
