using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-17 (T3/AC5) — the custom prompt resolution order
/// <c>byRoleAction["role:action"] → system → ERROR</c>, fail-loud, never
/// empty/plain. Drives <see cref="CustomAgentPromptResolver"/> over a fake
/// <see cref="IAgentRepository"/> that returns a canned active version.
/// </summary>
[TestFixture]
public class CustomAgentPromptResolverTests
{
    private static Agent PrivateAgent(Guid id) => new()
    {
        Id = id,
        Name = "atlas",
        Visibility = AgentVisibility.Private,
        OwnerTenantId = Guid.NewGuid(),
    };

    private static CustomAgentPromptResolver Resolver(string configJson)
        => new(new FakeAgents(configJson), NullLogger<CustomAgentPromptResolver>.Instance);

    [Test]
    public async Task ByRoleAction_Wins_Over_System()
    {
        var cfg = """
            {
              "prompts": {
                "system": "SYSTEM FALLBACK",
                "byRoleAction": { "developer:implement-feature": "ROLE-ACTION PROMPT" }
              }
            }
            """;
        var agent = PrivateAgent(Guid.NewGuid());

        var text = await Resolver(cfg).ResolveAsync(agent, "developer", "implement-feature");

        text.Should().Be("ROLE-ACTION PROMPT");
    }

    [Test]
    public async Task System_Used_When_NoMatchingRoleAction()
    {
        var cfg = """
            {
              "prompts": {
                "system": "SYSTEM FALLBACK",
                "byRoleAction": { "developer:implement-feature": "ROLE-ACTION PROMPT" }
              }
            }
            """;
        var agent = PrivateAgent(Guid.NewGuid());

        // role:action present in block but the REQUESTED (tester, write-tests)
        // has no entry → fall to system.
        var text = await Resolver(cfg).ResolveAsync(agent, "tester", "write-tests");

        text.Should().Be("SYSTEM FALLBACK");
    }

    [Test]
    public async Task System_Used_When_ActionNull_AndNoRoleActionApplies()
    {
        var cfg = """{ "prompts": { "system": "SYSTEM FALLBACK" } }""";
        var agent = PrivateAgent(Guid.NewGuid());

        var text = await Resolver(cfg).ResolveAsync(agent, "developer", action: null);

        text.Should().Be("SYSTEM FALLBACK");
    }

    [Test]
    public async Task NoMatch_NoSystem_FailsLoud_NeverEmpty()
    {
        var cfg = """
            { "prompts": { "byRoleAction": { "developer:implement-feature": "X" } } }
            """;
        var agent = PrivateAgent(Guid.NewGuid());

        // Requested (tester, write-tests) has no entry and there is no system →
        // CUSTOM_PROMPT_UNRESOLVED, never an empty/plain prompt.
        Func<Task> act = async () =>
            await Resolver(cfg).ResolveAsync(agent, "tester", "write-tests");

        var ex = (await act.Should().ThrowAsync<CustomPromptUnresolvedException>()).Which;
        ex.Code.Should().Be("CUSTOM_PROMPT_UNRESOLVED");
        ex.AgentId.Should().Be(agent.Id);
        ex.RoleActionKey.Should().Be("tester:write-tests");
        // The exception message must NOT carry any template body.
        ex.Message.Should().NotContain("X");
    }

    [Test]
    public async Task EmptyPromptsBlock_FailsLoud_OnCustomPath()
    {
        // The resolver is only entered with a non-empty block by MaterialiseAsync,
        // but defends against a racing version flip: an empty/absent block on the
        // custom path is still a no-resolve, never a silent fallback.
        var agent = PrivateAgent(Guid.NewGuid());

        Func<Task> act = async () =>
            await Resolver("""{ "prompts": {} }""").ResolveAsync(agent, "developer", "implement-feature");

        await act.Should().ThrowAsync<CustomPromptUnresolvedException>();
    }

    private sealed class FakeAgents(string configJson) : IAgentRepository
    {
        public Task<AgentVersion?> GetActiveVersionAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<AgentVersion?>(new AgentVersion
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Version = 1,
                ConfigJson = configJson,
            });

        // Unused surface — throw so an accidental call is loud.
        public Task<Agent> CreateAsync(Agent agent, string firstVersionConfigJson, string? notes, Guid? createdBy, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AgentVersion?> PublishVersionAsync(Guid agentId, string configJson, string? notes, Guid? updatedBy, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Agent?> ArchiveAsync(Guid agentId, Guid? updatedBy, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AgentVersion?> SetActiveVersionAsync(Guid agentId, int version, Guid? updatedBy, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Agent?> GetByIdAsync(Guid agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentVersion?> GetVersionAsync(Guid agentId, int version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentVersion>> ListVersionsAsync(Guid agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> ListVisibleAsync(Guid? tenantId, Guid? userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Agent?> GetPublicByNameAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
