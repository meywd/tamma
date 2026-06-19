namespace Tamma.Data.Entities;

/// <summary>
/// Story 37-1 — one curated, normalized, compliance-relevant audit row,
/// materialized FROM the immutable DCB stream by the <c>AuditProjector</c>.
///
/// <para><b>This is a derived read-model, NOT a new event store.</b> The raw
/// <see cref="DomainEvent"/> / <see cref="PlatformEvent"/> rows remain the
/// authoritative source of truth; this table is rebuildable at any time
/// (truncate + reset cursor + re-project). Every row back-references its
/// origin via <see cref="SourceEventId"/> (the idempotency key) +
/// <see cref="SourceSequenceNumber"/> (the deterministic replay/chain order).</para>
///
/// <para><b>Scope routing</b> (Story 37-1 AC11): tenant-scoped rows live in the
/// per-tenant schema keyed by <see cref="TenantId"/> (SaaS); platform-scoped
/// rows live in the control plane (also keyed by <see cref="TenantId"/>, or
/// <c>null</c> for platform-only actions); single-user rows are keyed by
/// <see cref="UserId"/>. Exactly one of <see cref="TenantId"/> /
/// <see cref="UserId"/> is non-null (XOR CHECK, mirrors
/// <c>prompt_overrides.principal_xor</c>).</para>
/// </summary>
public class AuditRecord
{
    /// <summary>Surrogate PK — Postgres <c>gen_random_uuid()</c> default.</summary>
    public Guid Id { get; set; }

    /// <summary>Canonical DCB event-type string this row was projected from,
    /// e.g. <c>SECRET.REVEAL</c>. Always a key in <c>SensitiveActionCatalog.ByCode</c>.</summary>
    public string ActionCode { get; set; } = null!;

    /// <summary>Compliance category (the lowercased <c>AuditCategory</c> member name).</summary>
    public string Category { get; set; } = null!;

    /// <summary>Coarse triage severity (the lowercased <c>AuditSeverity</c> member name).</summary>
    public string Severity { get; set; } = null!;

    // ── Who ──

    /// <summary>The acting user (resolved from the raw event's tags/data). Null
    /// for system-initiated actions with no resolvable actor.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Point-in-time actor email snapshot (the user row may later change
    /// or be deleted; the audit row outlives the actor — SOC2 requirement).</summary>
    public string? ActorEmailSnapshot { get; set; }

    // ── What / target ──

    /// <summary>Target object type, e.g. <c>secret</c> / <c>user</c> / <c>tenant</c>.</summary>
    public string? TargetType { get; set; }

    /// <summary>Target object id (free-form string — may be a Guid, slug, or number).</summary>
    public string? TargetId { get; set; }

    /// <summary>Outcome — <c>success</c> | <c>failure</c> | <c>denied</c>.</summary>
    public string Outcome { get; set; } = "success";

    // ── Context ──

    /// <summary>Source IP, when the raw event carried one.</summary>
    public string? IpAddress { get; set; }

    /// <summary>User-agent, when the raw event carried one.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When the audited action actually occurred (the raw event's
    /// <c>CreatedAt</c>), stored as <c>timestamp with time zone</c>.</summary>
    public DateTime OccurredAt { get; set; }

    // ── Back-reference to the raw DCB event (immutable source of truth) ──

    /// <summary>The originating raw event id. UNIQUE — the idempotency key that
    /// makes the projection insert-if-absent and replay-safe (AC8).</summary>
    public Guid SourceEventId { get; set; }

    /// <summary>The originating raw event's DCB <c>SequenceNumber</c>. Preserves
    /// total order for deterministic replay and for Story 37-2's hash chain.</summary>
    public long SourceSequenceNumber { get; set; }

    /// <summary>Redacted JSON projection of the raw event's <c>Data</c>/<c>Tags</c>.
    /// Passed through <c>CredentialRedactor.Clean</c> BEFORE persistence — never
    /// "redacted on read" (AC10).</summary>
    public string PayloadJson { get; set; } = "{}";

    // ── Per-mode ownership — exactly one is non-null (XOR CHECK) ──

    /// <summary>SaaS-mode owner (the tenant). Null in single-user mode.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Single-user-mode owner (the sole user). Null in SaaS mode.</summary>
    public Guid? UserId { get; set; }

    // ── Reserved for Story 37-2 tamper-evidence (this story leaves both null) ──

    /// <summary>Reserved for Story 37-2 — the hash of this record in the chain.
    /// Story 37-1 leaves this null; 37-2 populates it. Do NOT compute here.</summary>
    public string? RecordHash { get; set; }

    /// <summary>Reserved for Story 37-2 — the previous record's hash, linking the
    /// chain. Story 37-1 leaves this null; 37-2 populates it.</summary>
    public string? PrevRecordHash { get; set; }
}
