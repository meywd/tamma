using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Middleware;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Middleware;

/// <summary>
/// Story 28-8 unit suite for <see cref="TenantContextMiddleware"/>. Covers:
/// <list type="bullet">
///   <item><description>Bypass-path short-circuit (no tenant resolution at all
///     for /api/v1/admin/*, /api/v1/auth/*, GitHub webhooks, health, etc.)</description></item>
///   <item><description>Tenant id resolution from JWT
///     <c>active_tenant_id</c> claim binds the
///     <see cref="ITenantContext"/> AND warms the per-tenant pool via
///     <see cref="ITenantConnectionResolver"/>.</description></item>
///   <item><description>Resolution from the <see cref="UserAuthPrincipal"/>
///     populated by the API-key handler.</description></item>
///   <item><description>Fallback to <c>users.tenant_id</c> when the JWT
///     carries no tenant claim.</description></item>
///   <item><description>Fail-fast 401 when the resolver throws
///     <see cref="TenantNotFoundException"/> (stale JWT pointing at a
///     deleted tenant).</description></item>
///   <item><description>Fail-fast 401 when the resolver throws
///     <see cref="TenantNotProvisionedException"/>.</description></item>
///   <item><description>OpenTelemetry baggage tag <c>tamma.tenant_id</c>
///     populated on the current activity.</description></item>
/// </list>
///
/// <para>The resolver is mocked because the real LRU pool builds an
/// <see cref="NpgsqlDataSource"/> — those have their own per-class test
/// suite under Epic28/. Here we only assert the middleware contract
/// (resolution + warm-call + scope binding + tracing).</para>
/// </summary>
[TestFixture]
public class TenantContextMiddlewareTests
{
    private Mock<ITenantConnectionResolver> _resolver = null!;
    private Mock<ITenantRepository> _tenantRepo = null!;
    private Mock<IUserRepository> _userRepo = null!;
    private TestTenantContext _tenantContext = null!;
    private bool _nextCalled;
    // Story 28-8 H7 — middleware now also takes the status cache and a
    // CP DbContext so it can gate non-active tenants with the proper
    // 503 / 424 / 410 / 404 status code instead of a blanket 401.
    private RecordingStatusCache _statusCache = null!;
    private ControlPlaneDbContext _controlPlane = null!;

    [SetUp]
    public void Setup()
    {
        _resolver = new Mock<ITenantConnectionResolver>(MockBehavior.Strict);
        _tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        _userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        _tenantContext = new TestTenantContext();
        _nextCalled = false;
        _statusCache = new RecordingStatusCache();
        var cpOpts = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _controlPlane = new ControlPlaneDbContext(cpOpts);
    }

    [TearDown]
    public void TearDown() => _controlPlane.Dispose();

    /// <summary>
    /// Recording fake for <see cref="ITenantStatusCache"/>. Captures
    /// every <c>Set</c>/<c>Invalidate</c> call so tests can assert the
    /// middleware populates the cache on cold-CP-read paths.
    /// </summary>
    private sealed class RecordingStatusCache : Tamma.Api.Services.TenantStatus.ITenantStatusCache
    {
        public Dictionary<Guid, string?> Entries { get; } = new();
        public List<Guid> Invalidations { get; } = new();
        public List<Guid> Reads { get; } = new();

        public bool TryGet(Guid tenantId, out string? status)
        {
            Reads.Add(tenantId);
            return Entries.TryGetValue(tenantId, out status);
        }

        public void Set(Guid tenantId, string? status) => Entries[tenantId] = status;

        public void Invalidate(Guid tenantId)
        {
            Invalidations.Add(tenantId);
            Entries.Remove(tenantId);
        }
    }

