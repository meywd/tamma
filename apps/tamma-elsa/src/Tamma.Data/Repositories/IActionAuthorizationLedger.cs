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
    /// action-scoped grant. An expired grant does not cover.
    ///
    /// <para><b>Story 43-14 — scope-aware (Amendment 2-A).</b> A
    /// <c>single-use</c> grant (the default; today's semantics) is consumed by a
    /// conditional single-statement UPDATE (CAS) — under concurrency exactly one
    /// caller consumes it (F1), its <c>ConsumedAtUtc</c> is stamped, and a second
    /// call does NOT cover. A <c>correlation-standing</c> grant is SATISFIED for
    /// every ask matching (principal, correlation, target) WITHOUT any write: its
    /// <c>ConsumedAtUtc</c> stays NULL and it keeps covering until it expires or
    /// its correlation ends. When both a standing and a single-use grant coexist
    /// for the same target, the standing grant is preferred so the person's
    /// one-call single-use grant is not burned by a repeat ask.</para>
    ///
    /// <para>On success the covering grant is returned; null when no covering
    /// grant exists (or every single-use candidate was consumed concurrently).</para>
    /// </summary>
    Task<ActionAuthorization?> TryConsumeAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string actionKeyWire,
        CancellationToken ct = default);

    /// <summary>
    /// Story 43-14 (Amendment 2-B) — MINT a <c>correlation-standing</c> grant
    /// directly in the <c>granted</c> state, at a workflow's human-approval
    /// decision point. A workflow approval is not a request-then-decide: the row
    /// is born granted so the resumed workflow's next mediated call passes Seam C
    /// via <c>ReasonCoveredByAuthorization</c> instead of 409ing a human's "yes".
    ///
    /// <para>Idempotent under the open-row unique index
    /// (principal, correlation, target):
    /// <list type="bullet">
    ///   <item><description>a pending row from an earlier Seam C 409 is DECIDED
    ///   granted + upgraded to <c>correlation-standing</c> (one conditional
    ///   UPDATE, the <c>DecideAsync</c> CAS shape);</description></item>
    ///   <item><description>a granted row is returned as-is (a second mint in the
    ///   same correlation is a no-op);</description></item>
    ///   <item><description>otherwise a fresh granted row is inserted, with the
    ///   same bounded unique-violation retry <see cref="RequestAsync"/> uses.</description></item>
    /// </list></para>
    /// </summary>
    /// <remarks>Default throws — only the real EF ledger mints. A lightweight
    /// double that never mints (the gate-consult path only reads) keeps
    /// compiling without stubbing this.</remarks>
    Task<ActionAuthorization> MintStandingGrantAsync(
        Guid? tenantId,
        Guid? userId,
        string correlationId,
        string targetKind,
        string targetKey,
        Guid decidedByUserId,
        string? reason,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This IActionAuthorizationLedger implementation does not mint standing grants.");

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

    /// <summary>
    /// Story 43-15 (Amendment 2-H) — the DECIDED grant rows for one principal
    /// since <paramref name="sinceUtc"/> (both <c>granted</c> and <c>denied</c>,
    /// keyed by their <c>DecidedAtUtc</c>). Backs the dial-diff preview's
    /// per-action approve rate = <c>granted / (granted + denied)</c> per
    /// <c>TargetKey</c>. GROUP grants are NOT attributed to member actions in v1
    /// (the reader only reads <c>action</c>-kind rows) — a deliberate simplification
    /// recorded in <c>ActionTelemetryReader</c>.
    ///
    /// <para>Read-only aggregate — the grant table is structurally EMPTY until
    /// something is gated and decided (the H chicken-and-egg), so an empty result
    /// is the ordinary case and the reader renders "no data", never a 0% rate.</para>
    ///
    /// <para>Default returns an empty list so a lightweight double is never an
    /// approve-rate source; the real ledger overrides it.</para>
    /// </summary>
    Task<IReadOnlyList<ActionAuthorization>> ListDecidedSinceAsync(
        Guid? tenantId, Guid? userId, DateTime sinceUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActionAuthorization>>(Array.Empty<ActionAuthorization>());
}
