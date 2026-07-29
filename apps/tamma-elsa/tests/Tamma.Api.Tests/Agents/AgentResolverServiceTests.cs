using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentResolverService"/>. Repository is mocked so
/// these tests are pure unit tests (no Postgres required).
/// </summary>
[TestFixture]
public class AgentResolverServiceTests
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

    // -----------------------------------------------------------------------
    // Platform defaults only (no tenant override)
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveAsync_NullTenant_Returns_Platform_Default_For_Developer()
    {
        _repoMock.Setup(r => r.GetTenantConfigAsync(null))
            .ReturnsAsync((JsonDocument?)null);

        var result = await _service.ResolveAsync(null, "developer");

        result.Should().NotBeNull();
        result.Role.Should().Be("developer");
        result.Provider.Should().NotBeNullOrEmpty();
        result.Model.Should().NotBeNullOrEmpty();
        result.Source.Should().Be("platform-default");
        result.Handle.Should().NotBeNullOrEmpty();
        result.MaxTokens.Should().BeGreaterThan(0);
        result.TokenBudget.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ResolveAsync_TenantWithNoOverride_Returns_Platform_Default()
    {
        var tenantId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync((JsonDocument?)null);

        var result = await _service.ResolveAsync(tenantId, "tester");

        result.Role.Should().Be("tester");
        result.Source.Should().Be("platform-default");
        result.Provider.Should().NotBeNullOrEmpty();
        result.Model.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ResolveAsync_All_Valid_Roles_Have_Platform_Default()
    {
        _repoMock.Setup(r => r.GetTenantConfigAsync(It.IsAny<Guid?>()))
            .ReturnsAsync((JsonDocument?)null);

        foreach (var role in RolePhaseMap.ValidRoles)
        {
            var result = await _service.ResolveAsync(null, role);
            result.Role.Should().Be(role);
            result.Provider.Should().NotBeNullOrEmpty($"role {role} should have a default provider");
            result.Model.Should().NotBeNullOrEmpty($"role {role} should have a default model");
        }
    }

    // -----------------------------------------------------------------------
    // Tenant overrides
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveAsync_TenantOverride_Fully_Replaces_Provider_And_Model()
    {
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "developer": {
                  "provider": "openai",
                  "model": "gpt-4o",
                  "temperature": 0.5,
                  "maxTokens": 8192,
                  "tokenBudget": 20000
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        var result = await _service.ResolveAsync(tenantId, "developer");

        result.Provider.Should().Be("openai");
        result.Model.Should().Be("gpt-4o");
        result.Temperature.Should().BeApproximately(0.5, 0.001);
        result.MaxTokens.Should().Be(8192);
        result.TokenBudget.Should().Be(20000);
        result.Source.Should().Be("tenant-override");
    }

    [Test]
    public async Task ResolveAsync_PartialTenantOverride_Merges_With_Default()
    {
        var tenantId = Guid.NewGuid();
        // Only override model — provider/temperature/budget stay from default
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "developer": {
                  "model": "claude-opus-4-1"
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        var defaultConfig = DefaultAgentConfig.ForRole("developer");

        var result = await _service.ResolveAsync(tenantId, "developer");

        result.Model.Should().Be("claude-opus-4-1"); // overridden
        result.Provider.Should().Be(defaultConfig.Provider); // from default
        result.Temperature.Should().BeApproximately(defaultConfig.Temperature, 0.001);
        result.Source.Should().Be("tenant-override");
    }

    [Test]
    public async Task ResolveAsync_TenantOverride_For_Different_Role_Does_Not_Affect_Developer()
    {
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "tester": {
                  "provider": "openai",
                  "model": "gpt-4o-mini"
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        var defaultDev = DefaultAgentConfig.ForRole("developer");
        var result = await _service.ResolveAsync(tenantId, "developer");

        result.Provider.Should().Be(defaultDev.Provider);
        result.Model.Should().Be(defaultDev.Model);
        result.Source.Should().Be("platform-default");
    }

    [Test]
    public async Task ResolveAsync_TenantOverride_Of_Tools_List_Replaces_Default()
    {
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "developer": {
                  "tools": ["Read", "Write", "Bash"]
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        var result = await _service.ResolveAsync(tenantId, "developer");

        result.Tools.Should().BeEquivalentTo(new[] { "Read", "Write", "Bash" });
    }

    [Test]
    public async Task ResolveAsync_TenantOverride_With_SystemPrompt_Propagates()
    {
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "developer": {
                  "systemPrompt": "Custom tenant-specific system prompt."
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        var result = await _service.ResolveAsync(tenantId, "developer");

        result.SystemPrompt.Should().Be("Custom tenant-specific system prompt.");
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveAsync_UnknownRole_Throws()
    {
        Func<Task> act = async () => await _service.ResolveAsync(null, "nonexistent_role");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ResolveAsync_EmptyRole_Throws()
    {
        Func<Task> act = async () => await _service.ResolveAsync(null, "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    [TestCase("__proto__")]
    [TestCase("constructor")]
    [TestCase("prototype")]
    public async Task ResolveAsync_ForbiddenKey_Throws(string role)
    {
        Func<Task> act = async () => await _service.ResolveAsync(null, role);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ResolveAsync_TenantOverride_Missing_Provider_After_Merge_Throws()
    {
        // This simulates a corrupt override that sets provider to empty string.
        var tenantId = Guid.NewGuid();
        var json = JsonDocument.Parse("""
            {
              "roles": {
                "developer": {
                  "provider": ""
                }
              }
            }
            """);
        _repoMock.Setup(r => r.GetTenantConfigAsync(tenantId))
            .ReturnsAsync(json);

        Func<Task> act = async () => await _service.ResolveAsync(tenantId, "developer");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*provider*");
    }

    // -----------------------------------------------------------------------
    // ResolveForPhase
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveForPhaseAsync_ImplementFeature_Developer_Returns_Config()
    {
        _repoMock.Setup(r => r.GetTenantConfigAsync(It.IsAny<Guid?>()))
            .ReturnsAsync((JsonDocument?)null);

        var result = await _service.ResolveForPhaseAsync(null, "implement-feature", "developer");

        result.Phase.Should().Be("implement-feature");
        result.Role.Should().Be("developer");
        result.Provider.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ResolveForPhaseAsync_IneligibleRole_Throws()
    {
        Func<Task> act = async () =>
            await _service.ResolveForPhaseAsync(null, "plan-system-design", "tester");
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*tester*plan-system-design*");
    }

    [Test]
    public async Task ResolveForPhaseAsync_UnknownPhase_Throws()
    {
        Func<Task> act = async () =>
            await _service.ResolveForPhaseAsync(null, "bogus-phase", "developer");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
