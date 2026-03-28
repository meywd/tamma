using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.Security;

[TestFixture]
public class ProviderAllowlistTests
{
    // =====================================================================
    // Known providers
    // =====================================================================

    [Test]
    public void IsAllowed_KnownProvider_ReturnsTrue()
    {
        var allowlist = new ProviderAllowlist();
        allowlist.IsAllowed("anthropic").Should().BeTrue();
        allowlist.IsAllowed("openai").Should().BeTrue();
        allowlist.IsAllowed("openrouter").Should().BeTrue();
        allowlist.IsAllowed("google").Should().BeTrue();
        allowlist.IsAllowed("github-copilot").Should().BeTrue();
        allowlist.IsAllowed("local-llm").Should().BeTrue();
        allowlist.IsAllowed("opencode").Should().BeTrue();
        allowlist.IsAllowed("z-ai").Should().BeTrue();
        allowlist.IsAllowed("zen-mcp").Should().BeTrue();
        allowlist.IsAllowed("azure-openai").Should().BeTrue();
        allowlist.IsAllowed("gemini").Should().BeTrue();
        allowlist.IsAllowed("ollama").Should().BeTrue();
        allowlist.IsAllowed("lmstudio").Should().BeTrue();
        allowlist.IsAllowed("together").Should().BeTrue();
        allowlist.IsAllowed("groq").Should().BeTrue();
    }

    // =====================================================================
    // Unknown providers
    // =====================================================================

    [Test]
    public void IsAllowed_UnknownProvider_ReturnsFalse()
    {
        var allowlist = new ProviderAllowlist();
        allowlist.IsAllowed("evil-provider").Should().BeFalse();
        allowlist.IsAllowed("http://attacker.com").Should().BeFalse();
        allowlist.IsAllowed("../../../etc/passwd").Should().BeFalse();
    }

    // =====================================================================
    // Case insensitivity
    // =====================================================================

    [Test]
    public void IsAllowed_CaseInsensitive()
    {
        var allowlist = new ProviderAllowlist();
        allowlist.IsAllowed("ANTHROPIC").Should().BeTrue();
        allowlist.IsAllowed("Anthropic").Should().BeTrue();
        allowlist.IsAllowed("aNtHrOpIc").Should().BeTrue();
        allowlist.IsAllowed("OPENAI").Should().BeTrue();
        allowlist.IsAllowed("OpenRouter").Should().BeTrue();
    }

    // =====================================================================
    // Empty / null / whitespace
    // =====================================================================

    [Test]
    public void IsAllowed_EmptyName_ReturnsFalse()
    {
        var allowlist = new ProviderAllowlist();
        allowlist.IsAllowed("").Should().BeFalse();
        allowlist.IsAllowed("  ").Should().BeFalse();
        allowlist.IsAllowed(null).Should().BeFalse();
    }

    // =====================================================================
    // Whitespace trimming
    // =====================================================================

    [Test]
    public void IsAllowed_TrimsWhitespace()
    {
        var allowlist = new ProviderAllowlist();
        allowlist.IsAllowed("  anthropic  ").Should().BeTrue();
        allowlist.IsAllowed("\tanthropic\t").Should().BeTrue();
    }

    // =====================================================================
    // Additional providers from config
    // =====================================================================

    [Test]
    public void IsAllowed_AdditionalProvidersFromConfig()
    {
        var options = Options.Create(new ProviderAllowlistOptions
        {
            AdditionalProviders = new List<string> { "my-custom-llm", "internal-provider" }
        });
        var allowlist = new ProviderAllowlist(options);

        allowlist.IsAllowed("my-custom-llm").Should().BeTrue();
        allowlist.IsAllowed("internal-provider").Should().BeTrue();
        // Default providers still allowed
        allowlist.IsAllowed("anthropic").Should().BeTrue();
        allowlist.IsAllowed("openai").Should().BeTrue();
    }

    [Test]
    public void IsAllowed_AdditionalProvidersAreCaseInsensitive()
    {
        var options = Options.Create(new ProviderAllowlistOptions
        {
            AdditionalProviders = new List<string> { "My-Custom-LLM" }
        });
        var allowlist = new ProviderAllowlist(options);

        allowlist.IsAllowed("my-custom-llm").Should().BeTrue();
        allowlist.IsAllowed("MY-CUSTOM-LLM").Should().BeTrue();
    }

