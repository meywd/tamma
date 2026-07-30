using Tamma.Api.Dtos.Admin;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 44-1 AC8 — <c>POST /api/admin/tenants/migrate</c>, the fleet-wide
/// tenant-DDL sweep, plus its status poll. Platform-owner only
/// (<c>PlatformOwnerAccess</c>, asserted over HTTP by
/// <c>TenantMigrationEndpointAuthTests</c>).
///
/// <para><b>2026-07-30 — BREAKING CHANGE to the default.</b> Until this
/// revision the handler was <c>dryRun ?? false</c>: a bare
/// <c>POST /api/admin/tenants/migrate</c> with no body and no query applied
/// schema migrations to EVERY provisioned tenant. That made "poke the endpoint
/// to see what it does" a fleet-wide mutation — the most dangerous default on
/// the admin surface. The safe action is now the default:</para>
///
/// <list type="bullet">
///   <item><c>POST .../migrate</c> (bare) ⇒ DRY RUN. 200 with the per-tenant
///   pending counts, <c>applied=false</c>, nothing written.</item>
///   <item><c>POST .../migrate?apply=true</c> + <c>X-Admin-Confirm:
///   migrate-all-tenants</c> ⇒ the real sweep. 202 + a run id to poll.</item>
///   <item><c>POST .../migrate?dryRun=false</c> ⇒ 400. The old spelling for
///   "apply" is refused LOUDLY rather than silently reinterpreted: a caller
///   scripted against the old default must find out from an error, not from a
///   fleet that migrated when they expected a report.</item>
/// </list>
///
/// <para><b>Why <c>?apply=true</c> and not <c>?dryRun=false</c>:</b> the opt-in
/// to a destructive action reads as an affirmative, never as a double negative
/// — and it pairs with the confirmation header this admin surface already uses
/// for its destructive routes (<c>force-delete</c> and <c>cleanup</c> both
/// demand <c>X-Admin-Confirm</c> echoing the tenant id, AdminTenantsEndpoints).
/// A sweep has no single tenant id to echo, so the constant
/// <c>migrate-all-tenants</c> plays that role: it is not typeable by
/// accident.</para>
///
/// <para><b>Why apply is 202 and dry-run is 200:</b> only the apply path runs
/// DDL, and per-tenant migration time is unbounded — a fleet of any size
/// outlives the proxy/client timeout, and the pre-2026-07-30 handler left the
/// caller with a 504 and no result while the sweep kept running. The dry run
/// does one pooled connection and one <c>__TenantMigrationsHistory</c> read per
/// tenant, 4-way parallel, and is the DEFAULT — making an operator poll twice
/// to learn "nothing is pending" would be a worse surface. A caller who expects
/// their dry run to be slow anyway (a very large fleet) can pass
/// <c>?async=true</c> and get the same 202 + poll treatment.</para>
/// </summary>
public static class AdminTenantMigrationEndpoints
{
    /// <summary>The confirmation header this admin surface already uses.</summary>
    public const string ConfirmHeader = "X-Admin-Confirm";

    /// <summary>
    /// What the header must carry to authorise an apply sweep. There is no
    /// tenant id to echo (the blast radius is every tenant), so the value names
    /// the blast radius instead.
    /// </summary>
    public const string ConfirmValue = "migrate-all-tenants";

    /// <summary>Where a started run is polled.</summary>
    public static string StatusUrlFor(Guid runId) => $"/api/admin/tenants/migrate/{runId:D}";

    // ── POST /api/admin/tenants/migrate ──

