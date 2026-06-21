using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;

namespace Tamma.Activities.Tests.LlmCall.Credentials;

/// <summary>
/// Story 32-3 Phase 1 — name-mapping is a single source of truth so the BYOK
/// management API and the resolver can never drift on the slug.
/// </summary>
[TestFixture]
public class ProviderCabinetNamesTests
{
    [TestCase("anthropic", "provider/anthropic/api-key")]
    [TestCase("openai", "provider/openai/api-key")]
    [TestCase("openrouter", "provider/openrouter/api-key")]
    public void Byok_MapsProviderToTenantSlug(string provider, string expected)
    {
        ProviderCabinetNames.Byok(provider).Should().Be(expected);
    }

    [TestCase("anthropic", "anthropic/api-key")]
    [TestCase("openai", "openai/api-key")]
    [TestCase("openrouter", "openrouter/api-key")]
    public void Platform_MapsProviderToPlatformSlug(string provider, string expected)
    {
        ProviderCabinetNames.Platform(provider).Should().Be(expected);
    }

    [Test]
    public void Byok_NormalizesCasingAndWhitespace()
    {
        ProviderCabinetNames.Byok("  Anthropic  ").Should().Be("provider/anthropic/api-key");
    }

    [Test]
    public void Platform_AnthropicMatchesStopgapConstant()
    {
        // The platform leg must reuse the Story 29-9 platform cabinet name.
        ProviderCabinetNames.Platform("anthropic").Should().Be("anthropic/api-key");
    }

    [Test]
    public void Byok_RejectsEmpty()
    {
        var act = () => ProviderCabinetNames.Byok("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Platform_RejectsNull()
    {
        var act = () => ProviderCabinetNames.Platform(null!);
        act.Should().Throw<ArgumentException>();
    }
}
