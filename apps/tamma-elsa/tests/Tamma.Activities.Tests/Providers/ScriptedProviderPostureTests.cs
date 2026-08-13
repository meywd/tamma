using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Activities.Security;
using Tamma.Api.Services.Providers;

namespace Tamma.Activities.Tests.Providers;

/// <summary>
/// 2026-08-13 (Epic 31 P5 follow-up) — the "scripted" provider's SECURITY
/// POSTURE, pinned cross-surface (this fixture sees both ProviderAllowlist and
/// ProviderCatalog, like ProviderCatalogTests):
/// <list type="bullet">
///   <item>catalogued as non-HTTP, <c>Allowlisted=false</c> — the defensive
///   non-selectable convention, so the allowlist⇔catalog keyset agreement is
///   UNCHANGED (no default-allowlist member was added);</item>
///   <item>the shipped default allowlist REFUSES the key — no deployment can
///   select it without the explicit flag;</item>
///   <item>the flag on any production-shaped host throws at startup.</item>
/// </list>
/// </summary>
[TestFixture]
public class ScriptedProviderPostureTests
{
    [Test]
    public void Scripted_IsCatalogued_NonHttp_NotAllowlisted()
    {
        var entry = ProviderCatalog.ResolveNonHttp("scripted");
        entry.Should().NotBeNull("the keyset contract is total — every shipped key is catalogued");
        entry!.Allowlisted.Should().BeFalse(
            "scripted is never selectable by default (the claude-code defensive convention)");
        entry.Transport.Should().Be(NonHttpProviderTransport.InProcess);
        ProviderCatalog.Resolve("scripted").Should().BeNull("scripted is not HTTP-dispatchable");
    }

    [Test]
    public void DefaultAllowlist_RefusesScripted()
    {
        new ProviderAllowlist().IsAllowed("scripted").Should().BeFalse(
            "the shipped defaults must never admit the test provider");
        ProviderAllowlist.IsAllowedDefault("scripted").Should().BeFalse();
    }

    [Test]
    public void OptIn_AdmitsScripted_ViaAdditionalProvidersOnly()
    {
        var options = Options.Create(new ProviderAllowlistOptions
        {
            AdditionalProviders = { ScriptedProviderPosture.ProviderKey },
        });
        new ProviderAllowlist(options).IsAllowed("scripted").Should().BeTrue(
            "the DI options path (what AddScriptedLlmProvider / the engine flag configures) " +
            "is the ONLY way the key becomes selectable");
    }

    [Test]
    public void FlagOff_IsDisabled_AndAssertReturnsFalse()
    {
        var config = Build();
        ScriptedProviderPosture.IsEnabled(config).Should().BeFalse();
        ScriptedProviderPosture.AssertAllowed(config).Should().BeFalse();
    }

    [Test]
    public void FlagOn_CleanHost_IsAllowed()
    {
        var config = Build(("Llm:EnableScriptedProvider", "true"));
        ScriptedProviderPosture.AssertAllowed(config).Should().BeTrue();
    }

    [TestCase("Tamma:TenantSharedSecret", "secret")]
    [TestCase("ConnectionStrings:ControlPlane", "Host=cp")]
    [TestCase("Tamma:Mode", "saas")]
    public void FlagOn_ProductionSignal_Throws(string key, string value)
    {
        var config = Build(("Llm:EnableScriptedProvider", "true"), (key, value));
        var act = () => ScriptedProviderPosture.AssertAllowed(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*refused*", "the guard is structural — the host must not start");
    }

    [Test]
    public void ProductionSignal_MirrorsTammaModeDetection_ExplicitSingleUserIsClean()
    {
        // An explicit single-user mode is NOT a production signal (mirrors
        // TammaModeProvider.Resolve: only saas / the SaaS-only config keys are).
        var config = Build(
            ("Llm:EnableScriptedProvider", "true"),
            ("Tamma:Mode", "single-user"));
        ScriptedProviderPosture.AssertAllowed(config).Should().BeTrue();
    }

    private static IConfiguration Build(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
}
