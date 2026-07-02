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
using Tamma.Api.Services.Ci;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.Ci;

/// <summary>
/// Story 38 (Phase 1) — HTTP tests for the CI-mediation endpoints. Same engine-only
/// plane as <c>/api/v1/git/...</c>: missing/invalid bearer ⇒ 401, a user JWT ⇒ 403
/// (both BEFORE the handler); the acting tenant comes from <see cref="ITenantContext"/>
/// (never the body/route); the <c>ToHttpResult</c> mapping (200 / 200 success:false /
/// 403 / 503) holds.
/// </summary>
[TestFixture]
public class CiEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string TriggerRoute = "/api/v1/ci/acme/widgets/test-runs";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingCiMediationService _ci = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _ci = new CapturingCiMediationService();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICiMediationService>();
                services.AddSingleton<ICiMediationService>(_ci);

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

    private static object TriggerBody() => new { branch = "feature", correlationId = "wf-1" };

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ci.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ci.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_ValidBearer_ReconstructsRepo_DerivesTenantFromContext()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _ci.Next = new CiMediationResult { Success = true, Outcome = "Triggered", CredentialSource = "byok" };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _ci.LastRepo.Should().Be("acme/widgets");
        _ci.LastTenantId.Should().Be(tenant);
    }

    [Test]
    public async Task Post_ExpectedPlatformFailure_Returns200SuccessFalse()
    {
        _ci.Next = new CiMediationResult { Success = false, Outcome = "Error", FailureCode = CiFailureCodes.PlatformError, PlatformStatusCode = 403 };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)resp.StatusCode).Should().BeLessThan(500);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Post_RepoNotAuthorized_Returns403()
    {
        _ci.Next = new CiMediationResult { Success = false, FailureCode = CiFailureCodes.RepoNotAuthorized };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Post_TokenUnavailable_Returns503()
    {
        _ci.Next = new CiMediationResult { Success = false, FailureCode = CiFailureCodes.TokenUnavailable };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(TriggerRoute, TriggerBody());
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Get_BuildStatus_Success_Returns200()
    {
        _ci.Next = new CiMediationResult { Success = true, Outcome = "Read", BuildStatus = new CiBuildStatusDto { Status = "success" } };
        using var client = AuthedClient();
        var resp = await client.GetAsync("/api/v1/ci/acme/widgets/build-status?branch=feature&correlationId=wf-2");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _ci.LastRepo.Should().Be("acme/widgets");
    }

    private sealed class CapturingCiMediationService : ICiMediationService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastRepo { get; private set; }
        public CiMediationResult? Next { get; set; }

        public Task<CiMediationResult> TriggerTestsAsync(Guid? tenantId, string repo, TriggerTestsRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(Next ?? new CiMediationResult { Success = true, Outcome = "Triggered" });
        }

        public Task<CiMediationResult> GetBuildStatusAsync(Guid? tenantId, string repo, string branch, string correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(Next ?? new CiMediationResult { Success = true, Outcome = "Read" });
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