    [Test]
    public void IsAllowed_AdditionalProviders_IgnoresEmptyEntries()
    {
        var options = Options.Create(new ProviderAllowlistOptions
        {
            AdditionalProviders = new List<string> { "", "  ", "valid-provider" }
        });
        var allowlist = new ProviderAllowlist(options);

        allowlist.IsAllowed("valid-provider").Should().BeTrue();
        allowlist.IsAllowed("").Should().BeFalse();
    }

    // =====================================================================
    // FilterAllowed
    // =====================================================================

    [Test]
    public void FilterAllowed_MixedValidInvalid_FiltersCorrectly()
    {
        var allowlist = new ProviderAllowlist();
        var chain = new List<string> { "anthropic", "evil-provider", "openai", "bad-actor" };

        var filtered = allowlist.FilterAllowed(chain);

        filtered.Should().HaveCount(2);
        filtered.Should().Contain("anthropic");
        filtered.Should().Contain("openai");
        filtered.Should().NotContain("evil-provider");
        filtered.Should().NotContain("bad-actor");
    }

    [Test]
    public void FilterAllowed_PreservesOrder()
    {
        var allowlist = new ProviderAllowlist();
        var chain = new List<string> { "openai", "anthropic", "openrouter" };

        var filtered = allowlist.FilterAllowed(chain);

        filtered.Should().HaveCount(3);
        filtered[0].Should().Be("openai");
        filtered[1].Should().Be("anthropic");
        filtered[2].Should().Be("openrouter");
    }

    [Test]
    public void FilterAllowed_AllInvalid_ReturnsEmpty()
    {
        var allowlist = new ProviderAllowlist();
        var chain = new List<string> { "evil-1", "evil-2" };

        var filtered = allowlist.FilterAllowed(chain);

        filtered.Should().BeEmpty();
    }

    [Test]
    public void FilterAllowed_EmptyInput_ReturnsEmpty()
    {
        var allowlist = new ProviderAllowlist();
        var chain = new List<string>();

        var filtered = allowlist.FilterAllowed(chain);

        filtered.Should().BeEmpty();
    }

    // =====================================================================
    // Static convenience methods
    // =====================================================================

    [Test]
    public void IsAllowedDefault_StaticMethod_Works()
    {
        ProviderAllowlist.IsAllowedDefault("anthropic").Should().BeTrue();
        ProviderAllowlist.IsAllowedDefault("openai").Should().BeTrue();
        ProviderAllowlist.IsAllowedDefault("evil").Should().BeFalse();
        ProviderAllowlist.IsAllowedDefault(null).Should().BeFalse();
    }

    [Test]
    public void FilterAllowedDefault_StaticMethod_Works()
    {
        var chain = new List<string> { "anthropic", "evil", "openai" };
        var filtered = ProviderAllowlist.FilterAllowedDefault(chain);

        filtered.Should().HaveCount(2);
        filtered.Should().Contain("anthropic");
        filtered.Should().Contain("openai");
    }

    // =====================================================================
    // GetAllowedProviders
    // =====================================================================

    [Test]
    public void GetAllowedProviders_ReturnsAllDefault()
    {
        var allowlist = new ProviderAllowlist();
        var providers = allowlist.GetAllowedProviders();

        providers.Should().Contain("anthropic");
        providers.Should().Contain("openai");
        providers.Should().Contain("openrouter");
        providers.Count.Should().BeGreaterOrEqualTo(9);
    }

    [Test]
    public void GetAllowedProviders_IncludesAdditional()
    {
        var options = Options.Create(new ProviderAllowlistOptions
        {
            AdditionalProviders = new List<string> { "custom-provider" }
        });
        var allowlist = new ProviderAllowlist(options);
        var providers = allowlist.GetAllowedProviders();

        providers.Should().Contain("custom-provider");
        providers.Should().Contain("anthropic");
    }

    // =====================================================================
    // No options constructor
    // =====================================================================

    [Test]
    public void Constructor_WithoutOptions_UsesDefaults()
    {
        var allowlist = new ProviderAllowlist();

        allowlist.IsAllowed("anthropic").Should().BeTrue();
        allowlist.IsAllowed("evil").Should().BeFalse();
    }
}
