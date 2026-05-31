using System;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Data;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-3 AC3 follow-up (2026-05-30 residual #2, revised 2026-05-31) —
/// hard-fail when a Production deployment <b>that has opted into per-tenant
/// isolation</b> is missing the <c>ConnectionStrings:ControlPlane</c> string.
///
/// <para>The gap: <see cref="StubTenantConnectionResolver"/> is registered
/// unconditionally by <c>AddTammaData</c>; the real
/// <c>LruPooledTenantConnectionResolver</c> only replaces it when a CP
/// connection string is present. An operator who <b>intends</b> per-tenant
/// DB isolation but forgets the CP string would silently run on the stub —
/// every tenant on the central DB, isolation defeated.</para>
///
/// <para><b>Why the opt-in (2026-05-31 revision):</b> shared-infrastructure
/// mode (every tenant on the central Postgres, isolation via Phase-3 RLS,
/// <c>ConnectionStrings:ControlPlane</c> deliberately unset) is the
/// <b>documented production default</b> — it is exactly what the Hetzner VPS
/// deploy runs (see <c>docker-compose.prod.yml</c>). The first cut of this
/// guard fired on ANY Production host without a CP string and so bricked
/// that supported topology (the Deploy-to-VPS job crash-looped). The guard
/// now fires only when the operator has explicitly declared
/// <c>Tamma:RequireTenantIsolation=true</c> — i.e. "per-tenant DBs are
/// mandatory here, a missing CP string is a misconfiguration." Without the
/// opt-in, shared-DB-in-Production is allowed (the existing Info log stands).</para>
///
/// <para>The guard is also a no-op outside Production: the entire test suite
/// runs in Development on the stub WITHOUT a CP string and must stay green.</para>
/// </summary>
[TestFixture]
public class StubResolverProductionGuardTests
{
    // ── Failing case: Production + opted-in isolation + NO CP string ──────
    // The operator declared per-tenant DBs mandatory but forgot the string.

    [Test]
    public void Production_RequireIsolation_WithoutControlPlaneConnectionString_Throws()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            requireTenantIsolation: true,
            controlPlaneConnectionString: null);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("ConnectionStrings:ControlPlane")
            .And.Contain("RequireTenantIsolation");
    }

    [Test]
    public void Production_RequireIsolation_WithWhitespaceControlPlaneConnectionString_Throws()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            requireTenantIsolation: true,
            controlPlaneConnectionString: "   ");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("ConnectionStrings:ControlPlane");
    }

    // ── Passing case: Production + opted-in isolation + CP string set ─────

    [Test]
    public void Production_RequireIsolation_WithControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            requireTenantIsolation: true,
            controlPlaneConnectionString:
                "Host=cp;Database=tamma_control;Username=u;Password=p");

        act.Should().NotThrow();
    }

    // ── Passing case: Production + shared-DB DEFAULT (no opt-in) + no CP ──
    // THIS is the documented Hetzner-VPS topology that the first cut of the
    // guard broke. Shared-infrastructure mode (RLS isolation) is allowed in
    // Production when the operator has NOT opted into per-tenant isolation.

    [Test]
    public void Production_SharedDbDefault_WithoutControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: true,
            requireTenantIsolation: false,
            controlPlaneConnectionString: null);

        act.Should().NotThrow();
    }

    // ── Passing case: non-Production (dev / test) is always allowed ──────
    // The branch the entire existing suite relies on — stub resolver without
    // a CP string is acceptable outside Production, opt-in or not.

    [Test]
    public void Development_RequireIsolation_WithoutControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: false,
            requireTenantIsolation: true,
            controlPlaneConnectionString: null);

        act.Should().NotThrow();
    }

    [Test]
    public void Development_SharedDbDefault_WithoutControlPlaneConnectionString_DoesNotThrow()
    {
        Action act = () => DependencyInjection.GuardTenantIsolationInProduction(
            isProduction: false,
            requireTenantIsolation: false,
            controlPlaneConnectionString: null);

        act.Should().NotThrow();
    }
}
