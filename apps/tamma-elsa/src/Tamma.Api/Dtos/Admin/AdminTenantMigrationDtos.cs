using Tamma.Data.Abstractions;

namespace Tamma.Api.Dtos.Admin;

/// <summary>
/// Wire shapes for <c>POST /api/admin/tenants/migrate</c> and
/// <c>GET /api/admin/tenants/migrate/{runId}</c> (Story 44-1 sweep hygiene,
/// 2026-07-30).
///
/// <para>Every response carries BOTH <c>mode</c> and <c>applied</c> because the
/// endpoint's default changed: a bare POST used to apply DDL fleet-wide and now
/// reports only. <c>mode</c> is the INTENT (<c>dry-run</c> | <c>apply</c>);
/// <c>applied</c> is what is KNOWN about the fleet's state, and a caller must
/// never have to infer either from counts.</para>
/// </summary>
public static class AdminTenantMigrationMode
{
    /// <summary>Report-only: pending counts per tenant, no DDL. The DEFAULT.</summary>
    public const string DryRun = "dry-run";

    /// <summary>Applies pending migrations to every provisioned tenant.</summary>
    public const string Apply = "apply";
}

/// <summary>
/// What is known to have been written to the fleet. <b>TRI-STATE as of the
/// 2026-07-30 review (Finding 1.3) — this field was a boolean and the boolean
/// said the opposite of the truth at the worst possible moment.</b>
///
/// <para>It was computed as <c>!dryRun &amp;&amp; state != failed</c>, so an apply run
/// reported <c>applied=true</c> while still <c>running</c> and before a single
/// tenant had been touched, and <c>applied=false</c> — the field whose whole
/// job is to mean "nothing was written" — after a partial failure that may have
/// migrated most of the fleet.</para>
///
/// <para>The invariant that now holds: <see cref="No"/> means nothing was
/// written, full stop. Anything else means DDL may have reached some tenants,
/// and the run's <c>result</c> lists exactly which (a failed run keeps its
/// partial per-tenant rows — see
/// <see cref="TenantMigrationSweepRun.ResultIsPartial"/>).</para>
/// </summary>
public static class AdminTenantMigrationApplied
{
    /// <summary>Guaranteed nothing was written: a dry run, or an apply that died having migrated zero tenants.</summary>
    public const string No = "not-applied";

    /// <summary>
    /// Some subset of the fleet may already carry the DDL: an apply that is
    /// still running, or one that died partway. Pessimistic by design — at the
    /// instant a 202 is written nothing has been migrated yet, but by the time
    /// the caller reads it that is no longer guaranteed, and only
    /// <see cref="No"/> is allowed to carry a guarantee.
    /// </summary>
    public const string Partial = "partially-applied";

    /// <summary>The apply sweep ran to completion over the whole fleet (individual tenants may still be <c>failed</c> rows).</summary>
    public const string Yes = "applied";
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
    string Applied,
    bool DryRun,
    int Total,
    int Migrated,
    int AlreadyCurrent,
    int Pending,
    int Failed,
    IReadOnlyList<TenantMigrationSweepEntry> Tenants,
    string Message)
{
    /// <param name="applied">
    /// Override for the tri-state. Defaults to the only two values a
    /// SELF-CONTAINED result can justify (a dry run wrote nothing; a completed
    /// apply wrote everything); a run wrapper that knows the result is partial
    /// passes <see cref="AdminTenantMigrationApplied.Partial"/> instead so the
    /// nested body never contradicts the run body around it.
    /// </param>
    /// <param name="message">Override for the human summary (a partial result needs to say so).</param>
    public static AdminTenantMigrationSweepResponse From(
        TenantMigrationSweepResult r, string? applied = null, string? message = null) =>
        new(
            r.DryRun ? AdminTenantMigrationMode.DryRun : AdminTenantMigrationMode.Apply,
            applied ?? (r.DryRun
                ? AdminTenantMigrationApplied.No
                : AdminTenantMigrationApplied.Yes),
            r.DryRun,
            r.Total,
            r.Migrated,
            r.AlreadyCurrent,
            r.Pending,
            r.Failed,
            r.Tenants,
            message ?? (r.DryRun
                ? $"DRY RUN — nothing was applied. {r.Pending} of {r.Total} tenant(s) have "
                  + "pending migrations. Re-POST with ?apply=true plus the X-Admin-Confirm "
                  + "header to apply."
                : $"Applied pending migrations to {r.Migrated} of {r.Total} tenant(s); "
                  + $"{r.Failed} failed."));
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
    string Applied,
    bool DryRun,
    DateTimeOffset StartedAt,
    string StatusUrl,
    string Message);

/// <summary>
/// <c>GET /api/admin/tenants/migrate/{runId}</c> — a run's state.
///
/// <para><see cref="Result"/> is null only while <see cref="State"/> is
/// <c>running</c>. A <c>failed</c> run carries the PARTIAL result: the tenants
/// that completed before the sweep died, with
/// <see cref="ResultIsPartial"/> true. "We do not know which tenants got the
/// DDL" is the worst possible post-failure state for a fleet-DDL primitive, so
/// the endpoint reports what it observed and labels it incomplete.</para>
/// </summary>
public record AdminTenantMigrationRunResponse(
    Guid RunId,
    string State,
    string Mode,
    string Applied,
    bool DryRun,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    AdminTenantMigrationSweepResponse? Result,
    bool ResultIsPartial)
{
    public static AdminTenantMigrationRunResponse From(TenantMigrationSweepRun run)
    {
        var applied = AppliedFor(run);
        var message = run.ResultIsPartial
            ? $"PARTIAL RESULT — the sweep did not finish. These {run.Result?.Total ?? 0} "
              + "tenant(s) are the ones that completed before it died; any tenant NOT listed was "
              + "either never attempted or was in flight. See 'error' for why it stopped."
            : null;

        return new(
            run.RunId,
            run.State,
            run.DryRun ? AdminTenantMigrationMode.DryRun : AdminTenantMigrationMode.Apply,
            applied,
            run.DryRun,
            run.StartedAt,
            run.CompletedAt,
            run.Error,
            run.Result is null
                ? null
                : AdminTenantMigrationSweepResponse.From(run.Result, applied, message),
            run.ResultIsPartial);
    }

    /// <summary>
    /// The tri-state. Only <see cref="AdminTenantMigrationApplied.No"/> carries
    /// a guarantee, so it is used only where one exists: a dry run (writes
    /// nothing by construction) and a failed apply whose partial result proves
    /// zero tenants were migrated. A running apply and a failed apply with an
    /// unknown or non-zero migrated count are both
    /// <see cref="AdminTenantMigrationApplied.Partial"/>.
    /// </summary>
    private static string AppliedFor(TenantMigrationSweepRun run)
    {
        if (run.DryRun) return AdminTenantMigrationApplied.No;
        if (run.State == TenantMigrationSweepRunState.Completed)
            return AdminTenantMigrationApplied.Yes;
        if (run.State == TenantMigrationSweepRunState.Failed && run.Result is { Migrated: 0 })
            return AdminTenantMigrationApplied.No;
        return AdminTenantMigrationApplied.Partial;
    }
}
