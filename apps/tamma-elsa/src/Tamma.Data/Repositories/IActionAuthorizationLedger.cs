using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 43-5 (AC4) — the authorization ledger seam: one human decision covers
/// one run of a governed action. 43-9's seams call
/// <see cref="TryConsumeAsync"/> at gate time; the human decision endpoint
/// (transitioning <c>pending → granted/denied</c>) lands with Story 43-9.
/// Same CP residency + parallel-plane rules as
/// <see cref="IActionAssignmentRepository"/>.
/// </summary>
public interface IActionAuthorizationLedger
{
    /// <summary>
    /// Record a pending authorization request. At most one LIVE open
    /// (pending/granted, not past expiry) row per (principal, correlation,
    /// target) — a second request while one is live returns the existing row
    /// instead of inserting (the partial unique index arbitrates the race).
    /// A time-expired open row is transitioned to <c>expired</c> (CAS) and a
    /// fresh pending row minted, so an unattended request can never deadlock
    /// its (principal, correlation, target) key forever (adversarial review
    /// F3, 2026-07-29).
    /// </summary>
    Task<ActionAuthorization> RequestAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string targetKind,
        string targetKey,
        string? reason,
        int? autonomyLevelAtRequest,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>
    /// Consume a live grant covering <paramref name="actionKeyWire"/> for this
    /// (principal, correlation): an <c>action</c>-scoped grant covers itself;
    /// a <c>group</c>-scoped grant covers every member of that group, where
    /// membership is resolved from <c>ActionCatalog</c> INSIDE the ledger —
    /// never from caller input (adversarial review F2: a caller-supplied group
    /// wire let a grant for one group be consumed for an action outside it).
    /// An action key with no catalog entry can only be covered by an exact
    /// action-scoped grant. An expired grant does not cover; a consumed grant
    /// does not cover a second call. Consumption is a conditional
    /// single-statement UPDATE (CAS) — under concurrency exactly one caller
    /// consumes a given grant (F1). On success the grant's
    /// <c>ConsumedAtUtc</c> is stamped and the row returned; null when no
    /// covering grant exists (or every candidate was consumed concurrently).
    /// </summary>
    Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        CancellationToken ct = default);

    /// <summary>Transition a row to granted/denied (the 43-9 decision path;
    /// shipped here so the state machine has one owner). A conditional
    /// single-statement UPDATE (<c>WHERE state = 'pending'</c> and not past
    /// expiry): under a concurrent grant-vs-deny race exactly one caller wins
    /// (F1). Returns the updated row, or null when the row is missing,
    /// already decided, expired, lost the race, or <b>belongs to a different
    /// governance principal</b>.
    ///
    /// <para><b>The principal is a REQUIRED parameter, not an optional filter</b>
    /// (adversarial review F6, 2026-08-01). This transition used to match on
    /// <c>Id</c> and <c>State</c> alone: anyone holding the guid could decide
    /// anyone's row, and the guid is handed out in the Seam C 409 body and the
    /// Seam E response, so in SaaS any tenant admin could GRANT another tenant's
    /// blocked effect. <c>ListAuthorizations</c> was already principal-scoped,
    /// with a comment explaining that merely ENUMERATING another principal's rows
    /// is a capability disclosure — deciding one is strictly worse. It is
    /// positional and mandatory so that every existing and future call site has to
    /// state which principal is acting, rather than inheriting an unscoped
    /// default.</para>
    ///
    /// <para><b>The principal is the row's OWNER, not the decider.</b>
    /// <paramref name="decidedByUserId"/> records WHO pressed the button (audit);
    /// <paramref name="tenantId"/>/<paramref name="userId"/> say WHOSE ledger the
    /// row must be in. In SaaS those are different by construction — a tenant
    /// admin decides a tenant-scoped row.</para></summary>
    /// <param name="tenantId">The acting governance principal's tenant, or null.</param>
    /// <param name="userId">The acting governance principal's user, or null.</param>
    Task<ActionAuthorization?> DecideAsync(
        Guid? tenantId, Guid? userId,
        Guid id, bool granted, Guid decidedByUserId, string? reason,
        CancellationToken ct = default);
}
