using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// Story 32-3 Phase 1 (AC6.1) — the platform-fallback gating matrix.
/// </summary>
[TestFixture]
public class ConfigPlatformFallbackPolicyTests
{
    private static IConfiguration Config(params (string Key, string Value)[] kv) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static ConfigPlatformFallbackPolicy Policy(
        TammaMode mode, params (string, string)[] kv) =>
        new(Config(kv), new StubMode(mode));

    private static readonly Guid Tenant = Guid.NewGuid();

    [Test]
    public void SingleUser_AlwaysAllowed()
    {
        Policy(TammaMode.SingleUser)
            .IsPlatformFallbackAllowed(null, "anthropic")
            .Should().BeTrue();
    }

    [Test]
    public void SingleUser_AllowedEvenWhenConfigDisablesIt()
    {
        // Disable flags are SaaS-only knobs; single-user ignores them.
        Policy(TammaMode.SingleUser, ("Providers:PlatformFallbackDisabled", "true"))
            .IsPlatformFallbackAllowed(null, "anthropic")
            .Should().BeTrue();
    }

    [Test]
    public void Saas_AllowedByDefault()
    {
        Policy(TammaMode.SaaS)
            .IsPlatformFallbackAllowed(Tenant, "anthropic")
            .Should().BeTrue();
    }

    [Test]
    public void Saas_NullTenant_TreatedAsSingleUserScope_Allowed()
    {
        // A null tenant id in SaaS means no tenant in context — platform scope.
        Policy(TammaMode.SaaS)
            .IsPlatformFallbackAllowed(null, "anthropic")
            .Should().BeTrue();
    }

    [Test]
    public void Saas_DisabledGlobally_Denied()
    {
        Policy(TammaMode.SaaS, ("Providers:PlatformFallbackDisabled", "true"))
            .IsPlatformFallbackAllowed(Tenant, "anthropic")
            .Should().BeFalse();
    }

    [Test]
    public void Saas_DisabledPerProvider_DeniesOnlyThatProvider()
    {
        var policy = Policy(
            TammaMode.SaaS,
            ("Providers:anthropic:PlatformFallbackDisabled", "true"));

        policy.IsPlatformFallbackAllowed(Tenant, "anthropic").Should().BeFalse();
        policy.IsPlatformFallbackAllowed(Tenant, "openai").Should().BeTrue();
    }

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }
}
