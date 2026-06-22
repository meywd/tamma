using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-17 (T3/AC5) — the custom prompt resolution order
/// <c>byRoleAction["role:action"] → system → ERROR</c>, fail-loud, never
/// empty/plain. Drives <see cref="CustomAgentPromptResolver"/> over the
/// ALREADY-LOADED <see cref="AgentPromptSet"/> the caller threads in (the
/// resolver does NO repository read — C2 fix).
/// </summary>
[TestFixture]
public class CustomAgentPromptResolverTests
{
    private static CustomAgentPromptResolver Resolver()
        => new(NullLogger<CustomAgentPromptResolver>.Instance);

    private static AgentPromptSet Set(string configJson) =>
        AgentPromptSet.TryRead(configJson)
        ?? throw new InvalidOperationException("test config had no prompts block");

    [Test]
    public async Task ByRoleAction_Wins_Over_System()
    {
        var set = Set("""
            {
              "prompts": {
                "system": "SYSTEM FALLBACK",
                "byRoleAction": { "developer:implement-feature": "ROLE-ACTION PROMPT" }
              }
            }
            """);

        var text = await Resolver().ResolveAsync(Guid.NewGuid(), set, "developer", "implement-feature");

        text.Should().Be("ROLE-ACTION PROMPT");
    }

    [Test]
    public async Task System_Used_When_NoMatchingRoleAction()
    {
        var set = Set("""
            {
              "prompts": {
                "system": "SYSTEM FALLBACK",
                "byRoleAction": { "developer:implement-feature": "ROLE-ACTION PROMPT" }
              }
            }
            """);

        // role:action present in block but the REQUESTED (tester, write-tests)
        // has no entry → fall to system.
        var text = await Resolver().ResolveAsync(Guid.NewGuid(), set, "tester", "write-tests");

        text.Should().Be("SYSTEM FALLBACK");
    }

    [Test]
    public async Task System_Used_When_ActionNull_AndNoRoleActionApplies()
    {
        var set = Set("""{ "prompts": { "system": "SYSTEM FALLBACK" } }""");

        var text = await Resolver().ResolveAsync(Guid.NewGuid(), set, "developer", action: null);

        text.Should().Be("SYSTEM FALLBACK");
    }

    [Test]
    public async Task NoMatch_NoSystem_FailsLoud_NeverEmpty()
    {
        var set = Set("""
            { "prompts": { "byRoleAction": { "developer:implement-feature": "X" } } }
            """);
        var agentId = Guid.NewGuid();

        // Requested (tester, write-tests) has no entry and there is no system →
        // CUSTOM_PROMPT_UNRESOLVED, never an empty/plain prompt.
        Func<Task> act = async () =>
            await Resolver().ResolveAsync(agentId, set, "tester", "write-tests");

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("CUSTOM_PROMPT_UNRESOLVED");
        ex.Context["agentId"].Should().Be(agentId);
        ex.Context["roleActionKey"].Should().Be("tester:write-tests");
        ex.Severity.Should().Be(TammaErrorSeverity.High);
        ex.Retryable.Should().BeFalse();
        // The exception message must NOT carry any template body.
        ex.Message.Should().NotContain("X");
    }

    [Test]
    public async Task EmptyPromptsBlock_FailsLoud_OnCustomPath()
    {
        // The resolver is only entered with a non-empty block by MaterialiseAsync,
        // but defends against a racing version flip: an empty/absent block on the
        // custom path is still a no-resolve, never a silent fallback.
        var set = Set("""{ "prompts": {} }""");

        Func<Task> act = async () =>
            await Resolver().ResolveAsync(Guid.NewGuid(), set, "developer", "implement-feature");

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("CUSTOM_PROMPT_UNRESOLVED");
    }

    // ── C1 — alias/non-canonical byRoleAction key round-trips ──────────────────

    [Test]
    public async Task LegacyAliasKey_RoundTrips_ToCanonicalLookup()
    {
        // STORED with a legacy ROLE alias ("implementer" → "developer"); the
        // resolver looks up the canonical (developer:implement-feature). Before the
        // C1 fix this silently MISSED (ordinal "developer:implement-feature" !=
        // stored "implementer:implement-feature") and fell to system/ERROR.
        var set = Set("""
            { "prompts": { "byRoleAction": { "implementer:implement-feature": "ALIAS PROMPT" } } }
            """);

        var text = await Resolver().ResolveAsync(
            Guid.NewGuid(), set, "developer", "implement-feature");

        text.Should().Be("ALIAS PROMPT",
            "an aliased stored key must canonicalize so the canonical lookup hits it");
    }

    [Test]
    public async Task LegacyActionAliasKey_RoundTrips_ToCanonicalLookup()
    {
        // STORED with a legacy ACTION/phase alias AND a role alias
        // ("implementer:CODE_GENERATION" → "developer:implement-feature").
        var set = Set("""
            { "prompts": { "byRoleAction": { "implementer:CODE_GENERATION": "LEGACY CELL PROMPT" } } }
            """);

        var text = await Resolver().ResolveAsync(
            Guid.NewGuid(), set, "developer", "implement-feature");

        text.Should().Be("LEGACY CELL PROMPT",
            "a legacy role+action alias key must canonicalize for the canonical lookup");
    }

    [Test]
    public void Canonicalization_Stores_Under_CanonicalKey()
    {
        // The parsed set exposes the canonical key directly (store-side proof).
        var set = Set("""
            { "prompts": { "byRoleAction": { "implementer:CODE_GENERATION": "P" } } }
            """);

        set.ByRoleAction.Should().ContainKey("developer:implement-feature");
        set.ByRoleAction.Should().NotContainKey("implementer:CODE_GENERATION");
    }

    [Test]
    public void Canonicalization_UnparseableKey_PreservedVerbatim_ForValidator()
    {
        // A key that does NOT parse (no colon / unknown token) is kept raw so the
        // write-time validator can reject it (PROMPTS_INVALID_KEY); TryRead never
        // throws and never drops it.
        var set = Set("""
            { "prompts": { "byRoleAction": { "bogus-no-colon": "P", "wizard:nope": "Q" } } }
            """);

        set.ByRoleAction.Should().ContainKey("bogus-no-colon");
        set.ByRoleAction.Should().ContainKey("wizard:nope");
    }
}
