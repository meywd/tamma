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
using Tamma.Api.Services.Jira;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.Jira;

/// <summary>
/// Story 38 (Phase 1) — HTTP tests for the JIRA-mediation endpoints. Same engine-only
/// plane: missing/invalid bearer ⇒ 401, a user JWT ⇒ 403; the acting tenant comes
/// from <see cref="ITenantContext"/>; every non-success rides inside 200 success:false
/// (JIRA is not repo-scoped — never a 403/503 for it).
/// </summary>
[TestFixture]
public class JiraEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string TicketRoute = "/api/v1/jira/tickets/PROJ-42";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingJiraMediationService _jira = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _jira = new CapturingJiraMediationService();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJiraMediationService>();
                services.AddSingleton<IJiraMediationService>(_jira);

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

    [Test]
    public async Task Get_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(TicketRoute);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _jira.LastTicketId.Should().BeNull();
    }

    [Test]
    public async Task Patch_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PatchAsJsonAsync(TicketRoute, new { status = "Done", correlationId = "c" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _jira.LastTicketId.Should().BeNull();
    }

    [Test]
    public async Task Get_ValidBearer_DerivesTenant_Returns200()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _jira.Next = new JiraMediationResult { Success = true, Outcome = "Read", TicketKey = "PROJ-42" };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        var resp = await client.GetAsync(TicketRoute + "?correlationId=wf-1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _jira.LastTicketId.Should().Be("PROJ-42");
        _jira.LastTenantId.Should().Be(tenant);
    }

    [Test]
    public async Task Patch_Failure_Returns200SuccessFalse_NeverRaw5xx()
    {
        _jira.Next = new JiraMediationResult { Success = false, Outcome = "Error", FailureCode = JiraFailureCodes.NotConfigured };
        using var client = AuthedClient();
        var resp = await client.PatchAsJsonAsync(TicketRoute, new { status = "Done", correlationId = "c" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)resp.StatusCode).Should().BeLessThan(500);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("failureCode").GetString().Should().Be(JiraFailureCodes.NotConfigured);
    }

    private sealed class CapturingJiraMediationService : IJiraMediationService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastTicketId { get; private set; }
        public JiraMediationResult? Next { get; set; }

        public Task<JiraMediationResult> GetTicketAsync(Guid? tenantId, string ticketId, string correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastTicketId = ticketId;
            return Task.FromResult(Next ?? new JiraMediationResult { Success = true, Outcome = "Read" });
        }

        public Task<JiraMediationResult> UpdateTicketAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastTicketId = ticketId;
            return Task.FromResult(Next ?? new JiraMediationResult { Success = true, Outcome = "Updated" });
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
