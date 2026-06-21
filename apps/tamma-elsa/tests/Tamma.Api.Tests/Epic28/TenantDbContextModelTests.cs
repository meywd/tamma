using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-3 model-shape tests for <see cref="TenantDbContext"/>.
/// Asserts the new context maps the tenant-resident tables, omits
/// every <c>TenantId</c> column (the discriminator is implicit in the
/// connection string per Doc 01 §1.4), and pins the api_keys CHECK
/// constraint that locks the tenant DB to <c>Scope='tenant'</c>.
/// </summary>
[TestFixture]
public class TenantDbContextModelTests
{
    private static TenantDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=tenant_test;Username=tamma;Password=tamma")
            .Options;
        return new TenantDbContext(options);
    }

    [Test]
    public void Model_Has_Tenant_Resident_Tables()
    {
        using var ctx = CreateContext();

        var tableNames = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToHashSet()!;

        tableNames.Should().Contain(new[]
        {
            "agent_configs",
            // Story 32-2 — SaaS tenant-keyed role→agent selections (dual-resident
            // with the CP single-user rows).
            "agent_role_selections",
            "prompt_overrides",
            "conventions",
            "provider_health",
            "provider_diagnostics",
            "sanitization_rules",
            "workflow_definitions",
            "workflow_instances",
            "domain_events",
            "queued_tasks",
            "email_outbox",
            "budget_configs",
            "api_keys",
            "mentorship_sessions",
            "mentorship_events",
            "junior_developers",
            "stories",
        });
    }

    [Test]
    [Ignore("Story 28-1 PR D — kept ignored: the entity move from CP to "
        + "Tenant has landed (the 11 + 4 mentorship POCOs are no longer "
        + "in the CP model graph) but the TenantId COLUMN on tenant-"
        + "resident tables stays during the shared-DB transitional phase. "
        + "Production today routes most tenants through "
        + "StubTenantConnectionResolver onto a shared central Postgres "
        + "(see CLAUDE.md 'Routing (current state)'). The TenantId "
        + "predicate in tenant repositories is the only isolation plane "
        + "while the shared-DB topology is in play. Re-enable when "
        + "every tenant has a dedicated physical DB (Epic 28 cutover, "
        + "or when Epic 30's pluggable infra backends remove the "
        + "shared-DB seam entirely).")]
    public void Tenant_Resident_Entities_Have_No_TenantId_Column()
    {
        using var ctx = CreateContext();

        // Sample three representative tables — each must NOT carry a
        // TenantId column on the tenant DB. Discriminator is implicit
        // (one DB == one tenant — Doc 01 §1.4).
        var agent = ctx.Model.FindEntityType(typeof(AgentConfig))!;
        agent.GetProperties().Select(p => p.Name).Should().NotContain("TenantId");

        var events = ctx.Model.FindEntityType(typeof(Tamma.Data.Entities.DomainEvent))!;
        events.GetProperties().Select(p => p.Name).Should().NotContain("TenantId");

        var queued = ctx.Model.FindEntityType(typeof(QueuedTask))!;
        queued.GetProperties().Select(p => p.Name).Should().NotContain("TenantId");
    }

    [Test]
    public void ApiKey_On_TenantDb_Has_TenantScope_Check_Constraint()
    {
        using var ctx = CreateContext();

        // CHECK constraints are pruned from the runtime read-optimized
        // model. Verify via the SQL the migration generator would emit
        // (Database.GenerateCreateScript is the same path).
        var sql = ctx.Database.GenerateCreateScript();

        sql.Should().Contain("ck_api_keys_tenant_scope",
            because: "Doc 01 §1.4 — tenant DB api_keys must be locked to Scope='tenant' via CHECK constraint");
        sql.Should().Contain("\"Scope\" = 'tenant'");
    }

    [Test]
    public void Mentorship_Tables_Are_Owned_By_TenantDb()
    {
        using var ctx = CreateContext();

        // Doc 01 §1.2 rows 27–30 — mentorship is pure tenant data.
        ctx.Model.FindEntityType(typeof(Tamma.Core.Entities.MentorshipSession))
            .Should().NotBeNull();
        ctx.Model.FindEntityType(typeof(Tamma.Core.Entities.MentorshipEvent))
            .Should().NotBeNull();
        ctx.Model.FindEntityType(typeof(Tamma.Core.Entities.JuniorDeveloper))
            .Should().NotBeNull();
        ctx.Model.FindEntityType(typeof(Tamma.Core.Entities.Story))
            .Should().NotBeNull();
    }

    [Test]
    public void TenantDb_Does_Not_Carry_ControlPlane_Entities()
    {
        using var ctx = CreateContext();

        var tableNames = ctx.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToHashSet();

        // CP-only tables must not appear here.
        tableNames.Should().NotContain("users");
        tableNames.Should().NotContain("tenants");
        tableNames.Should().NotContain("tenant_memberships");
        tableNames.Should().NotContain("plans");
        tableNames.Should().NotContain("platform_events");
        tableNames.Should().NotContain("platform_email_outbox");
        tableNames.Should().NotContain("platform_queued_tasks");
        tableNames.Should().NotContain("github_installations");
    }
}
