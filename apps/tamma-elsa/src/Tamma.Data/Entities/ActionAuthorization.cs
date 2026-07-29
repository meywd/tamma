namespace Tamma.Data.Entities;

/// <summary>
/// Story 43-5 (AC4) — the authorization LEDGER: one human decision covers one
/// run of a governed action (keyed by correlation id), rather than one
/// decision per retry. Story 43-9's seams consume grants through
/// <c>IActionAuthorizationLedger.TryConsumeAsync</c>; the human decision
/// endpoint lands with 43-9.
///
/// <para><b>Control-plane resident in BOTH modes and EXCLUDED from the
/// destructive startup DROP list</b> — the same forced reasoning as
/// <see cref="ActionAssignment"/> (see its doc comment; 43-5 D1/D3): no FK to
/// wiped tables, IF-NOT-EXISTS idempotent migration, and the exclusion is
/// pinned by <c>ActionGovernanceResidencyTests</c>.</para>
///
/// <para><b>Three-scope principal CHECK</b>
/// (<c>ck_action_authorizations_principal_scope</c>): tenant-only, user-only,
/// or neither (a platform-scope authorization) — deliberately NOT named
/// <c>_principal_xor</c> (43-5 D2).</para>
///
/// <para><b>At most one OPEN row per (principal, correlation, target)</b>: a
/// partial unique index over
/// <c>(TenantId, UserId, CorrelationId, TargetKind, TargetKey)</c>
/// <c>NULLS NOT DISTINCT WHERE State IN ('pending','granted')</c> — a second
/// request while one is pending/granted conflicts; a denied/expired row
/// permits a fresh request.</para>
/// </summary>
public class ActionAuthorization
{
    public Guid Id { get; set; }

    /// <summary>SaaS principal — set iff <see cref="UserId"/> is NULL.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Single-user principal — set iff <see cref="TenantId"/> is NULL.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The run this decision covers (workflow-instance correlation id).</summary>
    public string CorrelationId { get; set; } = null!;

    /// <summary>The GRANTED scope: <c>action</c> or <c>group</c> — a group
    /// grant covers every member of that group
    /// (<c>ck_action_authorizations_target_kind</c>).</summary>
    public string TargetKind { get; set; } = null!;

    /// <summary>The granted target's wire string (action-key wire or group wire).</summary>
    public string TargetKey { get; set; } = null!;

    /// <summary><c>pending</c> | <c>granted</c> | <c>denied</c> | <c>expired</c>
    /// (<c>ck_action_authorizations_state</c>).</summary>
    public string State { get; set; } = null!;

    /// <summary>NOT NULL from day one (AC4).</summary>
    public DateTime RequestedAtUtc { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>The human who granted/denied.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Grant expiry (default +24h; config
    /// <c>Tamma:Governance:AuthorizationTtlHours</c>).</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Set when a seam consumes the grant — a consumed grant does not
    /// cover a second run.</summary>
    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>Free-text request/decision reason.</summary>
    public string? Reason { get; set; }

    /// <summary>The dial position at request time (audit).</summary>
    public int? AutonomyLevelAtRequest { get; set; }
}