    /// <summary>
    /// Builds a default-bypass <see cref="HttpContext"/> with a memory
    /// response body and an authenticated principal carrying the supplied
    /// claims. Pass <paramref name="authenticated"/>=false to simulate an
    /// anonymous request (the middleware must defer to the next pipeline
    /// stage).
    /// </summary>
    private static DefaultHttpContext BuildContext(
        string path,
        IEnumerable<Claim>? claims = null,
        AuthPrincipal? principal = null,
        bool authenticated = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        if (authenticated)
        {
            var identity = new ClaimsIdentity(
                claims ?? Array.Empty<Claim>(),
                authenticationType: "Test");
            ctx.User = new ClaimsPrincipal(identity);
        }
        else
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        if (principal is not null)
            ctx.SetAuthPrincipal(principal);

        return ctx;
    }

    private TenantContextMiddleware BuildMiddleware()
    {
        return new TenantContextMiddleware(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });
    }

    private async Task InvokeAsync(HttpContext ctx)
    {
        var mw = BuildMiddleware();
        await mw.InvokeAsync(
            ctx,
            _tenantContext,
            _tenantRepo.Object,
            _userRepo.Object,
            _resolver.Object,
            _statusCache,
            _controlPlane,
            NullLogger<TenantContextMiddleware>.Instance);
    }

    /// <summary>
    /// Seed a tenant row into the in-memory CP context so the middleware's
    /// cold-path Status read returns the configured value. The middleware
    /// reads <c>EF.Property&lt;string?&gt;(t, "Status")</c>; setting that
    /// shadow property requires the InMemory provider's
    /// <c>EntityEntry.Property("Status")</c> hook.
    /// </summary>
    private async Task SeedTenantStatusAsync(Guid tenantId, string? status, bool deleted = false)
    {
        var tenant = new Tamma.Data.Entities.Tenant
        {
            Id = tenantId,
            Name = $"tenant-{tenantId:N}",
            Slug = $"slug-{tenantId:N}",
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        var entry = _controlPlane.Tenants.Add(tenant);
        entry.Property("Status").CurrentValue = status;
        await _controlPlane.SaveChangesAsync();
    }

    private static async Task<JsonDocument> ReadJsonBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return JsonDocument.Parse(body);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Bypass paths
    // ─────────────────────────────────────────────────────────────────────

    [TestCase("/api/health")]
    [TestCase("/api/v1/admin/users")]
    [TestCase("/api/admin/tenants/123/provisioning")]
    [TestCase("/api/v1/auth/login")]
    [TestCase("/api/v1/auth/password-reset/request")]
    [TestCase("/api/auth/github/callback")]
    [TestCase("/api/github/webhooks")]
    [TestCase("/api/convention-templates/typescript-node")]
    [TestCase("/health")]
    [TestCase("/swagger/index.html")]
    public async Task BypassPaths_ShortCircuit_NeverTouchResolver(string path)
    {
        var ctx = BuildContext(path, claims: new[]
        {
            // Even with a real-looking claim, bypass paths must not consult
            // the resolver — that's the whole point.
            new Claim("active_tenant_id", Guid.NewGuid().ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeTrue("bypass paths must invoke the next delegate");
        _tenantContext.TenantId.Should().BeNull("bypass paths must not bind a tenant");
        _resolver.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UnauthenticatedRequest_DefersToNext_WithoutResolution()
    {
        var ctx = BuildContext("/api/v1/issues", authenticated: false);

        await InvokeAsync(ctx);

        _nextCalled.Should().BeTrue();
        _tenantContext.TenantId.Should().BeNull();
        _resolver.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NoTenantClaimAndNoUserRow_DefersToNext_NoResolverCall()
    {
        var userId = Guid.NewGuid();
        _userRepo.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync((User?)null);

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeTrue("the personal-tenant bootstrap middleware owns this case");
        _tenantContext.TenantId.Should().BeNull();
        _resolver.VerifyNoOtherCalls();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Happy-path resolution + pool warming
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task JwtActiveTenantClaim_BindsContextAndWarmsPool()
    {
        var tenantId = Guid.NewGuid();
        // H7 — middleware now consults the status cache before the
        // resolver. Pre-seed "active" so the test stays focused on the
        // resolver contract rather than the CP-read path.
        _statusCache.Entries[tenantId] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeTrue();
        _tenantContext.TenantId.Should().Be(tenantId);
        _resolver.Verify(
            r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()),
            Times.Once,
            "middleware must pre-warm the per-tenant pool before the handler runs");
    }

    [Test]
    public async Task LegacyTenantIdClaim_StillResolves_WhenActiveTenantIdAbsent()
    {
        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("tenantId", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _tenantContext.TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task UserAuthPrincipal_TakesPrecedenceOverJwtClaim()
    {
        // API-key tenant wins even when the JWT carries a different
        // active_tenant_id (defensive — should never happen in practice but
        // the principal source is more authoritative than user-controlled
        // bearer claims).
        var apiKeyTenant = Guid.NewGuid();
        var jwtTenant = Guid.NewGuid();
        _statusCache.Entries[apiKeyTenant] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(apiKeyTenant, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext(
            "/api/v1/issues",
            claims: new[] { new Claim("active_tenant_id", jwtTenant.ToString()) },
            principal: new UserAuthPrincipal(
                KeyId: Guid.NewGuid(),
                UserId: Guid.NewGuid(),
                Role: "owner",
                TenantId: apiKeyTenant));

        await InvokeAsync(ctx);

        _tenantContext.TenantId.Should().Be(apiKeyTenant);
        _resolver.Verify(
            r => r.GetDataSourceAsync(apiKeyTenant, It.IsAny<CancellationToken>()),
            Times.Once);
        _resolver.Verify(
            r => r.GetDataSourceAsync(jwtTenant, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UserRowFallback_BindsContext_WhenJwtCarriesNoTenantClaim()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "active";
        _userRepo
            .Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "user@example.test",
                AuthMethod = "email",
                TenantId = tenantId,
            });
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        });

        await InvokeAsync(ctx);

        _tenantContext.TenantId.Should().Be(tenantId);
        _resolver.Verify(
            r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Fail-fast on resolver errors
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TenantNotFound_Returns404_AndAbortsPipeline()
    {
        // H7 — when the cold-CP read finds no row at all (stale JWT
        // pointing at a vanished tenant), the middleware now returns
        // 404 + tenant_not_found instead of a generic 401.
        var tenantId = Guid.NewGuid();

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeFalse("a missing tenant must short-circuit the pipeline");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ctx.Response.ContentType.Should().StartWith("application/json");

        var body = await ReadJsonBodyAsync(ctx);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("tenant_not_found");

        _tenantContext.TenantId.Should().BeNull(
            "context must not be bound when tenant resolution failed");

        // Cold-path also caches the not-found marker so a flood of
        // probes from the same stale token doesn't hammer CP.
        _statusCache.Entries.Should().ContainKey(tenantId);
    }

    [Test]
    public async Task TenantStatusProvisioning_Returns503_FromColdPathRead()
    {
        // H7 — provisioning state surfaces as 503 + Retry-After: 5.
        var tenantId = Guid.NewGuid();
        await SeedTenantStatusAsync(tenantId, "provisioning");

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers["Retry-After"].ToString().Should().Be("5");

        var body = await ReadJsonBodyAsync(ctx);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("tenant_not_ready");
        body.RootElement.GetProperty("status").GetString()
            .Should().Be("provisioning");

        // Status cache populated so the next request short-circuits
        // BEFORE re-reading CP.
        _statusCache.Entries[tenantId].Should().Be("provisioning");
        _resolver.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TenantStatusFailed_Returns424_FromColdPathRead()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantStatusAsync(tenantId, "failed");

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        var body = await ReadJsonBodyAsync(ctx);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("tenant_provisioning_failed");
    }

    [Test]
    public async Task TenantStatusDeleted_Returns410_FromColdPathRead()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantStatusAsync(tenantId, "deleted", deleted: true);

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        var body = await ReadJsonBodyAsync(ctx);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("tenant_deleted");
    }

    [Test]
    public async Task CachedNonActiveStatus_Returns503_WithoutCpReadOrResolverCall()
    {
        // H7 — the whole point of the cache: a hit for a non-active
        // value means we skip both the CP read AND the resolver call.
        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "deleting";

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var body = await ReadJsonBodyAsync(ctx);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be("tenant_deleting");

        _resolver.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CachedActiveStatus_DoesNotQueryCp_ButCallsResolver()
    {
        // H7 — cache hit + active value: skip the CP read but proceed
        // to the resolver warm-up (which is the per-tenant pool).
        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _nextCalled.Should().BeTrue();
        _tenantContext.TenantId.Should().Be(tenantId);
        // Cold-CP-read marker — _statusCache.Reads only records TryGet
        // calls, so the entry stays unchanged after a hit.
        _statusCache.Reads.Should().Contain(tenantId);
        _statusCache.Entries[tenantId].Should().Be("active",
            "cache value must not be re-written on a hot-path hit");
        _resolver.Verify(
            r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Tracing baggage
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolvedTenant_AddsBaggageToCurrentActivity()
    {
        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        // Spin up a listener so Activity.Current is non-null inside the
        // middleware. Without this, .NET silently drops Activity creation
        // because nothing listens — the middleware has to handle that case
        // too (covered by the JwtActiveTenantClaim happy-path test which
        // runs without a listener).
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Tamma.Tests.TenantContextMiddleware");
        using var activity = source.StartActivity("test-request");
        activity.Should().NotBeNull("listener should permit activity creation");

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        await InvokeAsync(ctx);

        _tenantContext.TenantId.Should().Be(tenantId);

        var baggage = activity!.GetBaggageItem(TenantContextMiddleware.TenantBaggageKey);
        baggage.Should().Be(tenantId.ToString(),
            "middleware must stamp the tenant id into Activity baggage for tracing");

        var tag = activity.GetTagItem(TenantContextMiddleware.TenantBaggageKey)?.ToString();
        tag.Should().Be(tenantId.ToString(),
            "the duplicate tag is what in-process consumers (Serilog enrichers, ETW) read");
    }

    [Test]
    public async Task NoActivity_DoesNotThrow_WhenResolutionSucceeds()
    {
        // Defensive: no listener means Activity.Current is null. Middleware
        // must not blow up in that case (it's the unit-test default).
        Activity.Current.Should().BeNull();

        var tenantId = Guid.NewGuid();
        _statusCache.Entries[tenantId] = "active";
        var ds = StubDataSource();
        _resolver
            .Setup(r => r.GetDataSourceAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<NpgsqlDataSource>(ds));

        var ctx = BuildContext("/api/v1/issues", claims: new[]
        {
            new Claim("active_tenant_id", tenantId.ToString()),
        });

        var act = async () => await InvokeAsync(ctx);
        await act.Should().NotThrowAsync();
        _tenantContext.TenantId.Should().Be(tenantId);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cheap throwaway data source. Tests never open a connection through
    /// it — they only assert that the middleware calls the resolver, so
    /// the host string never needs to be reachable.
    /// </summary>
    private static NpgsqlDataSource StubDataSource()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "stub.invalid",
            Port = 5432,
            Database = "t",
            Username = "u",
            Password = "p",
        };
        return NpgsqlDataSource.Create(builder);
    }

    /// <summary>
    /// Hand-rolled <see cref="ITenantContext"/> stub — Moq with strict
    /// behavior gets noisy when both Get and Set are exercised. This
    /// makes the assertion (<c>TenantId.Should().Be(...)</c>) read more
    /// naturally than verifying a Set call on a mock.
    /// </summary>
    private sealed class TestTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; }

        public void SetTenantId(Guid tenantId) => TenantId = tenantId;

        public void ClearTenantId() => TenantId = null;
    }
}
