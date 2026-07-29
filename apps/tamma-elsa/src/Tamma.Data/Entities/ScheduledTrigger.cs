namespace Tamma.Data.Entities;

/// <summary>
/// Story 41-30 (D1) — one schedule definition: "fire workflow definition
/// <see cref="DefinitionId"/> for tenant <see cref="TenantId"/> on cron
/// cadence <see cref="CronExpression"/>". The tenant-aware scheduled-trigger
/// seam's registry table (<c>scheduled_triggers</c>).
///
/// <para><b>Residency: control plane, not tenant schema</b> — the sweeper
/// (<c>TenantScheduledTriggerService</c>) must enumerate schedules ACROSS
/// tenants in one query; a tenant-schema table cannot be scanned without
/// opening N connections first, and a new tenant-schema migration would not
/// reach already-provisioned tenants (same three reasons Story 43-5 recorded
/// for <c>action_assignments</c>).</para>
///
/// <para><b><c>TenantId == null</c> is a PLATFORM DEFAULT TEMPLATE</b> (D6):
/// the tick materialises a concrete per-tenant row for every active tenant
/// that lacks one for the same <c>(DefinitionId, Name)</c>, then fires the
/// concrete row. Templates themselves are never fired — firing a template
/// would collapse the fire ledger's <c>UNIQUE (trigger_id, window_key)</c>
/// across tenants, which is the tenant-suppression bug
/// (<c>HourlyAnalyticsRollupScheduler.cs:241</c>) in a new costume.</para>
///
/// <para><b>The definition id is DATA, never a compile-time constant</b>
/// (AC3): the seam is target-agnostic; the admin API validates writes
/// against a closed allowlist of schedulable definition ids.</para>
///
/// <para><b>No FK to <c>tenants</c></b> — deliberately, mirroring
/// <see cref="ProviderSetting"/>: both schedule tables are EXCLUDED from the
/// destructive startup DROP list (AC7) so a deploy cannot silently disable
/// every tenant's audits, while <c>tenants</c> IS wiped; a cascade FK would
/// take the surviving schedule rows with it. Orphaned tenant ids are simply
/// never matched by the active-tenant snapshot at tick time.</para>
/// </summary>
public class ScheduledTrigger
{
    public Guid Id { get; set; }

    /// <summary>Owning tenant; <c>null</c> = platform default template (D6).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The Elsa workflow definition id to dispatch. Row DATA (AC3).</summary>
    public string DefinitionId { get; set; } = null!;

    /// <summary>Stable per-tenant schedule name (part of the natural key).</summary>
    public string Name { get; set; } = null!;

    /// <summary>Standard 5-field cron expression, evaluated in UTC (AC5).</summary>
    public string CronExpression { get; set; } = null!;

    public bool Enabled { get; set; } = true;

    /// <summary>Opaque JSON object merged into the dispatch inputs (e.g. a repo filter).</summary>
    public string InputJson { get; set; } = "{}";

    /// <summary>Next computed due instant (UTC); null until first computed.</summary>
    public DateTime? NextDueAt { get; set; }

    /// <summary>The most recent fired window key (informational; the ledger is authoritative).</summary>
    public string? LastWindowKey { get; set; }

    public DateTime? LastFiredAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>User who created the row (admin API); null for materialised rows.</summary>
    public Guid? CreatedBy { get; set; }
}
