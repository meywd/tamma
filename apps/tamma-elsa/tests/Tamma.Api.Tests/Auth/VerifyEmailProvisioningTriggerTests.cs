using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Dtos.Auth;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-5 AC1 follow-up (2026-05-30) — verify-email must emit
/// <c>TENANT.PROVISIONING_REQUESTED</c> + flip <c>tenants.Status</c> from
/// <c>pending_verification</c> to <c>provisioning</c> for every tenant the
/// freshly-verified user owns. Closes the gap flagged by the 2026-05-29
/// Epic 28 audit.
///
/// <para><b>Design constraint</b>: today <c>Register</c> does NOT stamp
/// <c>Status='pending_verification'</c> on the newly-minted personal
/// tenant — Status defaults to NULL, and <c>TenantStatusEvaluator</c>
/// treats NULL as active. To avoid bricking the live signup flow (no
/// consumer drains the trigger event today), the <c>VerifyEmail</c>
/// transition is <b>conditional</b>: only tenants explicitly stamped
/// <c>pending_verification</c> flip to <c>provisioning</c> and emit the
/// event. NULL-status tenants are left untouched — preserves the existing
/// "shared-infra default" semantics. This matches the conditional pattern
/// used by <see cref="Admin.AdminTenantsEndpoints.RetryTenant"/>.</para>
/// </summary>
[TestFixture]
public class VerifyEmailProvisioningTriggerTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
    private ILoggerFactory _loggerFactory = null!;
