using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Middleware;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Middleware;

/// <summary>
/// Story 28-8 follow-up (2026-05-30 audit-residual closure) — the
/// <see cref="EnsurePersonalTenantMiddleware"/> survives in the pipeline
/// to serve <see cref="TammaMode.SingleUser"/> deployments (the sole user
/// gets a personal tenant minted on first authenticated request). In
/// <see cref="TammaMode.SaaS"/> the middleware MUST short-circuit without
/// touching any repository — SaaS-mode tenant creation is owned by the
/// async <c>CreateTenantWorkflow</c> at registration / verify-email time,
/// NOT by a per-request middleware (Story 28-8 AC1 + AC4).
/// </summary>
[TestFixture]
public class EnsurePersonalTenantMiddlewareTests
{
    private sealed class StubModeProvider : ITammaModeProvider
    {
        public StubModeProvider(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private sealed class StubTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; }
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    /// <summary>
    /// Unified-tenancy Phase 2 Task 6 — fake for the synchronous
    /// provisioning hook. The middleware resolves
    /// <see cref="ITenantProvisioningService"/> from
    /// <c>context.RequestServices</c> and calls ONLY
    /// <see cref="ITenantProvisioningService.ProvisionAsync"/>; the
    /// step-level members throw to pin that contract.
    /// </summary>
    private sealed class FakeTenantProvisioningService : ITenantProvisioningService
    {
        public List<Guid> ProvisionCalls { get; } = new();
        public Exception? ThrowOnProvision { get; set; }

        public Task<TenantPlacement> AssignPlacementAsync(
            Guid tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("middleware must only call ProvisionAsync");

        public Task<string?> CreateRoleAsync(
            Guid tenantId, TenantPlacement placement, CancellationToken ct = default) =>
            throw new NotSupportedException("middleware must only call ProvisionAsync");

        public Task CreateSchemaAsync(
            Guid tenantId, TenantPlacement placement, CancellationToken ct = default) =>
            throw new NotSupportedException("middleware must only call ProvisionAsync");

        public Task<string> BuildConnectionStringAsync(
            Guid tenantId, TenantPlacement placement, string password,
            CancellationToken ct = default) =>
            throw new NotSupportedException("middleware must only call ProvisionAsync");

        public Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)
        {
            ProvisionCalls.Add(tenantId);
            if (ThrowOnProvision is not null) throw ThrowOnProvision;
            return Task.CompletedTask;
        }
    }

    private static IServiceProvider BuildRequestServices(ITenantProvisioningService provisioning) =>
        new ServiceCollection()
            .AddSingleton(provisioning)
            .BuildServiceProvider();

    private static DefaultHttpContext BuildAuthenticatedContext(string path, Guid userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            authenticationType: "Test");
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }

