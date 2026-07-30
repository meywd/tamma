using Tamma.Data.Abstractions;

namespace Tamma.Api.Dtos.Admin;

/// <summary>
/// Wire shapes for <c>POST /api/admin/tenants/migrate</c> and
/// <c>GET /api/admin/tenants/migrate/{runId}</c> (Story 44-1 sweep hygiene,
/// 2026-07-30).
///
/// <para>Every response carries BOTH <c>mode</c> and <c>applied</c> because the
/// endpoint's default changed: a bare POST used to apply DDL fleet-wide and now
/// reports only. A caller must never have to infer from counts which of the two
/// happened, and <c>applied=false</c> is the field that says "nothing was
/// written" without ambiguity.</para>
/// </summary>
public static class AdminTenantMigrationMode
{
    /// <summary>Report-only: pending counts per tenant, no DDL. The DEFAULT.</summary>
    public const string DryRun = "dry-run";

    /// <summary>Applies pending migrations to every provisioned tenant.</summary>
    public const string Apply = "apply";
}

/// <summary>
/// 200 body for a synchronous (dry-run) sweep. Deliberately FLAT — it keeps the
/// pre-2026-07-30 result fields (<c>dryRun</c>, <c>total</c>, <c>migrated</c>,
/// <c>alreadyCurrent</c>, <c>pending</c>, <c>failed</c>, <c>tenants</c>) at the
/// top level so existing readers keep working, and adds <c>mode</c> /
/// <c>applied</c> / <c>message</c> on top.
/// </summary>
public record AdminTenantMigrationSweepResponse(
    string Mode,
    bool Applied,
    bool DryRun,
    int Total,
    int Migrated,
    int AlreadyCurrent,
    int Pending,
    int Failed,
    IReadOnlyList<TenantMigrationSweepEntry> Tenants,
    string Message)
{
    public static AdminTenantMigrationSweepResponse From(TenantMigrationSweepResult r) =>
        new(
            r.DryRun ? AdminTenantMigrationMode.DryRun : AdminTenantMigrationMode.Apply,
            Applied: !r.DryRun,
            r.DryRun,
            r.Total,
            r.Migrated,
            r.AlreadyCurrent,
            r.Pending,
            r.Failed,
            r.Tenants,
            r.DryRun
                ? $"DRY RUN — nothing was applied. {r.Pending} of {r.Total} tenant(s) have "
                  + "pending migrations. Re-POST with ?apply=true plus the X-Admin-Confirm "
                  + "header to apply."
                : $"Applied pending migrations to {r.Migrated} of {r.Total} tenant(s); "
                  + $"{r.Failed} failed.");
}

/// <summary>
/// 202 body: the sweep runs in the background and is polled at
/// <see cref="StatusUrl"/> — the same 202-plus-status-poll shape the
/// provisioning (<c>POST /api/admin/tenants/{id}/provision</c>) and tenant-move
/// endpoints use.
/// </summary>
public record AdminTenantMigrationAcceptedResponse(
    Guid RunId,
    string Mode,
    bool Applied,
    bool DryRun,
    DateTimeOffset StartedAt,
    string StatusUrl,
    string Message);

/// <summary>
/// <c>GET /api/admin/tenants/migrate/{runId}</c> — a run's state.
/// <see cref="Result"/> is null until <see cref="State"/> leaves
/// <c>running</c>.
/// </summary>
public record AdminTenantMigrationRunResponse(
    Guid RunId,
    string State,
    string Mode,
    bool Applied,
    bool DryRun,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    AdminTenantMigrationSweepResponse? Result)
{
    public static AdminTenantMigrationRunResponse From(TenantMigrationSweepRun run) =>
        new(
            run.RunId,
            run.State,
            run.DryRun ? AdminTenantMigrationMode.DryRun : AdminTenantMigrationMode.Apply,
            Applied: !run.DryRun && run.State != TenantMigrationSweepRunState.Failed,
            run.DryRun,
            run.StartedAt,
            run.CompletedAt,
            run.Error,
            run.Result is null ? null : AdminTenantMigrationSweepResponse.From(run.Result));
}
