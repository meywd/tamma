using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Middleware;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Middleware;

/// <summary>
/// Pins the contract for <see cref="ProxyHeaderAuthMiddleware"/>: the
/// bridge from oauth2-proxy session → tamma_session JWT.
///
/// Critically, includes the regression test for the bug that shipped
/// when the legacy /api/auth/github flow was retired:
///   • A user signed in at oauth2-proxy lands on app.tamma.dev with
///     _oauth2_proxy cookie set, but no tamma_session.
///   • The dashboard polls /api/auth/me which requires AuthenticatedAny.
///   • Without this middleware, every poll returns 401 and the dashboard
///     loops back to the login page → "Sign in does nothing."
/// The "MintsJwtFromProxyCookie" test asserts that the bridge fires and
/// produces a tamma_session response cookie + an authenticated User.
/// </summary>
[TestFixture]
public class ProxyHeaderAuthMiddlewareTests
{
    private const string ValidProxyCookieValue = "fake-proxy-cookie-value";

    private Mock<IUserRepository> _userRepo = null!;
    private Mock<ITenantRepository> _tenantRepo = null!;
    private Mock<ITenantMembershipRepository> _membershipRepo = null!;
    private Mock<IPlatformBootstrapRepository> _bootstrapRepo = null!;
    private Mock<IJwtService> _jwt = null!;
    private FakeHttpMessageHandler _handler = null!;
    private IHttpClientFactory _clientFactory = null!;