    private static EnsurePersonalTenantMiddleware BuildMiddleware(out int nextCallCount)
    {
        var counter = 0;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            counter++;
            return Task.CompletedTask;
        });
        nextCallCount = 0;
        // Return both — closure captures `counter`, caller reads via a
        // local ref by re-invoking through an outer wrapper.
        return mw;
    }

    [Test]
    public async Task SaaSMode_AuthenticatedRequest_NoTenant_DefersToNext_WithoutCreatingTenant()
    {
        var userId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);

        var tenantContext = new StubTenantContext();
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Strict);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Strict);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var events = new Mock<IEventRepository>(MockBehavior.Strict);
        var modeProvider = new StubModeProvider(TammaMode.SaaS);

        var nextCalled = false;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        nextCalled.Should().BeTrue(
            "SaaS-mode requests must pass through — TenantContextMiddleware " +
            "already handled tenant resolution; AC1 mandates this middleware " +
            "is a no-op in SaaS.");
        tenantContext.TenantId.Should().BeNull(
            "SaaS-mode bootstrap is owned by CreateTenantWorkflow, not this middleware");
        // MockBehavior.Strict — any repo call would have thrown. The mere
        // fact we got here means zero CP / membership / user / events
        // queries were issued.
    }

    [Test]
    public async Task SingleUserMode_AuthenticatedRequest_NoTenant_NoMembership_AutoCreatesPersonalTenant()
    {
        // This test pins the single-user-mode survival path. The middleware
        // must still mint a personal tenant + membership + persist the
        // active tenant + emit TENANT.AUTO_CREATED.SUCCESS.
        var userId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);
        // Phase 3: provisioning is mandatory on the auto-create path, so the
        // fake must be resolvable from RequestServices.
        ctx.RequestServices = BuildRequestServices(new FakeTenantProvisioningService());

        var tenantContext = new StubTenantContext();
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Loose);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        var events = new Mock<IEventRepository>(MockBehavior.Loose);
        var modeProvider = new StubModeProvider(TammaMode.SingleUser);

        membershipRepo
            .Setup(r => r.GetUserTenantsAsync(userId))
            .ReturnsAsync(new List<TenantMembership>());
        userRepo
            .Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "sole@self.host",
                AuthMethod = "email",
                DisplayName = "Sole User",
            });
        tenantRepo
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>()))
            .ReturnsAsync((Tenant?)null);
        tenantRepo
            .Setup(r => r.CreateAsync(It.IsAny<Tenant>()))
            .ReturnsAsync((Tenant t) => { t.Id = Guid.NewGuid(); return t; });

        var nextCalled = false;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        nextCalled.Should().BeTrue();
        tenantContext.TenantId.Should().NotBeNull(
            "single-user-mode first request must bind a freshly-minted personal tenant");
        tenantRepo.Verify(r => r.CreateAsync(It.IsAny<Tenant>()), Times.Once);
        membershipRepo.Verify(
            r => r.AddAsync(It.IsAny<Guid>(), userId, "owner"), Times.Once);
        userRepo.Verify(r => r.UpdateActiveTenantAsync(userId, It.IsAny<Guid>()), Times.Once);
        events.Verify(
            r => r.AppendAsync(It.Is<DomainEvent>(e => e.Type == "TENANT.AUTO_CREATED.SUCCESS")),
            Times.Once);
    }

    [Test]
    public async Task SingleUserMode_TenantAlreadyBound_PassesThrough_NoWork()
    {
        // Sanity: when TenantContextMiddleware already bound a tenant, this
        // middleware bails before doing anything — same behaviour as before
        // the mode gate landed.
        var userId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);

        var tenantContext = new StubTenantContext();
        tenantContext.SetTenantId(Guid.NewGuid());

        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Strict);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Strict);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var events = new Mock<IEventRepository>(MockBehavior.Strict);
        var modeProvider = new StubModeProvider(TammaMode.SingleUser);

        var nextCalled = false;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        nextCalled.Should().BeTrue();
    }

    // ── Unified-tenancy Phase 2 Task 6: synchronous provisioning hook ──

    [Test]
    public async Task SingleUserMode_FirstLoginCreation_InvokesProvisioningOnce_WithNewTenantId()
    {
        var userId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);
        var provisioning = new FakeTenantProvisioningService();
        ctx.RequestServices = BuildRequestServices(provisioning);

        var tenantContext = new StubTenantContext();
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Loose);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        var events = new Mock<IEventRepository>(MockBehavior.Loose);
        var modeProvider = new StubModeProvider(TammaMode.SingleUser);

        membershipRepo
            .Setup(r => r.GetUserTenantsAsync(userId))
            .ReturnsAsync(new List<TenantMembership>());
        userRepo
            .Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "sole@self.host",
                AuthMethod = "email",
                DisplayName = "Sole User",
            });
        tenantRepo
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>()))
            .ReturnsAsync((Tenant?)null);
        tenantRepo
            .Setup(r => r.CreateAsync(It.IsAny<Tenant>()))
            .ReturnsAsync((Tenant t) => { t.Id = Guid.NewGuid(); return t; });

        var mw = new EnsurePersonalTenantMiddleware(_ => Task.CompletedTask);

        await mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        tenantContext.TenantId.Should().NotBeNull();
        provisioning.ProvisionCalls.Should().ContainSingle(
                "the freshly-minted personal tenant must be provisioned synchronously "
                + "(placement → role → schema → conn string → migrate) exactly once")
            .Which.Should().Be(tenantContext.TenantId!.Value,
                "ProvisionAsync must receive the NEW tenant's id");
    }

    [Test]
    public async Task SingleUserMode_ExistingMembership_DoesNotProvision()
    {
        // Tenant already exists (existing-membership path) — provisioning
        // happened at creation time; re-running it per-request would be
        // wasted work. The hook fires ONLY on first-login creation.
        var userId = Guid.NewGuid();
        var existingTenantId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);
        var provisioning = new FakeTenantProvisioningService();
        ctx.RequestServices = BuildRequestServices(provisioning);

        var tenantContext = new StubTenantContext();
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Strict);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Loose);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        var events = new Mock<IEventRepository>(MockBehavior.Loose);
        var modeProvider = new StubModeProvider(TammaMode.SingleUser);

        membershipRepo
            .Setup(r => r.GetUserTenantsAsync(userId))
            .ReturnsAsync(new List<TenantMembership>
            {
                new()
                {
                    TenantId = existingTenantId,
                    UserId = userId,
                    Role = "owner",
                    JoinedAt = DateTime.UtcNow.AddDays(-1),
                },
            });

        var nextCalled = false;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        nextCalled.Should().BeTrue();
        tenantContext.TenantId.Should().Be(existingTenantId);
        provisioning.ProvisionCalls.Should().BeEmpty(
            "an already-existing tenant must NOT be re-provisioned on every request");
    }

    [Test]
    public async Task SingleUserMode_ProvisioningThrows_RequestFails()
    {
        // Phase 3 failure policy: propagate. The Phase 2 shared-path stub
        // is gone — an unprovisioned tenant cannot access tenant data at
        // all, so the first request must fail loudly with the real error
        // instead of proceeding with a broken half-tenant.
        var userId = Guid.NewGuid();
        var ctx = BuildAuthenticatedContext("/api/v1/issues", userId);
        var provisioning = new FakeTenantProvisioningService
        {
            ThrowOnProvision = new InvalidOperationException("cluster unreachable"),
        };
        ctx.RequestServices = BuildRequestServices(provisioning);

        var tenantContext = new StubTenantContext();
        var tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        var membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Loose);
        var userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        var events = new Mock<IEventRepository>(MockBehavior.Loose);
        var modeProvider = new StubModeProvider(TammaMode.SingleUser);

        membershipRepo
            .Setup(r => r.GetUserTenantsAsync(userId))
            .ReturnsAsync(new List<TenantMembership>());
        userRepo
            .Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "sole@self.host",
                AuthMethod = "email",
                DisplayName = "Sole User",
            });
        tenantRepo
            .Setup(r => r.GetBySlugAsync(It.IsAny<string>()))
            .ReturnsAsync((Tenant?)null);
        tenantRepo
            .Setup(r => r.CreateAsync(It.IsAny<Tenant>()))
            .ReturnsAsync((Tenant t) => { t.Id = Guid.NewGuid(); return t; });

        var nextCalled = false;
        var mw = new EnsurePersonalTenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var act = () => mw.InvokeAsync(
            ctx,
            tenantContext,
            tenantRepo.Object,
            membershipRepo.Object,
            userRepo.Object,
            events.Object,
            modeProvider,
            NullLogger<EnsurePersonalTenantMiddleware>.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>(
                "Phase 3 removed the shared-path stub — a provisioning failure "
                + "must propagate so the first request fails with the real error")
            .WithMessage("cluster unreachable");
        provisioning.ProvisionCalls.Should().ContainSingle(
            "the hook must have been attempted before the failure propagated");
        nextCalled.Should().BeFalse(
            "the request must NOT proceed when provisioning fails — there is "
            + "no shared path for an unprovisioned tenant to ride");
        events.Verify(
            r => r.AppendAsync(It.Is<DomainEvent>(e => e.Type == "TENANT.AUTO_CREATED.SUCCESS")),
            Times.Never,
            "the success event is emitted after provisioning, which threw");
    }
}
