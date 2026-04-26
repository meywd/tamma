using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Middleware;
using Tamma.Api.Services.Auth;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-R2 follow-up B — middleware unit tests.
///
/// <para>Confirms the three behaviours that make the middleware a
/// security gate rather than a passthrough: it validates the
/// <c>imp_id</c> claim against the audit table, fails closed on
/// stale/expired sessions, and stashes the verified id in
/// <see cref="HttpContext.Items"/> for downstream consumers.</para>
/// </summary>
[TestFixture]
public class ImpersonationContextMiddlewareTests
{
    private ControlPlaneDbContext _db = null!;
    private IJwtService _jwt = null!;
    private IConfiguration _config = null!;
    private FakeTimeProvider _time = null!;
    private IAdminImpersonationService _service = null!;

    private static readonly Guid OperatorId =
        Guid.Parse("12121212-3434-5656-7878-9a9a9a9a9a9a");

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
                ["Jwt:Issuer"] = "tamma",
                ["Jwt:Audience"] = "tamma-api",
                ["Tamma:Impersonation:MaxSessionMinutes"] = "60",
            })
            .Build();
        _jwt = new JwtService(_config);
        _time = new FakeTimeProvider(new DateTime(2026, 04, 26, 12, 0, 0, DateTimeKind.Utc));
        _service = new AdminImpersonationService(_db, _jwt, _config, _time);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static ClaimsPrincipal Operator()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "ops@tamma.dev"),
            new Claim("platformRole", "platform_admin"),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private async Task<Guid> SeedActiveSessionAsync()
    {
        // Operator + tenant rows the service insists on.
        _db.Users.Add(new User
        {
            Id = OperatorId,
            Email = "ops@tamma.dev",
            DisplayName = "Operator",
            AuthMethod = "email",
            Role = "owner",
            PlatformRole = "platform_admin",
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime,
        });
        var tenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Target",
            Slug = "target-" + tenantId.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync();

        var begin = await _service.BeginImpersonationAsync(
            Operator(), tenantId, null, "Test session", null, null);
        return begin.ImpersonationId;
    }

    /// <summary>
    /// Build the middleware around a sentinel "next" delegate so tests can
    /// confirm whether the pipeline was allowed to continue (200) or
    /// short-circuited (401).
    /// </summary>
    private (ImpersonationContextMiddleware mw, NextSentinel next) BuildMiddleware()
    {
        var sentinel = new NextSentinel();
        var mw = new ImpersonationContextMiddleware(sentinel.Invoke);
        return (mw, sentinel);
    }

    private sealed class NextSentinel
    {
        public bool Invoked { get; private set; }
        public Task Invoke(HttpContext ctx)
        {
            Invoked = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task NoImpIdClaim_FallsThrough_NextInvoked()
    {
        var (mw, next) = BuildMiddleware();
        var http = new DefaultHttpContext();
        http.User = Operator();

        await mw.InvokeAsync(http, _service, _config, _time, NullLogger<ImpersonationContextMiddleware>.Instance);

        next.Invoked.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Items.Should().NotContainKey(ImpersonationContextMiddleware.ImpersonationIdItem);
    }

    [Test]
    public async Task ActiveSession_StashesIdInHttpContext()
    {
        var impId = await SeedActiveSessionAsync();
        var (mw, next) = BuildMiddleware();
        var http = new DefaultHttpContext();
        http.User = WithImpClaim(impId);
        http.Response.Body = new MemoryStream();

        await mw.InvokeAsync(http, _service, _config, _time, NullLogger<ImpersonationContextMiddleware>.Instance);

        next.Invoked.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ImpersonationContextMiddleware.GetImpersonationId(http).Should().Be(impId);
        http.Response.Headers[ImpersonationContextMiddleware.ImpersonationHeader]
            .ToString().Should().Be(impId.ToString("D"));
    }

    [Test]
    public async Task ExpiredSession_BlocksAndReturns401()
    {
        var impId = await SeedActiveSessionAsync();
        var (mw, next) = BuildMiddleware();
        var http = new DefaultHttpContext();
        http.User = WithImpClaim(impId);
        http.Response.Body = new MemoryStream();
        // Advance past MaxSessionMinutes (60 + 1).
        _time.Advance(TimeSpan.FromMinutes(61));

        await mw.InvokeAsync(http, _service, _config, _time, NullLogger<ImpersonationContextMiddleware>.Instance);

        next.Invoked.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        // The middleware force-ends the row so the active list immediately
        // reflects reality.
        var row = await _db.AdminImpersonations.AsNoTracking()
            .FirstAsync(r => r.Id == impId);
        row.EndedAt.Should().NotBeNull();
        row.EndedReason.Should().Be("session_expired");
    }

    [Test]
    public async Task EndedSession_BlocksAndReturns401()
    {
        var impId = await SeedActiveSessionAsync();
        await _service.EndImpersonationAsync(impId, "explicit_exit");

        var (mw, next) = BuildMiddleware();
        var http = new DefaultHttpContext();
        http.User = WithImpClaim(impId);
        http.Response.Body = new MemoryStream();

        await mw.InvokeAsync(http, _service, _config, _time, NullLogger<ImpersonationContextMiddleware>.Instance);

        next.Invoked.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task MalformedImpIdClaim_BlocksAndReturns401()
    {
        var (mw, next) = BuildMiddleware();
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim("imp_id", "not-a-guid"),
        }, authenticationType: "Test");
        http.User = new ClaimsPrincipal(identity);
        http.Response.Body = new MemoryStream();

        await mw.InvokeAsync(http, _service, _config, _time, NullLogger<ImpersonationContextMiddleware>.Instance);

        next.Invoked.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static ClaimsPrincipal WithImpClaim(Guid impId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "ops@tamma.dev"),
            new Claim("platformRole", "platform_admin"),
            new Claim("imp_id", impId.ToString("D")),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Local fake clock — keeps the tests free of dependencies on the
    /// Microsoft.Extensions.TimeProvider.Testing package while still
    /// supporting <see cref="Advance"/>.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTime initialUtc)
        {
            _now = new DateTimeOffset(DateTime.SpecifyKind(initialUtc, DateTimeKind.Utc));
        }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
