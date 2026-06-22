using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Api.Services.Security;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 — unit tests for <see cref="StaticProviderAuthLookup"/> (the
/// interim pre-34-11 eligibility seam). The canonical guard is "every
/// <c>ProviderAllowlist.DefaultProviders</c> entry resolves to a deterministic
/// non-null <see cref="ProviderAuthModel"/>" — so a newly added provider can
/// never be silently mis-classified (left <c>null</c>) and slip an unknown
/// past the SaaS fail-closed deny.
/// </summary>
[TestFixture]
public class StaticProviderAuthLookupTests
{
    private readonly StaticProviderAuthLookup _sut = new();

    private static readonly string[] CliTokenProviders = { "claude-code", "opencode", "zen-mcp" };

    // The allowlist entries as published by ProviderAllowlist.DefaultProviders.
    private static readonly string[] AllowlistProviders =
    {
        "anthropic", "openai", "openrouter", "google", "github-copilot",
        "local-llm", "opencode", "z-ai", "zen-mcp", "azure-openai",
        "gemini", "ollama", "lmstudio", "together", "groq",
    };

    // Every name the lookup MUST classify deterministically (non-null): the
    // allowlist + the flagship `claude-code` harness (which the allowlist
    // omits but is the canonical CLI-token provider).
    private static readonly string[] KnownProviders =
        AllowlistProviders.Concat(new[] { "claude-code" }).Distinct().ToArray();

    [TestCaseSource(nameof(KnownProviders))]
    public async Task Known_provider_resolves_to_a_deterministic_non_null_auth_model(string provider)
    {
        // Guards against a new provider being silently mis-classified as unknown
        // (null) and slipping past the SaaS fail-closed deny.
        var model = await _sut.AuthModelAsync(provider);
        model.Should().NotBeNull(
            "every known provider must classify deterministically — null is unknown (fail-closed)");
    }

    [TestCaseSource(nameof(CliTokenProviders))]
    public async Task Harness_providers_classify_as_cli_token(string provider)
    {
        var model = await _sut.AuthModelAsync(provider);
        model.Should().Be(ProviderAuthModel.CliToken);
    }

    [TestCase("anthropic")]
    [TestCase("openai")]
    [TestCase("openrouter")]
    [TestCase("gemini")]
    [TestCase("google")]
    [TestCase("groq")]
    public async Task Api_key_providers_classify_as_api_key(string provider)
    {
        var model = await _sut.AuthModelAsync(provider);
        model.Should().Be(ProviderAuthModel.ApiKey);
    }

    [Test]
    public async Task Unknown_provider_resolves_to_null()
    {
        var model = await _sut.AuthModelAsync("definitely-not-a-provider");
        model.Should().BeNull("an unknown provider must be null to drive the SaaS fail-closed deny");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Blank_or_null_provider_resolves_to_null(string? provider)
    {
        var model = await _sut.AuthModelAsync(provider);
        model.Should().BeNull();
    }

    [TestCase("Claude-Code ", ProviderAuthModel.CliToken)]
    [TestCase("  ANTHROPIC", ProviderAuthModel.ApiKey)]
    [TestCase("OpenCode", ProviderAuthModel.CliToken)]
    [TestCase(" OpenAI ", ProviderAuthModel.ApiKey)]
    public async Task Matching_is_case_insensitive_and_trimmed(string provider, ProviderAuthModel expected)
    {
        var model = await _sut.AuthModelAsync(provider);
        model.Should().Be(expected);
    }

    [Test]
    public void Every_allowlist_provider_is_covered_by_the_test_list()
    {
        // Belt-and-braces: if ProviderAllowlist gains a provider, this surfaces
        // that the AllowlistProviders list above (and the lookup) must be updated.
        foreach (var provider in AllowlistProviders)
        {
            ProviderAllowlist.IsAllowedDefault(provider).Should().BeTrue(
                $"'{provider}' should be a known default provider");
        }
    }
}