#pragma warning restore NUnit1032
    private IUserRepository _userRepo = null!;
    private ITenantRepository _tenantRepo = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private async Task<(User user, string rawToken)> CreateUnverifiedUser(string email)
    {
        var rawToken = Guid.NewGuid().ToString("N");
        var user = await _userRepo.CreateAsync(new User
        {
            Email = email.ToLowerInvariant(),
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
            EmailVerificationTokenHash = HashToken(rawToken),
            EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24),
        });
        return (user, rawToken);
    }

    private async Task<Tenant> CreateOwnedTenant(Guid ownerId, string slug, string? status = null)
    {
        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "T-" + slug,
            Slug = slug,
            Type = "personal",
            OwnerId = ownerId,
        });

        if (status is not null)
        {
            // Status is a shadow property; set via EF Property API and save.
            _db.Entry(tenant).Property("Status").CurrentValue = status;
            await _db.SaveChangesAsync();
        }

        return tenant;
    }

    private async Task<string?> ReadStatus(Guid tenantId)
    {
        // Use a fresh scope so we read what was committed by the handler's
        // own scope (the SUT opens its own DbContext via IPlatformEventPublisher).
        // Project the shadow Status column via EF.Property to bypass change
        // tracking — Entry().Property.CurrentValue on a fresh entity is
        // fine, but the projection form is closer to "what does the DB hold".
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        return await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => EF.Property<string?>(t, "Status"))
            .FirstOrDefaultAsync();
    }

    private static HttpContext ContextWithPublisher(RecordingPlatformEventPublisher publisher)
    {
        // Mirrors AuthAuditEventTests' MakeContext — but explicitly
        // returns the factory's IServiceScopeFactory so any scope opened
        // by the SUT pulls real services (ControlPlaneDbContext etc.)
        // from the factory's container, NOT from the per-test override
        // sub-provider. Without this, the auto-registered scope factory
        // on the sub-provider would hand the SUT a scope with only the
        // override services in it (db == null).
        var primary = new TestOverrideServiceProvider(
            new Dictionary<Type, object>
            {
                [typeof(IPlatformEventPublisher)] = publisher,
            },
            ApiTestFixture.Factory.Services);
        var ctx = new DefaultHttpContext { RequestServices = primary };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>
    /// Service provider that returns the per-test overrides for matching
    /// types and falls through to the factory's container for everything
    /// else — INCLUDING the canonical <see cref="IServiceScopeFactory"/>.
    /// This is what lets the SUT open a scope and resolve real scoped
    /// services (ControlPlaneDbContext) that the test never owns.
    /// </summary>
    private sealed class TestOverrideServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _overrides;
        private readonly IServiceProvider _fallback;
        public TestOverrideServiceProvider(
            Dictionary<Type, object> overrides, IServiceProvider fallback)
        {
            _overrides = overrides;
            _fallback = fallback;
        }
        public object? GetService(Type serviceType)
            => _overrides.TryGetValue(serviceType, out var v) ? v : _fallback.GetService(serviceType);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Test]
    public async Task VerifyEmail_FlipsPendingVerificationToProvisioning_AndEmitsEvent()
    {
        // AC1 happy path: user owns a tenant explicitly marked
        // pending_verification (matches the db-per-tenant rollout pattern
        // where Register stamps the new tenant for the upcoming
        // CreateTenantWorkflow trigger). VerifyEmail flips Status →
        // provisioning and emits TENANT.PROVISIONING_REQUESTED.
        var (user, rawToken) = await CreateUnverifiedUser("alice@example.com");
        var tenant = await CreateOwnedTenant(
            user.Id, $"a-{Guid.NewGuid():N}".Substring(0, 12),
            status: "pending_verification");

        // Sanity check that the test setup actually persisted Status.
        (await ReadStatus(tenant.Id)).Should().Be("pending_verification",
            "setup precondition — Status must be pending_verification before VerifyEmail runs");

        var publisher = new RecordingPlatformEventPublisher();
        var ctx = ContextWithPublisher(publisher);

        var result = await AuthEndpoints.VerifyEmail(
            new VerifyEmailRequest(rawToken),
            _userRepo, _loggerFactory, ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Status flipped on the row.
        (await ReadStatus(tenant.Id)).Should().Be("provisioning",
            "AC1 — verify-email transitions pending_verification → provisioning");

        // EmailVerified was the existing behaviour — must still happen.
        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        refreshed!.EmailVerified.Should().BeTrue();

        // Event row with the canonical type + tenant binding + actor.
        publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.PROVISIONING_REQUESTED");
        var evt = publisher.Events.Single(e => e.Type == "TENANT.PROVISIONING_REQUESTED");
        evt.TenantId.Should().Be(tenant.Id);
        evt.UserId.Should().Be(user.Id);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags!["tenantId"].Should().Be(tenant.Id.ToString("D"));
        tags["userId"].Should().Be(user.Id.ToString("D"));
        tags["source"].Should().Be("verify-email",
            "differentiates this trigger from admin-retry and other sources");
    }

    [Test]
    public async Task VerifyEmail_NullStatusTenant_LeavesRowUntouchedAndEmitsNoEvent()
    {
        // Live-flow safety: today Register does NOT stamp
        // pending_verification, so tenants have Status=NULL (treated as
        // active by TenantStatusEvaluator). VerifyEmail must NOT promote
        // those to provisioning — that would brick the user (503 until
        // a workflow that doesn't yet exist drains the event).
        var (user, rawToken) = await CreateUnverifiedUser("bob@example.com");
        var tenant = await CreateOwnedTenant(
            user.Id, $"b-{Guid.NewGuid():N}".Substring(0, 12),
            status: null);

        var publisher = new RecordingPlatformEventPublisher();
        var ctx = ContextWithPublisher(publisher);

        var result = await AuthEndpoints.VerifyEmail(
            new VerifyEmailRequest(rawToken),
            _userRepo, _loggerFactory, ctx);
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        (await ReadStatus(tenant.Id)).Should().BeNull(
            "NULL-Status tenants are the shared-infra default and must not be promoted");

        publisher.Events.Should().NotContain(e => e.Type == "TENANT.PROVISIONING_REQUESTED",
            "no event emission for the shared-infra default — keeps live signups working");
    }

    [Test]
    public async Task VerifyEmail_MultipleOwnedTenants_FlipsAndEmitsForEach()
    {
        // A user can own multiple tenants (e.g. invited as owner to a
        // second org before completing verification). All owned tenants
        // in pending_verification must transition; tenants the user
        // merely belongs to but does not own stay untouched.
        var (user, rawToken) = await CreateUnverifiedUser("carol@example.com");
        var t1 = await CreateOwnedTenant(
            user.Id, $"c1-{Guid.NewGuid():N}".Substring(0, 12),
            status: "pending_verification");
        var t2 = await CreateOwnedTenant(
            user.Id, $"c2-{Guid.NewGuid():N}".Substring(0, 12),
            status: "pending_verification");
        // Third tenant: owned by SOMEONE ELSE but pending — must not be touched.
        var otherUser = await _userRepo.CreateAsync(new User
        {
            Email = "outsider@example.com",
            AuthMethod = "email",
        });
        var t3 = await CreateOwnedTenant(
            otherUser.Id, $"c3-{Guid.NewGuid():N}".Substring(0, 12),
            status: "pending_verification");

        var publisher = new RecordingPlatformEventPublisher();
        var ctx = ContextWithPublisher(publisher);

        await (await AuthEndpoints.VerifyEmail(
            new VerifyEmailRequest(rawToken),
            _userRepo, _loggerFactory, ctx)).ExecuteAsync(ctx);

        (await ReadStatus(t1.Id)).Should().Be("provisioning");
        (await ReadStatus(t2.Id)).Should().Be("provisioning");
        (await ReadStatus(t3.Id)).Should().Be("pending_verification",
            "outsider's tenant must remain untouched");

        publisher.Events.Where(e => e.Type == "TENANT.PROVISIONING_REQUESTED")
            .Select(e => e.TenantId)
            .Should().BeEquivalentTo(new[] { t1.Id, t2.Id });
    }

    [Test]
    public async Task VerifyEmail_AlreadyProvisioningTenant_DoesNotReEmit()
    {
        // Idempotency: if a tenant is already past pending_verification
        // (e.g. admin manually advanced it) we must NOT re-emit the
        // trigger — workflow retries are the admin path, not the
        // verify-email path.
        var (user, rawToken) = await CreateUnverifiedUser("dave@example.com");
        var tenant = await CreateOwnedTenant(
            user.Id, $"d-{Guid.NewGuid():N}".Substring(0, 12),
            status: "provisioning");

        var publisher = new RecordingPlatformEventPublisher();
        var ctx = ContextWithPublisher(publisher);

        await (await AuthEndpoints.VerifyEmail(
            new VerifyEmailRequest(rawToken),
            _userRepo, _loggerFactory, ctx)).ExecuteAsync(ctx);

        (await ReadStatus(tenant.Id)).Should().Be("provisioning",
            "no transition needed");
        publisher.Events.Should().NotContain(e => e.Type == "TENANT.PROVISIONING_REQUESTED",
            "no re-emission for tenants already moved past pending_verification");
    }

    [Test]
    public async Task VerifyEmail_PublisherUnavailable_StillSucceeds()
    {
        // Defense-in-depth: a missing IPlatformEventPublisher must not
        // break verify-email. Audit emission is best-effort; the
        // primary semantic (EmailVerified=true) is the user-visible
        // contract — Status transition is also best-effort because it
        // depends on the publisher being present (no point flipping
        // Status if no event ever fires).
        var (user, rawToken) = await CreateUnverifiedUser("eve@example.com");
        await CreateOwnedTenant(
            user.Id, $"e-{Guid.NewGuid():N}".Substring(0, 12),
            status: "pending_verification");

        // Build a context whose RequestServices resolves the framework
        // basics (ILoggerFactory for Results.Ok().ExecuteAsync) but
        // explicitly null-routes IPlatformEventPublisher AND has no
        // ControlPlaneDbContext — exercising the early-return path in
        // TryTriggerProvisioningForOwnedTenantsAsync.
        var sub = new ServiceCollection();
        sub.AddLogging();
        // Intentionally NOT registering IPlatformEventPublisher or
        // ControlPlaneDbContext / IServiceScopeFactory pathway to the
        // factory's container — the handler must early-return cleanly.
        var stripped = sub.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = stripped };
        ctx.Response.Body = new MemoryStream();

        var result = await AuthEndpoints.VerifyEmail(
            new VerifyEmailRequest(rawToken),
            _userRepo, _loggerFactory, ctx);
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        refreshed!.EmailVerified.Should().BeTrue();
    }
}
