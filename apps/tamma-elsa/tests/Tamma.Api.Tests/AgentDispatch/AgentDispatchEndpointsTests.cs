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
using Tamma.Api.Services.AgentDispatch;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 (AC1/AC6) — HTTP tests for the agent-dispatch mediation endpoints.
/// Same engine-only plane as /api/v1/git and /api/v1/llm/call: a missing/invalid
/// bearer ⇒ 401, a non-engine (user) principal ⇒ 403 — both BEFORE the handler.
/// The composition (IAgentDispatchMediationService) is a capturing fake so these
/// tests exercise ONLY the endpoint: auth, the auth-derived tenant scope,
/// {owner}/{repo} reconstruction, and the ToHttpResult status mapping.
/// </summary>
[TestFixture]
public class AgentDispatchEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string RunsRoute = "/api/v1/agent-dispatch/acme/widgets/runs";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingMediationService _svc = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _svc = new CapturingMediationService();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentDispatchMediationService>();
                services.AddSingleton<IAgentDispatchMediationService>(_svc);

                services.RemoveAll<ITenantContext>();
                services.AddScoped<ITenantContext>(_ => _tenantContext);

                services.AddAuthentication(TestEngineAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestEngineAuthHandler>(
                        TestEngineAuthHandler.SchemeName, _ => { });

                services.AddHttpContextAccessor();
                services.AddSingleton<IAuthorizationHandler, ServicePrincipalHandler>();

                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
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

    private static object DispatchBody() => new
    {
        workflowFileName = "tamma-agent.yml",
        @ref = "tamma/issue-7",
        inputs = new Dictionary<string, string> { ["issue_number"] = "7" },
        correlationId = "wf-1",
    };

    // ── AC1 — auth ──────────────────────────────────────────────────────

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _svc.LastRepo.Should().BeNull("the handler must not run when the bearer is missing");
    }

    [Test]
    public async Task Post_InvalidBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _svc.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _svc.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_ValidBearer_ReconstructsRepo_And_DerivesTenantFromContext()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _svc.NextDispatch = new AgentDispatchRunResult { Success = true, CredentialSource = "installation", DispatchedAt = DateTime.UtcNow };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _svc.LastRepo.Should().Be("acme/widgets", "{owner}/{repo} is reconstructed from the two route segments");
        _svc.LastTenantId.Should().Be(tenant, "the acting tenant comes from ITenantContext, not the body");
    }

    // ── AC6 — status mapping ────────────────────────────────────────────

    [Test]
    public async Task Post_Success_Returns200()
    {
        _svc.NextDispatch = new AgentDispatchRunResult { Success = true, CredentialSource = "installation", DispatchedAt = DateTime.UtcNow };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("credentialSource").GetString().Should().Be("installation");
    }

    [Test]
    public async Task Post_ExpectedPlatformFailure_Returns200SuccessFalse_WithPreservedStatus()
    {
        _svc.NextDispatch = new AgentDispatchRunResult
        {
            Success = false, FailureCode = AgentDispatchFailureCodes.DispatchRejected,
            FailureReason = "GitHub returned 403 for dispatch", PlatformStatusCode = 403,
            CredentialSource = "installation", DispatchedAt = DateTime.UtcNow,
        };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "an expected platform failure rides inside 200 success:false");
        ((int)resp.StatusCode).Should().BeLessThan(500, "a raw 5xx must NEVER leak");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("failureCode").GetString().Should().Be(AgentDispatchFailureCodes.DispatchRejected);
        body.GetProperty("platformStatusCode").GetInt32().Should().Be(403);
    }

    [Test]
    public async Task Post_RepoNotAuthorized_Returns403()
    {
        _svc.NextDispatch = new AgentDispatchRunResult
        { Success = false, FailureCode = AgentDispatchFailureCodes.RepoNotAuthorized, DispatchedAt = DateTime.UtcNow };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(RunsRoute, DispatchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("failureCode").GetString().Should().Be(AgentDispatchFailureCodes.RepoNotAuthorized);
    }

    [Test]
    public async Task Get_Run_Success_Returns200()
    {
        _svc.NextStatus = new AgentRunStatusResult
        { Success = true, Found = true, RunId = 55, Status = "completed", Conclusion = "success", CredentialSource = "installation" };
        using var client = AuthedClient();
        var resp = await client.GetAsync("/api/v1/agent-dispatch/acme/widgets/runs/55?correlationId=wf-2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _svc.LastRepo.Should().Be("acme/widgets");
        _svc.LastRunId.Should().Be(55);
    }

    // ── AC1 — auth on the GET routes too (review finding 7) ─────────────
    // discover / get-run / collect-results / installation are engine-only reads on the
    // same plane as POST /runs: a missing bearer ⇒ 401, a user principal ⇒ 403, BOTH
    // before the handler (the mediation service — a capturing fake — is never reached).

    private const string DiscoverRoute = "/api/v1/agent-dispatch/acme/widgets/runs?branch=tamma/issue-7&createdAfter=2026-06-30T12:00:00Z";
    private const string GetRunRoute = "/api/v1/agent-dispatch/acme/widgets/runs/55";
    private const string CollectRoute = "/api/v1/agent-dispatch/acme/widgets/runs/55/results?branch=tamma/issue-7&conclusion=success";
    private const string InstallationRoute = "/api/v1/agent-dispatch/acme/widgets/installation";

    [TestCase(DiscoverRoute)]
    [TestCase(GetRunRoute)]
    [TestCase(CollectRoute)]
    [TestCase(InstallationRoute)]
    public async Task Get_NoBearer_Returns401_HandlerNotReached(string route)
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(route);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _svc.LastRepo.Should().BeNull("the handler must not run when the bearer is missing");
    }

    [TestCase(DiscoverRoute)]
    [TestCase(GetRunRoute)]
    [TestCase(CollectRoute)]
    [TestCase(InstallationRoute)]
    public async Task Get_NonEnginePrincipal_Returns403_HandlerNotReached(string route)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.GetAsync(route);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _svc.LastRepo.Should().BeNull("a user principal is rejected before the handler");
    }

    [Test]
    public async Task Get_Discover_ValidBearer_ReachesHandler_WithReconstructedRepo()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _svc.NextStatus = new AgentRunStatusResult { Success = true, Found = false, CredentialSource = "installation" };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        var resp = await client.GetAsync(DiscoverRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _svc.LastRepo.Should().Be("acme/widgets", "the createdAfter string binds + parses without breaking the handler");
        _svc.LastTenantId.Should().Be(tenant);
    }

    // ── Test doubles ────────────────────────────────────────────────────

    private sealed class CapturingMediationService : IAgentDispatchMediationService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastRepo { get; private set; }
        public long? LastRunId { get; private set; }
        public AgentDispatchRunResult? NextDispatch { get; set; }
        public AgentRunStatusResult? NextStatus { get; set; }

        public Task<AgentDispatchRunResult> TriggerRunAsync(Guid? tenantId, string repo, DispatchAgentRunRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(NextDispatch ?? new AgentDispatchRunResult { Success = true, DispatchedAt = DateTime.UtcNow });
        }

        public Task<AgentRunStatusResult> DiscoverRunAsync(Guid? tenantId, string repo, string branch, DateTime createdAfter, string? correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(NextStatus ?? new AgentRunStatusResult { Success = true, Found = false });
        }

        public Task<AgentRunStatusResult> GetRunAsync(Guid? tenantId, string repo, long runId, string? correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo; LastRunId = runId;
            return Task.FromResult(NextStatus ?? new AgentRunStatusResult { Success = true, Found = false });
        }

        public Task<AgentRunResultsResult> CollectResultsAsync(Guid? tenantId, string repo, long runId, CollectAgentRunRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo; LastRunId = runId;
            return Task.FromResult(new AgentRunResultsResult { Success = true, AgentSuccess = true });
        }

        public Task<AgentInstallationResult> ResolveInstallationAsync(Guid? tenantId, string repo, string? correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(new AgentInstallationResult { Success = true, InstallationId = 100 });
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
                    KeyId: Guid.NewGuid(), ServiceName: "tamma-engine",
                    Permissions: Array.Empty<string>(), TenantId: null));
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "tamma-engine"), new Claim("scope", "service") },
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            if (string.Equals(token, UserBearer, StringComparison.Ordinal))
            {
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("role", "owner") },
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }
    }
}
