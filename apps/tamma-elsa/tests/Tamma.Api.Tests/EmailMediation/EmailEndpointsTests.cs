using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.EmailMediation;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) — HTTP tests for the email-mediation endpoint
/// (<c>POST /api/v1/notifications/email</c>). Same engine-only plane as the Slack
/// notification: missing/invalid bearer ⇒ 401, a user JWT ⇒ 403; the acting tenant
/// comes from <see cref="ITenantContext"/>; a fail-soft result rides inside 200
/// success:false (never a raw 5xx).
/// </summary>
[TestFixture]
public class EmailEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string Route = "/api/v1/notifications/email";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingEmailMediationService _email = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _email = new CapturingEmailMediationService();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailMediationService>();
                services.AddSingleton<IEmailMediationService>(_email);

                services.RemoveAll<ITenantContext>();
                services.AddScoped<ITenantContext>(_ => _tenantContext);

                services.AddAuthentication(TestEngineAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestEngineAuthHandler>(TestEngineAuthHandler.SchemeName, _ => { });

                services.AddHttpContextAccessor();
                services.AddSingleton<IAuthorizationHandler, ServicePrincipalHandler>();

                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName)
                        .RequireAuthenticatedUser().Build();
                    options.AddPolicy("EngineServiceOnly", p =>
                    {
                        p.AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName);
                        p.RequireAuthenticatedUser();
                        p.AddRequirements(new ServicePrincipalRequirement());
                    });
                });
            });
        });
    }

    [TearDown]
    public void TearDown() => _factory?.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestBearer);
        return client;
    }

    private static object EmailBody() => new { to = "dev@example.com", subject = "s", body = "b", correlationId = "wf-1" };

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(Route, EmailBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _email.LastTo.Should().BeNull();
    }

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PostAsJsonAsync(Route, EmailBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _email.LastTo.Should().BeNull();
    }

    [Test]
    public async Task Post_ValidBearer_DerivesTenant_Returns200()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _email.Next = new EmailMediationResult { Success = true, Outcome = "Queued", TxnId = Guid.NewGuid() };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        var resp = await client.PostAsJsonAsync(Route, EmailBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _email.LastTo.Should().Be("dev@example.com");
        _email.LastTenantId.Should().Be(tenant);
    }

    [Test]
    public async Task Post_FailSoft_Returns200SuccessFalse_NeverRaw5xx()
    {
        _email.Next = new EmailMediationResult { Success = false, Outcome = "Error", FailureCode = EmailMediationFailureCodes.PlatformError };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, EmailBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)resp.StatusCode).Should().BeLessThan(500);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    private sealed class CapturingEmailMediationService : IEmailMediationService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastTo { get; private set; }
        public EmailMediationResult? Next { get; set; }

        public Task<EmailMediationResult> SendEmailAsync(Guid? tenantId, SendEmailRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastTo = body.To;
            return Task.FromResult(Next ?? new EmailMediationResult { Success = true, Outcome = "Queued" });
        }
    }

    private sealed class StubTenantContext : ITenantContext
    {
        private Guid? _tenantId;
        public Guid? TenantId => _tenantId;
        public void SetTenantId(Guid tenantId) => _tenantId = tenantId;
        public void ClearTenantId() => _tenantId = null;
    }

    private sealed class TestEngineAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestEngine";

        public TestEngineAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
                return Task.FromResult(AuthenticateResult.NoResult());
            var value = header.ToString();
            if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());
            var token = value["Bearer ".Length..].Trim();

            if (string.Equals(token, TestBearer, StringComparison.Ordinal))
            {
                Context.SetAuthPrincipal(new ServiceAuthPrincipal(
                    KeyId: Guid.NewGuid(), ServiceName: "tamma-engine", Permissions: Array.Empty<string>(), TenantId: null));
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "tamma-engine"), new Claim("scope", "service") }, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            if (string.Equals(token, UserBearer, StringComparison.Ordinal))
            {
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("role", "owner") }, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }
    }
}
