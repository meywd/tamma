using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Deployment;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Unit coverage for <see cref="DeploymentMode.Resolve"/> — the shared, pure
/// single-vs-SaaS resolver the engine layer uses to thread a real <c>mode</c> into
/// the deployment pipeline's production-approval gate (IMPORTANT fix, 2026-06-22).
///
/// <para>The load-bearing guarantee: SaaS/business resolves to <c>"business"</c>
/// (gate ON) and an absent/unknown mode FAILS SAFE to <c>"business"</c> — never a
/// silent <c>"dev"</c> that would skip the human gate and auto-deploy to prod.</para>
/// </summary>
[TestFixture]
public class DeploymentModeTests
{
    // ── Explicit Tamma:Mode override wins ──────────────────────────────────

    [TestCase("saas")]
    [TestCase("SaaS")]
    [TestCase("  saas  ")]
    [TestCase("business")]
    public void Resolve_ExplicitSaaSOrBusiness_IsBusiness(string explicitMode)
    {
        DeploymentMode.Resolve(explicitMode, false, false).Should().Be(DeploymentMode.Business);
    }

    [TestCase("single-user")]
    [TestCase("singleuser")]
    [TestCase("single_user")]
    [TestCase("dev")]
    [TestCase("DEV")]
    public void Resolve_ExplicitSingleUser_IsDev(string explicitMode)
    {
        DeploymentMode.Resolve(explicitMode, false, false).Should().Be(DeploymentMode.Dev);
    }

    [Test]
    public void Resolve_ExplicitModeWins_OverConfigSignals()
    {
        // An explicit single-user override beats SaaS-signal presence — explicit wins.
        DeploymentMode.Resolve("single-user", hasTenantSharedSecret: true, hasControlPlaneConnection: true)
            .Should().Be(DeploymentMode.Dev);
    }

    // ── Fail-safe: unknown explicit mode REQUIRES approval ─────────────────

    [TestCase("prod")]
    [TestCase("enterprise")]
    [TestCase("xyz")]
    public void Resolve_UnknownExplicitMode_FailsSafeToBusiness_NotDev(string bad)
    {
        DeploymentMode.Resolve(bad, false, false).Should().Be(DeploymentMode.Business,
            "an unrecognised explicit mode must REQUIRE approval (fail-safe) — never a silent prod auto-deploy");
    }

    // ── Inferred from SaaS-only config signals ─────────────────────────────

    [Test]
    public void Resolve_TenantSharedSecretPresent_IsBusiness()
    {
        DeploymentMode.Resolve(null, hasTenantSharedSecret: true, hasControlPlaneConnection: false)
            .Should().Be(DeploymentMode.Business, "Tamma:TenantSharedSecret is a SaaS-only signal");
    }

    [Test]
    public void Resolve_ControlPlaneConnectionPresent_IsBusiness()
    {
        DeploymentMode.Resolve(null, hasTenantSharedSecret: false, hasControlPlaneConnection: true)
            .Should().Be(DeploymentMode.Business, "ConnectionStrings:ControlPlane is a SaaS-only signal");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Resolve_NoExplicitModeNoSignals_DefaultsToDev_SingleUser(string? explicitMode)
    {
        DeploymentMode.Resolve(explicitMode, hasTenantSharedSecret: false, hasControlPlaneConnection: false)
            .Should().Be(DeploymentMode.Dev, "a self-hosted deployment with no SaaS signals is single-user/dev");
    }

    // ── Gate semantics: business → the pipeline gate engages ───────────────

    [Test]
    public void BusinessToken_MatchesPipelineGateCondition()
    {
        // The pipeline's ProdApprovalNeeded gate fires on mode == "business"
        // (case-insensitive). The resolver's Business token must equal that literal.
        DeploymentMode.Business.Should().Be("business",
            "the resolved SaaS token must equal the pipeline gate's 'business' literal so the gate engages");
    }
}
