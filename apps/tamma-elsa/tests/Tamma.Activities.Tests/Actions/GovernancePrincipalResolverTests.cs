using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Actions;
using Tamma.Data;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 (AC7/D9) — one test per principal-resolution branch, plus the
/// fail-loud sole-user rules and the never-from-the-wire pin.
/// </summary>
[TestFixture]
public class GovernancePrincipalResolverTests
{
    private sealed class FixedMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = tenantId;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FixedSoleUser(Guid? id) : ISoleUserProvider
    {
        public int Calls;

        public Task<Guid> GetSoleUserIdAsync(CancellationToken ct = default)
        {
            Calls++;
            return id is Guid g
                ? Task.FromResult(g)
                : throw new TammaError(
                    "GOVERNANCE.PRINCIPAL.NO_SOLE_USER", "no sole user",
                    retryable: false, severity: TammaErrorSeverity.High);
        }
    }

    private static ClaimsPrincipal Claims(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
        }, "test");
        return new ClaimsPrincipal(identity);
    }

    // ── SaaS ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Saas_WithTenantContext_ResolvesTheTenant()
    {
        var tid = Guid.NewGuid();
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SaaS), new FixedTenantContext(tid), new FixedSoleUser(null));

        var principal = await resolver.ResolveAsync();

        principal.Should().Be(GovernancePrincipal.ForTenant(tid));
    }

    [Test]
    public async Task Saas_WithoutTenant_ResolvesPlatformOnly_AndNeverConsultsAUserRow()
    {
        var soleUser = new FixedSoleUser(Guid.NewGuid());
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SaaS), new FixedTenantContext(null), soleUser);

        // Even an authenticated caller with a user id: in SaaS a user row is
        // not a legal principal at all.
        var principal = await resolver.ResolveAsync(Claims(Guid.NewGuid()));

        principal.IsPlatformOnly.Should().BeTrue();
        soleUser.Calls.Should().Be(0, "the SaaS branch must NEVER reach for a user row");
    }

    // ── Single-user ─────────────────────────────────────────────────────────

    [Test]
    public async Task SingleUser_HumanPlane_UsesTheAuthenticatedClaims()
    {
        var uid = Guid.NewGuid();
        var soleUser = new FixedSoleUser(Guid.NewGuid());
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SingleUser), new FixedTenantContext(null), soleUser);

        var principal = await resolver.ResolveAsync(Claims(uid));

        principal.Should().Be(GovernancePrincipal.ForUser(uid));
        soleUser.Calls.Should().Be(0);
    }

    [Test]
    public async Task SingleUser_EnginePlane_UsesTheSoleUserProvider()
    {
        var sole = Guid.NewGuid();
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SingleUser), new FixedTenantContext(null),
            new FixedSoleUser(sole));

        var principal = await resolver.ResolveAsync(caller: null);

        principal.Should().Be(GovernancePrincipal.ForUser(sole));
    }

    [Test]
    public async Task SingleUser_EmptyUsers_FailsLoud()
    {
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SingleUser), new FixedTenantContext(null),
            new FixedSoleUser(null));

        var act = () => resolver.ResolveAsync(caller: null);

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("GOVERNANCE.PRINCIPAL.NO_SOLE_USER",
                "guessing a principal would silently apply the wrong policy");
    }

    [Test]
    public async Task EnginePlane_NeverReadsPrincipalFromTheWireBody()
    {
        // The resolver's surface admits NO request payload at all — identity
        // comes only from the ambient tenant context / claims / sole-user
        // provider. A caller-supplied tenant id in a body cannot reach it: pin
        // by resolving with a tenant context that DISAGREES with what any
        // body might have said, and asserting the ambient context wins.
        var ambient = Guid.NewGuid();
        var resolver = new GovernancePrincipalResolver(
            new FixedMode(TammaMode.SaaS), new FixedTenantContext(ambient),
            new FixedSoleUser(null));

        var principal = await resolver.ResolveAsync(Claims(Guid.NewGuid()));

        principal.TenantId.Should().Be(ambient);
        principal.UserId.Should().BeNull();

        typeof(IGovernancePrincipalResolver).GetMethods()
            .SelectMany(m => m.GetParameters())
            .Should().NotContain(
                p => p.Name!.Contains("body", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("request", StringComparison.OrdinalIgnoreCase),
                "the principal is never taken from caller-supplied payload");
    }

    // ── SoleUserProvider itself ─────────────────────────────────────────────

    [Test]
    public async Task SoleUserProvider_PrefersConfig_OverTheUsersTable()
    {
        var configured = Guid.NewGuid();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SoleUserProvider.OwnerUserIdConfigKey] = configured.ToString(),
            })
            .Build();

        var provider = new SoleUserProvider(config, factory: null);

        (await provider.GetSoleUserIdAsync()).Should().Be(configured);
    }

    [Test]
    public async Task SoleUserProvider_NoConfigNoDatabase_Throws_AndNeverCachesFailure()
    {
        var provider = new SoleUserProvider(
            new ConfigurationBuilder().Build(), factory: null);

        var act = () => provider.GetSoleUserIdAsync();
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("GOVERNANCE.PRINCIPAL.NO_SOLE_USER");

        // Still throws (no cached failure) — the first user created after
        // this call must be resolvable without a restart.
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("GOVERNANCE.PRINCIPAL.NO_SOLE_USER");
    }
}
