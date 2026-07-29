using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 41-1a AC5 (D3) — the scrum_master alias removal is a controlled behaviour
/// change with a proven migration. Removing the <c>scrum_master → product_owner</c>
/// entry from <see cref="RolePhaseMap.LegacyRoleAliases"/> means the name now
/// resolves to the first-class <see cref="AgentRole.ScrumMaster"/> everywhere:
/// a stored agent config keyed <c>scrum_master</c> still VALIDATES
/// (AgentConfigValidator's role-known check moves from the alias clause to the
/// ValidRoles clause), still RESOLVES to a provider chain (via the new
/// DefaultAgentConfig row), and resolves to the NEW role — not product_owner.
/// No data migration runs: the JSONB key text is unchanged, only its
/// interpretation (CLAUDE.md, "No migration anxiety").
/// </summary>
[TestFixture]
public class AgentAliasMigrationTests
{
    private Mock<IAgentConfigRepository> _repoMock = null!;
    private AgentResolverService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repoMock = new Mock<IAgentConfigRepository>(MockBehavior.Strict);
        _service = new AgentResolverService(
            _repoMock.Object,
            NullLogger<AgentResolverService>.Instance);
    }

    [Test]
    public void StoredConfig_KeyedScrumMaster_StillValidates()
    {
        // AgentConfigValidator.cs — the role-known check accepts a property name
        // that is EITHER in ValidRoles OR in LegacyRoleAliases; scrum_master moved
        // from the second clause to the first, so a stored config still validates.
        var (valid, errors) = AgentConfigValidator.Validate(
            """{"roles": {"scrum_master": {"provider": "openai", "model": "gpt-4o"}}}""");

        valid.Should().BeTrue(
            "a stored agent config keyed scrum_master must survive the alias removal; errors: " +
            string.Join("; ", errors));
    }

    [Test]
    public async Task ScrumMaster_ResolvesToItsOwnRole_NotProductOwner()
    {
        _repoMock.Setup(r => r.GetTenantConfigAsync(null))
            .ReturnsAsync((JsonDocument?)null);

        var resolved = await _service.ResolveAsync(null, "scrum_master");

        // D3: the name finally means what it says — before 41-1a this resolved to
        // product_owner (Role "product_owner", Handle "tamma-product-owner").
        resolved.Role.Should().Be("scrum_master");
        resolved.Handle.Should().Be("tamma-scrum-master");
        resolved.Source.Should().Be("platform-default");
    }

    [Test]
    public async Task ScrumMaster_TenantOverride_KeyedScrumMaster_StillApplies()
    {
        // The raw-dictionary read path (TryGetRoleOverride): a tenant override
        // keyed scrum_master is found via the canonical-key branch now, not the
        // alias walk — same JSONB, new interpretation, no data migration.
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse(
            """{"roles": {"scrum_master": {"provider": "openai", "model": "gpt-4o"}}}""");
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId)).ReturnsAsync(json);

        var resolved = await _service.ResolveAsync(tenantId, "scrum_master");

        resolved.Role.Should().Be("scrum_master");
        resolved.Provider.Should().Be("openai");
        resolved.Model.Should().Be("gpt-4o");
    }

    [Test]
    public async Task Analyst_Control_StillResolvesToProductOwner()
    {
        // Control case: analyst (and researcher) stay aliased to product_owner —
        // only scrum_master was promoted.
        _repoMock.Setup(r => r.GetTenantConfigAsync(null))
            .ReturnsAsync((JsonDocument?)null);

        var resolved = await _service.ResolveAsync(null, "analyst");

        resolved.Role.Should().Be("product_owner");
        resolved.Handle.Should().Be("tamma-product-owner");
    }
}
