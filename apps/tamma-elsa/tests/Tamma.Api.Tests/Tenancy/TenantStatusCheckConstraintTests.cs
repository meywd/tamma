using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Phase 4 Task 1 — real-Postgres probe of the <c>ck_tenants_status</c>
/// CHECK constraint. The Phase 0 collapsed baseline hand-mirrors the CHECK
/// SQL in four places (TammaModelConfiguration + InitialControlPlane
/// migration + its Designer + the model snapshot); this fixture asserts the
/// constraint that actually lands in the database accepts the Phase 4
/// <c>draining</c> status (the move's read-only window) and still rejects
/// arbitrary values with SQLSTATE 23514 (check_violation).
/// </summary>
[TestFixture]
public class TenantStatusCheckConstraintTests
{
    [SetUp]
    public Task SetUp() => TenancySetUpFixture.ResetDatabaseAsync();

    /// <summary>
    /// Seeds a tenant row WITH an encrypted-connection-string envelope so
    /// the sibling <c>ck_tenants_connection_string_present</c> CHECK is
    /// satisfied for envelope-requiring statuses (draining tenants always
    /// have envelopes — they are mid-move from an active placement).
    /// </summary>
    private static async Task<Guid> SeedTenantWithEnvelopeAsync()
    {
        var tenantId = Guid.NewGuid();
        using var scope = TenancySetUpFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var entry = db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "ck-status-probe",
            Slug = $"ck-{tenantId:N}"[..20],
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        entry.Property("Status").CurrentValue = "active";
        entry.Property("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3 };
        await db.SaveChangesAsync();
        return tenantId;
    }

    private static async Task<int> UpdateStatusAsync(Guid tenantId, string status)
    {
        await using var conn = new NpgsqlConnection(TenancySetUpFixture.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET \"Status\" = @status WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("id", tenantId);
        return await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task Draining_PassesTheStatusCheck()
    {
        var tenantId = await SeedTenantWithEnvelopeAsync();

        var rows = await UpdateStatusAsync(tenantId, "draining");

        rows.Should().Be(1,
            "Phase 4 adds 'draining' to ck_tenants_status — the UPDATE "
            + "must succeed against the real constraint");
    }

    [Test]
    public async Task BogusStatus_StillFailsTheCheck_With23514()
    {
        var tenantId = await SeedTenantWithEnvelopeAsync();

        var act = async () => await UpdateStatusAsync(tenantId, "bogus");

        var ex = await act.Should().ThrowAsync<PostgresException>(
            "ck_tenants_status must keep rejecting values outside the enumeration");
        ex.Which.SqlState.Should().Be("23514"); // check_violation
        ex.Which.ConstraintName.Should().Be("ck_tenants_status");
    }
}
