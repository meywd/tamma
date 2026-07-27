using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Api.Services.Providers;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// Phase 1 of the provider abstraction — catalogue invariants
/// (.dev/findings/provider-abstraction-and-openai-compatible-candidates.md).
/// This fixture deliberately lives in Tamma.Activities.Tests because it needs
/// BOTH surfaces in view: <see cref="ProviderAllowlist"/> (Tamma.Activities)
/// and <see cref="ProviderCatalog"/> (Tamma.Api).
/// </summary>
[TestFixture]
public class ProviderCatalogTests
{
    // ── The drift guard: allowlist ⇔ catalogue exact keyset agreement ──────

    /// <summary>
    /// The finding's core defect: the allowlist and the named-client registry
    /// disagreed, leaving seven allow-listed providers uncallable. This pins
    /// EXACT keyset agreement — every allow-listed key is either an HTTP
    /// descriptor or an explicit allow-listed non-HTTP classification, and
    /// vice versa — so the surfaces can never drift apart again.
    /// </summary>
    [Test]
    public void Allowlist_And_Catalog_Are_In_Exact_Keyset_Agreement()
    {
        var allowlist = new ProviderAllowlist().GetAllowedProviders()
            .Select(p => p.ToLowerInvariant())
            .ToHashSet();

        var catalogKeys = ProviderCatalog.HttpProviders.Select(d => d.Key)
            .Concat(ProviderCatalog.NonHttpProviders.Where(n => n.Allowlisted).Select(n => n.Key))
            .Select(k => k.ToLowerInvariant())
            .ToHashSet();

        catalogKeys.Should().BeEquivalentTo(
            allowlist,
            "the allowlist and the provider catalogue (HTTP descriptors + allow-listed " +
            "non-HTTP classifications) must be in exact keyset agreement — an entry in one " +
            "but not the other is precisely the seven-unreachable-providers drift the " +
            "descriptor exists to prevent");
    }

    [Test]
    public void Every_Allowlisted_Key_Resolves_In_The_Catalog()
    {
        foreach (var key in new ProviderAllowlist().GetAllowedProviders())
        {
            var http = ProviderCatalog.Resolve(key);
            var nonHttp = ProviderCatalog.ResolveNonHttp(key);
            (http is not null || nonHttp is not null).Should().BeTrue(
                $"allow-listed provider '{key}' must resolve to an HTTP descriptor " +
                "or an explicit non-HTTP classification — anything else is an " +
                "allow-listed-but-uncallable provider");
            (http is not null && nonHttp is not null).Should().BeFalse(
                $"provider '{key}' must not be both HTTP and non-HTTP");
        }
    }

    [Test]
    public void NonAllowlisted_NonHttp_Keys_Are_Actually_Not_Allowlisted()
    {
        var allowlist = new ProviderAllowlist();
        foreach (var entry in ProviderCatalog.NonHttpProviders.Where(n => !n.Allowlisted))
        {
            allowlist.IsAllowed(entry.Key).Should().BeFalse(
                $"'{entry.Key}' is classified as a defensive (non-selectable) rejection key");
        }
    }

    [Test]
    public void Catalog_Keys_And_Aliases_Are_Distinct()
    {
        var all = ProviderCatalog.HttpProviders
            .SelectMany(d => d.Aliases.Prepend(d.Key))
            .Concat(ProviderCatalog.NonHttpProviders.SelectMany(n => n.Aliases.Prepend(n.Key)))
            .Select(k => k.ToLowerInvariant())
            .ToList();

        all.Should().OnlyHaveUniqueItems(
            "a key/alias resolving to two catalogue entries would make dispatch ambiguous");
    }

    // ── Dialect selection per descriptor ────────────────────────────────────

    [Test]
    public void Anthropic_Is_The_Only_Anthropic_Dialect_Descriptor()
    {
        ProviderCatalog.HttpProviders
            .Where(d => d.Dialect == ProviderWireDialect.Anthropic)
            .Select(d => d.Key)
            .Should().BeEquivalentTo(new[] { "anthropic" });
    }

