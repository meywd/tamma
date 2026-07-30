namespace Tamma.Data.Abstractions;

/// <summary>
/// Story 44-1 AC8/AC9 — the migrate-all-provisioned-tenants sweep. Before this
/// story, <see cref="ITenantDbMigrator.MigrateTenantAppAsync"/> had exactly two
/// production call sites, both creation-only — so a new tenant migration
/// reached only tenants provisioned AFTER the deploy and every existing tenant
/// got <c>42P01</c> on first read. The sweep closes that gap: it enumerates
/// the tenant registry, resolves each tenant's connection through the pooled
/// resolver (which decrypts the stored envelope — no plaintext credentials
/// needed), and replays the already-idempotent per-schema migration set.
///
/// <para><b>Operational contract:</b> an explicit admin action, never
/// automatic on startup (a boot-time sweep over N tenants serializes deploy on
/// N migrations and turns one bad migration into a total outage). One
/// tenant's failure is a row in the result, never an abort. Concurrency is
/// bounded because every migration takes a non-pooled physical connection
/// (see <c>EfTenantDbMigrator</c>'s <c>Pooling=false</c> note). Exposed over
/// HTTP as <c>POST /api/admin/tenants/migrate</c> (platform-owner only).</para>
/// </summary>
public interface ITenantMigrationSweeper
{
    /// <summary>
    /// Run the sweep. <paramref name="dryRun"/> reports the pending-migration
    /// count per tenant without applying anything;
    /// <paramref name="maxConcurrency"/> bounds parallel tenant migrations
    /// (default 4, clamped to [1, 16]).
    ///
    /// <para><paramref name="dryRun"/> has <b>no default</b> (2026-07-30): the
    /// same "the safe action must be the explicit-free one" reasoning that
    /// flipped the HTTP endpoint's default applies to the seam. A default of
    /// <c>false</c> made <c>SweepAsync()</c> — the shortest thing to write —
    /// mean "apply DDL to every tenant"; there is no defensible default for
    /// that choice, so every call site states it.</para>
    /// </summary>
    Task<TenantMigrationSweepResult> SweepAsync(
        bool dryRun,
        int maxConcurrency = TenantMigrationSweep.DefaultMaxConcurrency,
        CancellationToken ct = default);
}

/// <summary>Shared constants + outcome vocabulary for the sweep.</summary>
public static class TenantMigrationSweep
{
    public const int DefaultMaxConcurrency = 4;

    /// <summary>Pending migrations were applied to the tenant schema.</summary>
    public const string OutcomeMigrated = "migrated";

    /// <summary>The tenant's history table already records the full set — no-op.</summary>
    public const string OutcomeAlreadyCurrent = "already-current";

    /// <summary>Dry-run only: migrations are pending and were NOT applied.</summary>
    public const string OutcomePending = "pending";

    /// <summary>This tenant failed (resolution, connection, or migration); see the error. Never aborts the sweep.</summary>
    public const string OutcomeFailed = "failed";
}

/// <summary>One tenant's row in the sweep result.</summary>
public sealed record TenantMigrationSweepEntry(
    Guid TenantId,
    string Outcome,
    int PendingBefore,
    string? Error);

/// <summary>The per-tenant result list plus roll-up counts.</summary>
public sealed record TenantMigrationSweepResult(
    bool DryRun,
    int Total,
    int Migrated,
    int AlreadyCurrent,
    int Pending,
    int Failed,
    IReadOnlyList<TenantMigrationSweepEntry> Tenants);