    public static async Task<IResult> Migrate(
        HttpContext http,
        bool? apply,
        bool? dryRun,
        bool? async,
        int? maxConcurrency,
        ITenantMigrationSweeper sweeper,
        ITenantMigrationSweepRunner runner,
        CancellationToken ct = default)
    {
        // ── mode resolution (fail loudly on anything ambiguous) ──
        if (apply == true && dryRun == true)
            return Results.BadRequest(new
            {
                error = "conflicting_mode",
                message = "apply=true and dryRun=true are mutually exclusive. "
                    + "Omit both for a dry run; pass apply=true to apply.",
            });

        if (dryRun == false && apply != true)
            return Results.BadRequest(new
            {
                error = "apply_requires_explicit_opt_in",
                message = "dryRun=false no longer applies migrations. As of 2026-07-30 this "
                    + "endpoint defaults to a dry run; applying requires ?apply=true together "
                    + $"with the {ConfirmHeader}: {ConfirmValue} header.",
            });

        var applyMode = apply == true;
        var concurrency = maxConcurrency ?? TenantMigrationSweep.DefaultMaxConcurrency;

        if (applyMode)
        {
            // 2FA-lite, exactly as force-delete/cleanup do it one file over —
            // an explicit query flag alone is one fat-fingered curl away.
            var confirm = http.Request.Headers[ConfirmHeader].ToString();
            if (!string.Equals(confirm, ConfirmValue, StringComparison.OrdinalIgnoreCase))
                return Results.Json(
                    new
                    {
                        error = "confirmation_required",
                        message = $"{ConfirmHeader} header must be '{ConfirmValue}' to authorise "
                            + "applying schema migrations to every provisioned tenant.",
                    },
                    statusCode: StatusCodes.Status400BadRequest);

            return await StartAsync(runner, dryRun: false, concurrency, ct);
        }

        // Dry run — synchronous by default, 202 + poll on request.
        if (async == true)
            return await StartAsync(runner, dryRun: true, concurrency, ct);

        var result = await sweeper.SweepAsync(dryRun: true, concurrency, ct);
        return Results.Ok(AdminTenantMigrationSweepResponse.From(result));
    }

    private static async Task<IResult> StartAsync(
        ITenantMigrationSweepRunner runner, bool dryRun, int concurrency, CancellationToken ct)
    {
        var start = await runner.StartAsync(dryRun, concurrency, ct);

        if (!start.Accepted)
        {
            var conflict = start.Conflict!;
            return Results.Json(
                new
                {
                    error = "sweep_already_running",
                    scope = conflict.Scope,
                    runId = conflict.RunId,
                    startedAt = conflict.StartedAt,
                    statusUrl = conflict.RunId is { } id ? StatusUrlFor(id) : null,
                    message = conflict.Scope == TenantMigrationSweepConflict.ScopeThisInstance
                        ? $"A tenant-migration sweep started at {conflict.StartedAt:O} is already "
                          + "running on this instance. Poll its status URL; only one sweep may run "
                          + "at a time."
                        : "A tenant-migration sweep is already running on another instance "
                          + "(it holds the cluster-wide advisory lock). This instance cannot see "
                          + "its run id or start time. Retry once it releases the lock.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var run = start.Run!;
        var statusUrl = StatusUrlFor(run.RunId);
        return Results.Accepted(
            statusUrl,
            new AdminTenantMigrationAcceptedResponse(
                run.RunId,
                run.DryRun ? AdminTenantMigrationMode.DryRun : AdminTenantMigrationMode.Apply,
                Applied: !run.DryRun,
                run.DryRun,
                run.StartedAt,
                statusUrl,
                run.DryRun
                    ? "Dry-run sweep started — nothing will be applied. Poll the status URL."
                    : "Sweep started — pending migrations are being applied to every provisioned "
                      + "tenant. Poll the status URL; a tenant that fails is a row in the result, "
                      + "never an abort."));
    }

    // ── GET /api/admin/tenants/migrate/{runId} ──

    /// <summary>
    /// Run status. Run state lives in the process that accepted the POST, so a
    /// poll that load-balances onto a different pod cannot find it; that case
    /// answers 404 <c>run_not_found_on_this_instance</c> and reports whether a
    /// sweep holds the cluster lock somewhere, rather than pretending the run
    /// never existed.
    /// </summary>
    public static async Task<IResult> GetRun(
        Guid runId,
        ITenantMigrationSweepRunner runner,
        CancellationToken ct = default)
    {
        var run = runner.TryGetRun(runId);
        if (run is not null)
            return Results.Ok(AdminTenantMigrationRunResponse.From(run));

        var runningSomewhere = await runner.IsSweepRunningAsync(ct);
        return Results.Json(
            new
            {
                error = "run_not_found_on_this_instance",
                runId,
                sweepRunningOnSomeInstance = runningSomewhere,
                message = runningSomewhere
                    ? "This instance has no record of that run, but a sweep IS holding the "
                      + "cluster-wide lock — the run is owned by another instance. Poll the "
                      + "instance that accepted the POST, or re-POST to receive a 409 while it "
                      + "runs."
                    : "No such run on this instance, and no sweep is currently running anywhere "
                      + "in the cluster. Run state is in-memory and per-instance: it is lost on "
                      + "restart and not replicated.",
            },
            statusCode: StatusCodes.Status404NotFound);
    }
}
