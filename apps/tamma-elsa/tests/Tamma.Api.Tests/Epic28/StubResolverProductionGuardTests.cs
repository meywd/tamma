using System;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Data;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-3 AC3 follow-up (2026-05-30 residual #2) — release-build
/// hard-fail when a Production deployment is missing the
/// <c>ConnectionStrings:ControlPlane</c> string.
///
/// <para>The gap: <see cref="StubTenantConnectionResolver"/> is
/// registered unconditionally by <c>AddTammaData</c>; the real
/// <c>LruPooledTenantConnectionResolver</c> only replaces it when a CP
/// connection string is present. A misconfigured Production deployment
/// (CP string unset) therefore silently runs on the stub — every tenant
/// shares the central DB and tenant isolation is defeated, with only an
/// Info log to show for it. The guard
/// (<see cref="DependencyInjection.GuardTenantIsolationInProduction"/>)
/// turns that silent fallback into a fail-fast startup exception.</para>
///
/// <para>Crucially, the guard fires ONLY in Production: Development /
/// Test deployments (the entire 2664-test suite) run on the stub WITHOUT
/// a CP connection string and must stay green.</para>
/// </summary>
[TestFixture]
public class StubResolverProductionGuardTests
{
    // ── Failing case: Production WITHOUT a CP connection string ──────

    [Test]
    public void Production_WithoutControlPlaneConnectionString_Throws()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            controlPlaneConnectionString: null);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("ConnectionStrings:ControlPlane")
            .And.Contain("tenant isolation");
    }

    [Test]
    public void Production_WithWhitespaceControlPlaneConnectionString_Throws()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            controlPlaneConnectionString: "   ");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("ConnectionStrings:ControlPlane");
    }

    // ── Passing case: Production WITH a CP connection string ─────────

    [Test]
    public void Production_WithControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            controlPlaneConnectionString:
                "Host=cp;Database=tamma_control;Username=u;Password=p");

        act.Should().NotThrow();
    }

    // ── Passing case: non-Production (dev / test) is always allowed ──
    // This is the branch the entire existing suite relies on — the stub
    // resolver without a CP string is acceptable outside Production.

    [Test]
    public void Development_WithoutControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: false,
            controlPlaneConnectionString: null);

        act.Should().NotThrow();
    }

    [Test]
    public void Development_WithControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: false,
            controlPlaneConnectionString:
                "Host=cp;Database=tamma_control;Username=u;Password=p");

        act.Should().NotThrow();
    }
}
