using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 5 of <c>CreateTenantWorkflow</c>. Seeds the new tenant database
/// with any default rows the application needs at first boot. The current
/// build has nothing tenant-resident that requires seeding (default
/// system prompts live in the CP <c>prompt_overrides</c> resolution
/// chain, default sanitization rules ship as code, etc.) so the activity
/// is a structural placeholder that:
///
/// <list type="bullet">
///   <item><description>Round-trips a no-op SELECT against the tenant
///     connection so we have a live-fire smoke test that the migration
///     left a usable database behind.</description></item>
///   <item><description>Logs the round-trip latency for the dashboard.</description></item>
/// </list>
///
/// <para>When seeders accrete (per-tenant default budget config, per-tenant
/// default convention starter, etc.), they go inside this activity rather
/// than as a chain of mini-activities — the workflow shape stays stable.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Seed Tenant Defaults",
    "Connect to the new tenant DB and write any default rows (placeholder today).",
    Kind = ActivityKind.Task)]
public sealed class SeedTenantDefaultsActivity : TenantLifecycleActivity
{
    public override string StepName => "seed-defaults";

    [Input(Description = "Per-tenant connection string (same value used by the migrate step).")]
    public Input<string> TenantConnectionString { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var cs = TenantConnectionString.Get(context);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "SeedTenantDefaults: TenantConnectionString input is empty.");

        // Lightweight round-trip — Npgsql opens the connection from the
        // tenant role. If the migration step left the database in an
        // unhealthy state, we surface the error here as part of the
        // create flow rather than at the first end-user request.
        await using var conn = new Npgsql.NpgsqlConnection(cs);
        await conn.OpenAsync(context.CancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        var result = await cmd.ExecuteScalarAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if (result is not int ok || ok != 1)
        {
            throw new InvalidOperationException(
                $"SeedTenantDefaults: smoke-test SELECT 1 returned {result ?? "<null>"} "
                + "for tenant " + tenantId);
        }

        Logger?.LogInformation(
            "tenant.lifecycle.seed_defaults smoke_ok tenantId={TenantId}",
            tenantId);
    }
}