    [TestCase("anthropic", ProviderWireDialect.Anthropic)]
    [TestCase("anthropic-claude", ProviderWireDialect.Anthropic)] // alias
    [TestCase("ANTHROPIC", ProviderWireDialect.Anthropic)]        // case-insensitive
    [TestCase("openai", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("openrouter", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("github-copilot", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("gemini", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("google", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("z-ai", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("z.ai", ProviderWireDialect.OpenAiCompatible)]      // alias
    [TestCase("deepseek", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("moonshot", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("kimi", ProviderWireDialect.OpenAiCompatible)]      // alias
    [TestCase("together", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("groq", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("azure-openai", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("ollama", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("lmstudio", ProviderWireDialect.OpenAiCompatible)]
    [TestCase("local-llm", ProviderWireDialect.OpenAiCompatible)]
    // Unknown keys keep the legacy OpenAI-compatible fallback (the else-arm of
    // the collapsed StartsWith/Equals("anthropic") branches).
    [TestCase("some-config-only-provider", ProviderWireDialect.OpenAiCompatible)]
    public void ResolveDialect_SelectsPerDescriptor(string key, ProviderWireDialect expected)
    {
        ProviderCatalog.ResolveDialect(key).Should().Be(expected);
    }

    // ── Version header is per-descriptor DATA ───────────────────────────────

    [Test]
    public void Anthropic_Descriptor_Carries_The_Published_Version_Header()
    {
        var anthropic = ProviderCatalog.Resolve("anthropic")!;
        anthropic.VersionHeaderName.Should().Be("anthropic-version");
        // 2023-06-01 is the published Anthropic API version. One of the three
        // pre-descriptor copies had drifted to 2024-01-01 (never released) —
        // per-descriptor data with a single source prevents a recurrence.
        anthropic.VersionHeaderValue.Should().Be("2023-06-01");
    }

    [Test]
    public void OpenAiCompatible_Descriptors_Carry_No_Version_Header()
    {
        foreach (var d in ProviderCatalog.HttpProviders
                     .Where(d => d.Dialect == ProviderWireDialect.OpenAiCompatible))
        {
            d.VersionHeaderName.Should().BeNull($"'{d.Key}' speaks plain OpenAI shape");
        }
    }

    // ── Endpoint paths + reconciliation specifics ───────────────────────────

    [Test]
    public void ChatPath_Defaults_Per_Dialect_And_Honours_Overrides()
    {
        ProviderCatalog.ChatPath(ProviderCatalog.Resolve("anthropic")!).Should().Be("/v1/messages");
        ProviderCatalog.ChatPath(ProviderCatalog.Resolve("openai")!).Should().Be("/v1/chat/completions");
        // Z.ai (Zhipu GLM) serves the OpenAI shape at a non-default path.
        ProviderCatalog.ChatPath(ProviderCatalog.Resolve("z-ai")!).Should().Be("/api/paas/v4/chat/completions");
        // Null descriptor (config-only provider) keeps the dialect default.
        ProviderCatalog.ChatPath(null, ProviderWireDialect.OpenAiCompatible)
            .Should().Be("/v1/chat/completions");
        ProviderCatalog.ChatPath(null, ProviderWireDialect.Anthropic)
            .Should().Be("/v1/messages");
    }

    [Test]
    public void New_Candidate_Providers_Have_Verified_Endpoints()
    {
        var deepseek = ProviderCatalog.Resolve("deepseek")!;
        deepseek.DefaultBaseUrl.Should().Be("https://api.deepseek.com");
        deepseek.AuthScheme.Should().Be(ProviderAuthScheme.BearerToken);
        deepseek.DefaultModel.Should().Be("deepseek-chat");

        var moonshot = ProviderCatalog.Resolve("moonshot")!;
        moonshot.DefaultBaseUrl.Should().Be("https://api.moonshot.ai");
        moonshot.AuthScheme.Should().Be(ProviderAuthScheme.BearerToken);
        ProviderCatalog.Resolve("kimi").Should().BeSameAs(moonshot);

        var zai = ProviderCatalog.Resolve("z-ai")!;
        zai.DefaultBaseUrl.Should().Be("https://api.z.ai");
        zai.AuthScheme.Should().Be(ProviderAuthScheme.BearerToken);
        // Product decision 2026-07-25: z-ai IS GLM (Zhipu); the key stays
        // "z-ai", the equivalence is documented on the display name.
        zai.DisplayName.Should().Contain("GLM");
        ProviderCatalog.Resolve("z.ai").Should().BeSameAs(zai);
        ProviderCatalog.Resolve("zai").Should().BeSameAs(zai);
    }

    [Test]
    public void Formerly_Unreachable_Allowlisted_Providers_Now_Resolve()
    {
        // Before Phase 1 these allow-listed keys could not be dispatched:
        // google / local-llm / z-ai had no map entry (only unlisted spellings
        // did), and azure-openai / together / groq had no named client at all.
        foreach (var key in new[] { "google", "local-llm", "z-ai", "azure-openai", "together", "groq" })
        {
            ProviderCatalog.Resolve(key).Should().NotBeNull(
                $"'{key}' was allow-listed but unreachable before the descriptor landed");
        }
    }

    [Test]
    public void NonHttp_Classification_Honours_The_Legacy_Rejection_Set()
    {
        // The exact key shapes HttpProviderClient rejected as CLI/MCP before.
        ProviderCatalog.ResolveNonHttp("claude-code")!.Transport.Should().Be(NonHttpProviderTransport.Cli);
        ProviderCatalog.ResolveNonHttp("claude-code-cli")!.Transport.Should().Be(NonHttpProviderTransport.Cli);
        ProviderCatalog.ResolveNonHttp("opencode")!.Transport.Should().Be(NonHttpProviderTransport.Cli);
        ProviderCatalog.ResolveNonHttp("opencode-cli")!.Transport.Should().Be(NonHttpProviderTransport.Cli);
        ProviderCatalog.ResolveNonHttp("zen-mcp")!.Transport.Should().Be(NonHttpProviderTransport.Mcp);
        ProviderCatalog.ResolveNonHttp("zen")!.Transport.Should().Be(NonHttpProviderTransport.Mcp);
        // And they are not HTTP-dispatchable.
        ProviderCatalog.Resolve("opencode").Should().BeNull();
        ProviderCatalog.Resolve("zen-mcp").Should().BeNull();
    }

    // ── Descriptor completeness ─────────────────────────────────────────────

    [Test]
    public void Http_Descriptors_Are_Complete()
    {
        foreach (var d in ProviderCatalog.HttpProviders)
        {
            d.Key.Should().NotBeNullOrWhiteSpace();
            d.DisplayName.Should().NotBeNullOrWhiteSpace();
            d.HttpClientName.Should().NotBeNullOrWhiteSpace(
                $"'{d.Key}' needs a named client for the HttpProviderClient dispatch path");
            d.ConfigSection.Should().NotBeNullOrWhiteSpace(
                $"'{d.Key}' needs a config section for BaseUrl/ApiKey overrides");
            if (d.VersionHeaderName is not null)
            {
                d.VersionHeaderValue.Should().NotBeNullOrWhiteSpace(
                    $"'{d.Key}' declares a version header name without a value");
            }

            // Azure OpenAI is the only descriptor allowed to have no default
            // base URL (its endpoint is per-resource and must be configured).
            if (d.Key != "azure-openai")
            {
                d.DefaultBaseUrl.Should().NotBeNullOrWhiteSpace($"'{d.Key}' needs a default base URL");
            }
        }
    }
}
