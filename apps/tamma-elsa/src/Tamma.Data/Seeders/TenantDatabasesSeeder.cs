using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Unified-tenancy Phase 2 — registers the central database as pool
/// member #1 (Label "central", shared, all tiers) when tenant_databases
/// is empty, so single-user/dev and SaaS share one placement code path.
/// Operators add real pool rows (and may retire this one) via Phase 4
/// admin CRUD. Insert-missing-only: never updates an existing row.
/// </summary>
public static class TenantDatabasesSeeder
{
    public static readonly Guid CentralDatabaseId =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public static async Task SeedAsync(
        ControlPlaneDbContext context,
        string adminConnectionString,
        ITenantConnectionStringProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        ArgumentNullException.ThrowIfNull(protector);

        if (await context.TenantDatabases.AnyAsync(cancellationToken))
            return;

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var now = DateTime.UtcNow;
        context.TenantDatabases.Add(new TenantDatabase
        {
            Id = CentralDatabaseId,
            Label = "central",
            Host = builder.Host ?? "localhost",
            Port = builder.Port,
            AdminConnectionStringEncrypted = protector.Encrypt(adminConnectionString),
            PlacementClass = "shared",
            TierEligibility = ["free", "team", "enterprise"],
            TenantCapacity = null,
            TenantCount = 0,
            Status = "active",
            KekVersion = (short)protector.CurrentKekVersion,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
