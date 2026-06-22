using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-17 (T1) — deserialization of the optional <c>ConfigJson.prompts</c>
/// block into <see cref="AgentPromptSet"/>. Covers the four shapes (absent,
/// full, system-only, byRoleAction-only) plus the wholly-empty object and the
/// <see cref="AgentPromptSet.IsEmpty"/> contract.
/// </summary>
[TestFixture]
public class AgentPromptSetTests
{
    [Test]
    public void Absent_PromptsKey_ReturnsNull()
    {
        var cfg = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";
        AgentPromptSet.TryRead(cfg).Should().BeNull();
    }

    [Test]
    public void FullBlock_PopulatesSystemAndByRoleAction()
    {
        var cfg = """
            {
              "provider": "anthropic",
              "prompts": {
                "system": "You are ACME's house implementer.",
                "byRoleAction": {
                  "developer:implement-feature": "Implement per ACME conventions.",
                  "senior_developer:code-review": "Review against ACME checklist."
                }
              }
            }
            """;
        var set = AgentPromptSet.TryRead(cfg);

        set.Should().NotBeNull();
        set!.System.Should().Be("You are ACME's house implementer.");
        set.IsEmpty.Should().BeFalse();
        set.ByRoleAction.Should().NotBeNull();
        set.ByRoleAction!.Should().HaveCount(2);
        set.ByRoleAction["developer:implement-feature"].Should().Be("Implement per ACME conventions.");
        set.ByRoleAction["senior_developer:code-review"].Should().Be("Review against ACME checklist.");
    }

    [Test]
    public void SystemOnly_RoundTrips_ByRoleActionNull()
    {
        var cfg = """{ "prompts": { "system": "House system prompt." } }""";
        var set = AgentPromptSet.TryRead(cfg);

        set.Should().NotBeNull();
        set!.System.Should().Be("House system prompt.");
        set.ByRoleAction.Should().BeNull();
        set.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void ByRoleActionOnly_RoundTrips_SystemNull()
    {
        var cfg = """
            { "prompts": { "byRoleAction": { "developer:implement-feature": "Do the thing." } } }
            """;
        var set = AgentPromptSet.TryRead(cfg);

        set.Should().NotBeNull();
        set!.System.Should().BeNull();
        set.ByRoleAction.Should().NotBeNull();
        set.ByRoleAction!.Should().ContainKey("developer:implement-feature");
        set.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void EmptyPromptsObject_IsEmpty_True()
    {
        var set = AgentPromptSet.TryRead("""{ "prompts": {} }""");
        set.Should().NotBeNull();
        set!.IsEmpty.Should().BeTrue("a wholly-empty prompts object is treated as absent");
    }

    [Test]
    public void WhitespaceSystem_AndEmptyMap_IsEmpty_True()
    {
        var set = AgentPromptSet.TryRead("""{ "prompts": { "system": "   ", "byRoleAction": {} } }""");
        set.Should().NotBeNull();
        set!.IsEmpty.Should().BeTrue();
    }

    [Test]
    public void PromptsNotAnObject_ReturnsNull()
    {
        AgentPromptSet.TryRead("""{ "prompts": "nope" }""").Should().BeNull();
        AgentPromptSet.TryRead("""{ "prompts": [1, 2] }""").Should().BeNull();
    }

    [Test]
    public void MalformedJson_ReturnsNull_NeverThrows()
    {
        AgentPromptSet.TryRead("{ not json").Should().BeNull();
        AgentPromptSet.TryRead(null).Should().BeNull();
        AgentPromptSet.TryRead("").Should().BeNull();
    }
}
