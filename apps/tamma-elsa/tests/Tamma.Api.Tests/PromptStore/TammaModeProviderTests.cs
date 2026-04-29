using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Story 27-2 — verify mode detection. The provider settles
/// <see cref="TammaMode"/> from the configuration once at startup;
/// every per-request handler reads the same value.
/// </summary>
[TestFixture]
public class TammaModeProviderTests
{
    private static IConfiguration Build(IEnumerable<KeyValuePair<string, string?>> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Test]
    public void TammaModeProvider_ExplicitSaaS_Wins()
    {
        var cfg = Build(new[]
        {
            new KeyValuePair<string, string?>("Tamma:Mode", "saas"),
        });

        TammaModeProvider.Resolve(cfg).Should().Be(TammaMode.SaaS);
    }

    [Test]
    public void TammaModeProvider_ExplicitSingleUser_Wins()
    {
        var cfg = Build(new[]
        {
            new KeyValuePair<string, string?>("Tamma:Mode", "single-user"),
            // Even with SaaS-signal config present, the explicit override wins.
            new KeyValuePair<string, string?>("Tamma:TenantSharedSecret", "anything"),
        });

        TammaModeProvider.Resolve(cfg).Should().Be(TammaMode.SingleUser);
    }

    [Test]
    public void TammaModeProvider_TenantSharedSecret_InfersSaaS()
    {
        var cfg = Build(new[]
        {
            new KeyValuePair<string, string?>("Tamma:TenantSharedSecret", "deadbeef"),
        });

        TammaModeProvider.Resolve(cfg).Should().Be(TammaMode.SaaS);
    }

    [Test]
    public void TammaModeProvider_ControlPlaneConnection_InfersSaaS()
    {
        var cfg = Build(new[]
        {
            new KeyValuePair<string, string?>(
                "ConnectionStrings:ControlPlane",
                "Host=localhost;Database=cp;Username=u;Password=p"),
        });

        TammaModeProvider.Resolve(cfg).Should().Be(TammaMode.SaaS);
    }

    [Test]
    public void TammaModeProvider_NoSignals_DefaultsToSingleUser()
    {
        var cfg = Build(Array.Empty<KeyValuePair<string, string?>>());

        TammaModeProvider.Resolve(cfg).Should().Be(TammaMode.SingleUser);
    }

    [Test]
    public void TammaModeProvider_UnrecognisedExplicitMode_Throws()
    {
        var cfg = Build(new[]
        {
            new KeyValuePair<string, string?>("Tamma:Mode", "potato"),
        });

        var act = () => TammaModeProvider.Resolve(cfg);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*potato*");
    }
}
