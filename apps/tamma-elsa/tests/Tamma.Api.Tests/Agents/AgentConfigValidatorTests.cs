using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-1 (Task 2) — unit tests for <see cref="AgentConfigValidator"/>,
/// the saved-config validator extracted from the private
/// <c>AgentEndpoints.ValidateConfigShape</c> and extended for the Epic 32
/// saved-config fields. Covers:
/// <list type="bullet">
///   <item>each new rule's accept/reject (model, temperature, maxTokens,
///     tokenBudget, tools[], systemPromptRef, rag{});</item>
///   <item>regression on the lifted legacy rules (provider regex,
///     maxBudgetUsd range, empty-chain, prototype-pollution, ReDoS).</item>
/// </list>
/// </summary>
[TestFixture]
public class AgentConfigValidatorTests
{
    private static (bool Valid, string[] Errors) Validate(string json)
        => AgentConfigValidator.Validate(json);

    // ── Empty / shape ──

    [Test]
    public void EmptyObject_IsValid()
    {
        Validate("{}").Valid.Should().BeTrue("an empty config falls through to defaults");
    }

    [Test]
    public void MalformedJson_IsRejected()
    {
        var (valid, errors) = Validate("{ not json ");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*Invalid JSON*");
    }

    [Test]
    public void NonObjectRoot_IsRejected()
    {
        Validate("[]").Valid.Should().BeFalse();
    }

    // ── New Epic 32 saved-config fields ──

    [Test]
    public void Valid_SavedConfig_AllNewFields_IsValid()
    {
        var json = """
            {
              "provider": "anthropic",
              "model": "claude-sonnet-4",
              "temperature": 0.7,
              "maxTokens": 4096,
              "tokenBudget": 100000,
              "tools": ["read_file", "write_file"],
              "systemPromptRef": "architect.system.v3",
              "rag": { "enabled": true, "topK": 5 }
            }
            """;
        Validate(json).Valid.Should().BeTrue();
    }

    [Test]
    public void TopLevelProvider_BadName_IsRejected()
    {
        var (valid, errors) = Validate("""{ "provider": "BAD NAME!" }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*provider*");
    }

    [TestCase(-0.1)]
    [TestCase(2.1)]
    public void Temperature_OutOfRange_IsRejected(double t)
    {
        var (valid, errors) = Validate($$"""{ "temperature": {{t}} }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*temperature*");
    }

    [TestCase(0.0)]
    [TestCase(1.0)]
    [TestCase(2.0)]
    public void Temperature_InRange_IsValid(double t)
    {
        Validate($$"""{ "temperature": {{t}} }""").Valid.Should().BeTrue();
    }

    [Test]
    public void Temperature_NotANumber_IsRejected()
    {
        Validate("""{ "temperature": "hot" }""").Valid.Should().BeFalse();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void MaxTokens_NotPositive_IsRejected(int v)
    {
        var (valid, errors) = Validate($$"""{ "maxTokens": {{v}} }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*maxTokens*");
    }

    [Test]
    public void MaxTokens_Positive_IsValid()
    {
        Validate("""{ "maxTokens": 1 }""").Valid.Should().BeTrue();
    }

    [Test]
    public void TokenBudget_Negative_IsRejected()
    {
        var (valid, errors) = Validate("""{ "tokenBudget": -1 }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*tokenBudget*");
    }

    [Test]
    public void TokenBudget_Zero_IsValid()
    {
        Validate("""{ "tokenBudget": 0 }""").Valid.Should().BeTrue();
    }

    [Test]
    public void Tools_NotArray_IsRejected()
    {
        var (valid, errors) = Validate("""{ "tools": "read_file" }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*tools*");
    }

    [Test]
    public void Tools_NonStringEntry_IsRejected()
    {
        var (valid, errors) = Validate("""{ "tools": ["ok", 42] }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*tools*");
    }

    [Test]
    public void Model_NotString_IsRejected()
    {
        var (valid, errors) = Validate("""{ "model": 123 }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*model*");
    }

    [Test]
    public void SystemPromptRef_NotString_IsRejected()
    {
        var (valid, errors) = Validate("""{ "systemPromptRef": [] }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*systemPromptRef*");
    }

    [Test]
    public void Rag_NotObject_IsRejected()
    {
        var (valid, errors) = Validate("""{ "rag": "yes" }""");
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*rag*");
    }

    // ── Regression: lifted legacy rules still fire ──

    [Test]
    public void Legacy_RoleProvider_BadName_IsRejected()
    {
        var json = """{ "roles": { "developer": { "provider": "BAD NAME" } } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*provider*");
    }

    [Test]
    public void Legacy_MaxBudgetUsd_OutOfRange_IsRejected()
    {
        var json = """{ "roles": { "developer": { "maxBudgetUsd": 250 } } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*maxBudgetUsd*");
    }

    [Test]
    public void Legacy_EmptyProviderChain_IsRejected()
    {
        var json = """{ "defaults": { "providerChain": [] } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*empty*");
    }

    [Test]
    public void Legacy_ForbiddenRoleKey_IsRejected()
    {
        var json = """{ "roles": { "__proto__": { "provider": "anthropic" } } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*Forbidden*");
    }

    [Test]
    public void Legacy_UnknownRole_IsRejected()
    {
        var json = """{ "roles": { "wizard": { "provider": "anthropic" } } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*Unknown role*");
    }

    [Test]
    public void Legacy_MaxFetchSizeBytes_OutOfRange_IsRejected()
    {
        var json = """{ "security": { "maxFetchSizeBytes": 9999999999 } }""";
        var (valid, errors) = Validate(json);
        valid.Should().BeFalse();
        errors.Should().ContainMatch("*maxFetchSizeBytes*");
    }
}