    [SetUp]
    public void Setup()
    {
        _userRepo = new Mock<IUserRepository>(MockBehavior.Loose);
        _tenantRepo = new Mock<ITenantRepository>(MockBehavior.Loose);
        _membershipRepo = new Mock<ITenantMembershipRepository>(MockBehavior.Loose);
        _bootstrapRepo = new Mock<IPlatformBootstrapRepository>(MockBehavior.Loose);
        _jwt = new Mock<IJwtService>(MockBehavior.Loose);
        _handler = new FakeHttpMessageHandler();
        _clientFactory = new SingleHandlerClientFactory(_handler);

        _membershipRepo
            .Setup(m => m.GetUserTenantsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<TenantMembership>());
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    /// <summary>
    /// The bridge is for unauthenticated requests only — JWT and API-key
    /// callers must short-circuit so the middleware adds zero overhead and
    /// does not double-mint cookies.
    /// </summary>
    [Test]
    public async Task SkipsWhenAlreadyAuthenticated()
    {
        var ctx = BuildContext(authenticated: true, proxyCookie: ValidProxyCookieValue);
        var middleware = BuildMiddleware();
        var nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.Response.Headers.SetCookie.ToString().Should().NotContain("tamma_session=",
            "the bridge must not mint a JWT for already-authenticated callers");
        _jwt.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A CLI / public-endpoint call that has neither tamma_session nor the
    /// proxy cookie should pass through untouched. This is the "optional"
    /// mode — anonymous endpoints stay anonymous, authenticated endpoints
    /// 401 from the downstream RequireAuthorization filter.
    /// </summary>
    [Test]
    public async Task PassesThroughWhenNoProxyCookie()
    {
        var ctx = BuildContext(authenticated: false, proxyCookie: null);
        var middleware = BuildMiddleware();
        var nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.User.Identity?.IsAuthenticated.Should().BeFalse();
        _handler.RequestCount.Should().Be(0, "no proxy cookie ⇒ no userinfo round-trip");
    }

    /// <summary>
    /// The headline regression test. With a _oauth2_proxy cookie present
    /// and oauth2-proxy returning a valid userinfo response, the
    /// middleware MUST:
    ///   1. Look up (or create) the user by email
    ///   2. Generate a tamma_session JWT
    ///   3. Set the cookie on the response so the next request skips here
    ///   4. Authenticate the current request so the immediately-following
    ///      MapGet("/api/auth/me").RequireAuthorization succeeds
    /// If any of these steps fail this test fails — and the prod symptom
    /// is "Sign in with GitHub does nothing" / 401 loop.
    /// </summary>
    [Test]
    public async Task MintsJwtFromProxyCookie_ForExistingUser()
    {
        var existingUser = new User
        {
            Id = Guid.NewGuid(),
            Email = "alice@example.com",
            DisplayName = "alice",
            AuthMethod = "github",
            EmailVerified = true,
            Role = "member",
            PlatformRole = "user",
        };
        _userRepo.Setup(r => r.GetByEmailAsync("alice@example.com")).ReturnsAsync(existingUser);
        _jwt.Setup(j => j.GenerateAccessToken(
                It.IsAny<User>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<TenantClaim>?>(), It.IsAny<Guid?>()))
            .Returns(BuildFakeJwt());

        _handler.NextResponse = (HttpStatusCode.OK, """{"user":"alice","email":"alice@example.com"}""");

        var ctx = BuildContext(authenticated: false, proxyCookie: ValidProxyCookieValue);
        var middleware = BuildMiddleware();
        var nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.Response.Headers.SetCookie.ToString().Should().Contain("tamma_session=",
            "bridge must mint a session cookie for the next request");
        ctx.User.Identity?.IsAuthenticated.Should().BeTrue(
            "the current request must see the authenticated identity so RequireAuthorization passes");
        _userRepo.Verify(r => r.UpdateLastActiveAsync(existingUser.Id), Times.Once);
        // No new tenant should be created for an existing user.
        _tenantRepo.Verify(t => t.CreateAsync(It.IsAny<Tenant>()), Times.Never);
    }

    /// <summary>
    /// When oauth2-proxy rejects the cookie (401 / 403), the bridge must
    /// swallow and continue — the request stays anonymous and the
    /// downstream filter handles authorization. We don't want a transient
    /// proxy-side error to surface as a Tamma.Api 5xx.
    /// </summary>
    [Test]
    public async Task PassesThroughWhenProxyRejectsCookie()
    {
        _handler.NextResponse = (HttpStatusCode.Unauthorized, "");

        var ctx = BuildContext(authenticated: false, proxyCookie: ValidProxyCookieValue);
        var middleware = BuildMiddleware();
        var nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.User.Identity?.IsAuthenticated.Should().BeFalse();
        ctx.Response.Headers.SetCookie.ToString().Should().NotContain("tamma_session=");
    }

    /// <summary>
    /// Network-level failures (oauth2-proxy unreachable, timeout, malformed
    /// JSON, etc.) get swallowed too. Same rationale as the 401 case: the
    /// downstream filter is the source of truth for "is this request
    /// authorized," and a transient proxy hiccup must not 500 user
    /// requests.
    /// </summary>
    [Test]
    public async Task SwallowsNetworkErrorAndPassesThrough()
    {
        _handler.ThrowOnRequest = new HttpRequestException("connection refused");

        var ctx = BuildContext(authenticated: false, proxyCookie: ValidProxyCookieValue);
        var middleware = BuildMiddleware();
        var nextCalled = false;

        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.User.Identity?.IsAuthenticated.Should().BeFalse();
    }

    /// <summary>
    /// CodeQL #104 — ProxyHeaderAuthMiddleware.cs:108 logs the
    /// attacker-controlled <c>context.Request.Path</c>. <c>PathString</c>
    /// URI-encodes CR/LF so raw injection is already blocked, but the
    /// codebase convention is to route user input through
    /// <see cref="Tamma.Core.Logging.LogSanitizer"/> for defense-in-depth
    /// and to clear the static-analysis finding. The observable,
    /// non-fakeable signal that the sanitizer is actually applied is its
    /// 200-char truncation marker, which <c>PathString.ToUriComponent()</c>
    /// does not produce.
    /// </summary>
    [Test]
    public async Task LogsSanitizedRequestPath_TruncatesOverlongUserPath()
    {
        _handler.NextResponse = (HttpStatusCode.Unauthorized, "");

        var log = new CapturingLogger();
        var ctx = BuildContext(authenticated: false, proxyCookie: ValidProxyCookieValue);
        ctx.Request.Path = new PathString("/" + new string('a', 400));

        var middleware = BuildMiddleware(log);

        await middleware.InvokeAsync(ctx, _ => Task.CompletedTask);

        var pathLog = log.Messages.Should().ContainSingle(
            m => m.Contains("Proxy bridge: invoked for")).Subject;
        pathLog.Should().Contain("…[truncated]",
            "the request path is user-controlled and must pass through LogSanitizer.Clean before logging (CWE-117)");
    }

    private sealed class CapturingLogger : ILogger<ProxyHeaderAuthMiddleware>
    {
        public List<string> Messages { get; } = new();
        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private ProxyHeaderAuthMiddleware BuildMiddleware(
        ILogger<ProxyHeaderAuthMiddleware>? log = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth2Proxy:Url"] = "http://oauth2-proxy:4180",
                ["Cookie:Domain"] = ".tamma.dev",
            })
            .Build();
        return new ProxyHeaderAuthMiddleware(
            _clientFactory,
            config,
            _userRepo.Object,
            _tenantRepo.Object,
            _membershipRepo.Object,
            _bootstrapRepo.Object,
            _jwt.Object,
            log ?? NullLogger<ProxyHeaderAuthMiddleware>.Instance);
    }

    private static DefaultHttpContext BuildContext(bool authenticated, string? proxyCookie)
    {
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        if (proxyCookie is not null)
        {
            ctx.Request.Headers.Cookie = $"_oauth2_proxy={proxyCookie}";
        }
        if (authenticated)
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim("sub", Guid.NewGuid().ToString()) },
                JwtBearerDefaults.AuthenticationScheme);
            ctx.User = new ClaimsPrincipal(identity);
        }
        return ctx;
    }

    private static string BuildFakeJwt()
    {
        // A minimal, syntactically-valid JWT: header.payload.sig (no real
        // signing). The middleware's BuildPrincipalFromJwt only does a
        // non-validating ReadJwtToken so this is enough.
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('=');
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"sub":"{{Guid.NewGuid()}}","email":"alice@example.com","exp":{{DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()}}}""")).TrimEnd('=');
        return $"{header}.{payload}.sig";
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) NextResponse { get; set; } = (HttpStatusCode.OK, "{}");
        public Exception? ThrowOnRequest { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (ThrowOnRequest is not null) throw ThrowOnRequest;
            return Task.FromResult(new HttpResponseMessage(NextResponse.Status)
            {
                Content = new StringContent(NextResponse.Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleHandlerClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
