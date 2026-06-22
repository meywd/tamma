using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-17 (T2/AC2, AC3) — the public-must-be-empty invariant and the
/// <c>prompts</c> content rules layered onto the 32-1
/// <see cref="AgentConfigValidator"/> via the new
/// <c>Validate(configJson, visibility)</c> overload. The same overload backs
/// BOTH the create and publish-version write paths (AC2 applies to both).
/// </summary>
[TestFixture]
public class AgentConfigValidatorPromptsTests
{
    private static (bool Valid, string[] Errors) Validate(string json, AgentVisibility visibility)
        => AgentConfigValidator.Validate(json, visibility);

    private const string PopulatedSystem =
        """{ "provider": "anthropic", "prompts": { "system": "House prompt." } }""";

    private const string PopulatedByRoleAction =
        """{ "prompts": { "byRoleAction": { "developer:implement-feature": "Do it." } } }""";

    // ── AC2: public must be prompt-free ──

    [Test]
    public void Public_WithSystemPrompt_Rejected_PromptsNotAllowed()
    {
        var (valid, errors) = Validate(PopulatedSystem, AgentVisibility.Public);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_NOT_ALLOWED_ON_PUBLIC*");
    }

    [Test]
    public void Public_WithByRoleActionPrompt_Rejected_PromptsNotAllowed()
    {
        var (valid, errors) = Validate(PopulatedByRoleAction, AgentVisibility.Public);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_NOT_ALLOWED_ON_PUBLIC*");
    }

    [Test]
    public void Public_WithoutPrompts_Accepted()
    {
        var (valid, _) = Validate(
            """{ "provider": "anthropic", "model": "claude-sonnet-4" }""",
            AgentVisibility.Public);
        valid.Should().BeTrue();
    }

    [Test]
    public void Public_WithEmptyPromptsObject_Accepted()
    {
        var (valid, _) = Validate("""{ "prompts": {} }""", AgentVisibility.Public);
        valid.Should().BeTrue("an empty prompts block is treated as absent");
    }

    [Test]
    public void Public_WithWhitespaceSystem_Accepted()
    {
        var (valid, _) = Validate(
            """{ "prompts": { "system": "   " } }""", AgentVisibility.Public);
        valid.Should().BeTrue("a wholly-empty/whitespace prompts block does not count as populated");
    }

    // ── AC2: private may carry prompts ──

    [Test]
    public void Private_WithSystemPrompt_Accepted()
    {
        var (valid, errors) = Validate(PopulatedSystem, AgentVisibility.Private);
        valid.Should().BeTrue(string.Join("; ", errors));
    }

    [Test]
    public void Private_WithByRoleActionPrompt_Accepted()
    {
        var (valid, errors) = Validate(PopulatedByRoleAction, AgentVisibility.Private);
        valid.Should().BeTrue(string.Join("; ", errors));
    }

    [Test]
    public void Private_WithoutPrompts_Accepted()
    {
        var (valid, _) = Validate(
            """{ "provider": "anthropic", "model": "claude-sonnet-4" }""",
            AgentVisibility.Private);
        valid.Should().BeTrue();
    }

    // ── AC3: prompts content validation (private) ──

    [Test]
    public void Private_InvalidKey_MissingColon_Rejected_InvalidKey()
    {
        var json = """{ "prompts": { "byRoleAction": { "developerimplementfeature": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_INVALID_KEY*");
    }

    [Test]
    public void Private_InvalidKey_UnknownRole_Rejected_InvalidKey()
    {
        var json = """{ "prompts": { "byRoleAction": { "wizard:implement-feature": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_INVALID_KEY*");
    }

    [Test]
    public void Private_InvalidKey_UnknownAction_Rejected_InvalidKey()
    {
        var json = """{ "prompts": { "byRoleAction": { "developer:nope": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_INVALID_KEY*");
    }

    [Test]
    public void Private_IneligibleRoleAction_Rejected_InvalidKey()
    {
        // developer:deploy is a known role + known action but not a valid cell.
        var json = """{ "prompts": { "byRoleAction": { "developer:deploy": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_INVALID_KEY*");
    }

    [Test]
    public void Private_EmptyTemplateValue_Rejected_EmptyTemplate()
    {
        var json = """{ "prompts": { "byRoleAction": { "developer:implement-feature": "   " } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_EMPTY_TEMPLATE*");
    }

    [Test]
    public void Private_ProtoPollutionKey_Rejected_ProtoPollution()
    {
        var json = """{ "prompts": { "byRoleAction": { "__proto__": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_PROTO_POLLUTION*");
    }

    [Test]
    public void Private_ProtoPollution_Constructor_Rejected()
    {
        var json = """{ "prompts": { "byRoleAction": { "constructor": "x" } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*PROMPTS_PROTO_POLLUTION*");
    }

    [Test]
    public void Private_ValidLegacyRoleAlias_Accepted_And_Canonicalized()
    {
        // "implementer" is a legacy alias for "developer". C1 — the key is now
        // CANONICALIZED at read time so STORE and the resolver's LOOKUP agree:
        // it validates AND the parsed set exposes the canonical key, not the alias.
        var json = """{ "prompts": { "byRoleAction": { "implementer:implement-feature": "Go." } } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeTrue(string.Join("; ", errors));

        // The canonicalization is what makes this safe: a key written with an alias
        // is stored under the canonical wire form the resolver looks up.
        var set = AgentPromptSet.TryRead(json)!;
        set.ByRoleAction.Should().ContainKey("developer:implement-feature");
        set.ByRoleAction.Should().NotContainKey("implementer:implement-feature");
    }

    // ── the 32-1 shape rules still apply through the new overload ──

    [Test]
    public void NewOverload_StillEnforces_BaseShapeRules()
    {
        // temperature out of range must still reject through the visibility overload.
        var json = """{ "temperature": 9, "prompts": { "system": "ok" } }""";
        var (valid, errors) = Validate(json, AgentVisibility.Private);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*temperature*");
    }

    [Test]
    public void NewOverload_EmptyConfig_Valid_ForBothVisibilities()
    {
        Validate("{}", AgentVisibility.Public).Valid.Should().BeTrue();
        Validate("{}", AgentVisibility.Private).Valid.Should().BeTrue();
    }

    // ── AC7 — no template body leaks into validation error messages ──

    [Test]
    public void RejectionErrors_NeverEcho_TemplateBody()
    {
        const string body = "SUPER SECRET ACME INSTRUCTIONS";
        // Public + populated (rejected) AND an empty-template entry (rejected):
        // the messages must reference keys/codes only, never the template value.
        var json = $$"""
            {
              "prompts": {
                "system": "{{body}}",
                "byRoleAction": { "developer:implement-feature": "{{body}}", "bad:key": "{{body}}" }
              }
            }
            """;
        var (valid, errors) = Validate(json, AgentVisibility.Public);
        valid.Should().BeFalse();
        string.Join("\n", errors).Should().NotContain(body,
            "validation errors carry only the source label + key, never a prompt body");
    }
}
