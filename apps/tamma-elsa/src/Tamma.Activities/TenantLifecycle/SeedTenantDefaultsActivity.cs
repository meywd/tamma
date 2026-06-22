using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Seeders;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 7 of <c>CreateTenantWorkflow</c>. Seeds the new tenant with any default
/// rows the application needs at first boot:
///
/// <list type="bullet">
///   <item><description>Round-trips a no-op SELECT against the tenant
///     connection so we have a live-fire smoke test that the migration
///     left a usable database behind.</description></item>
///   <item><description><b>Story 32-16 (AC10).</b> Seeds the CP-resident
///     per-tenant agent enablement: the platform <c>DefaultPersonaName</c>
///     persona is enabled for the freshly provisioned tenant so its catalog is
///     usable out of the box (enablement is otherwise default-deny for public
///     personas). This closes the SaaS half of AC10 — the single-user half is
///     wired in <c>EnsurePersonalTenantMiddleware</c>. Insert-missing-only +
///     best-effort/non-fatal: a seed failure must NOT abort tenant
///     creation.</description></item>
/// </list>
///
/// <para>When more seeders accrete (per-tenant default budget config, per-tenant
/// default convention starter, etc.), they go inside this activity rather
/// than as a chain of mini-activities — the workflow shape stays stable.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Seed Tenant Defaults",
    "Connect to the new tenant DB, smoke-test it, and seed default rows (incl. default-persona enablement).",
    Kind = ActivityKind.Task)]
public sealed class SeedTenantDefaultsActivity : TenantLifecycleActivity
{
    /// <summary>Config key for the platform default persona handle (Story 32-15).
    /// Kept as a literal because <c>Tamma.Activities</c> cannot reference
    /// <c>Tamma.Api</c>'s <c>DefaultPersonaOptions.SectionPath</c>.</summary>
    private const string DefaultPersonaConfigKey = "Tamma:Agents:DefaultPersonaName";

    /// <summary>Matches <c>DefaultPersonaOptions.DefaultPersonaName</c>'s default.</summary>
    private const string FallbackDefaultPersonaName = "claude";

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
        await using (var conn = new Npgsql.NpgsqlConnection(cs))
        {
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
        }

        Logger?.LogInformation(
            "tenant.lifecycle.seed_defaults smoke_ok tenantId={TenantId}",
            tenantId);

        // Story 32-16 (AC10) — seed the fresh SaaS tenant's default-persona
        // enablement against the CONTROL PLANE (the enablement table is
        // CP-resident in both modes), NOT the tenant schema. Best-effort/non-fatal.
        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var personaName = context.GetService<IConfiguration>()?[DefaultPersonaConfigKey]
            ?? FallbackDefaultPersonaName;

        await using var cpDb = await factory.CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false);
        await SeedTenantDefaultPersonaAsync(cpDb, personaName, tenantId, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Story 32-16 (AC10) — enable the platform default persona for a freshly
    /// provisioned SaaS <paramref name="tenantId"/> (tenant-keyed CP enablement
    /// row), insert-missing-only. <b>Best-effort/non-fatal</b>: a seed failure is
    /// WARN-logged and swallowed so it cannot abort tenant creation — mirroring
    /// how <c>EnsurePersonalTenantMiddleware</c> calls the seeder for the
    /// single-user (user-keyed) path. Directly callable (no Elsa runtime) so the
    /// wiring is unit-testable.
    /// </summary>
    public static async Task SeedTenantDefaultPersonaAsync(
        ControlPlaneDbContext cpDb,
        string defaultPersonaName,
        Guid tenantId,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await TenantEnablementSeeder.SeedDefaultPersonaAsync(
                cpDb, defaultPersonaName, tenantId: tenantId, userId: null,
                logger: logger, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "tenant.lifecycle.seed_defaults default_persona_enablement_failed "
                + "tenantId={TenantId} (non-fatal — tenant creation proceeds)", tenantId);
        }
    }
}
