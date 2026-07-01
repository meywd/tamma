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
using Tamma.Api.Services.Git;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.Git;

/// <summary>
/// Story 38-1 (AC1/AC6) — HTTP tests for the git-mediation endpoints via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. Same engine-only plane as
/// <c>/api/v1/llm/call</c>: a missing/invalid bearer ⇒ 401, a non-engine (user)
/// principal ⇒ 403 — both BEFORE the handler. The composition
/// (<see cref="IGitMediationService"/>) is a capturing fake so these tests
/// exercise ONLY the endpoint: auth, the auth-derived tenant scope (the body /
/// route never override it), {owner}/{repo} reconstruction, and the
/// <c>ToHttpResult</c> status mapping (200 / 200 success:false / 403 / 503).
/// </summary>
[TestFixture]
public class GitEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    private const string UserBearer = "tenant-user-token";
    private const string BranchesRoute = "/api/v1/git/acme/widgets/branches";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingGitMediationService _git = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _git = new CapturingGitMediationService();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitMediationService>();
                services.AddSingleton<IGitMediationService>(_git);

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

    private static object BranchBody() => new
    {
        branchName = "adl/7-thing",
        baseRef = "main",
        conflictStrategy = "suffix",
        issueNumber = 7,
        correlationId = "wf-1",
    };

    // -----------------------------------------------------------------------
    // AC1 — auth
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _git.LastRepo.Should().BeNull("the handler must not run when the bearer is missing");
    }

    [Test]
    public async Task Post_InvalidBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _git.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserBearer);
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _git.LastRepo.Should().BeNull();
    }

    [Test]
    public async Task Post_ValidBearer_ReconstructsRepo_And_DerivesTenantFromContext()
    {
        var tenant = Guid.NewGuid();
        _tenantContext.SetTenantId(tenant);
        _git.NextBranch = new GitMediationResult { Success = true, Outcome = "Created", BranchRef = "adl/7-thing", CredentialSource = "byok" };

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _git.LastRepo.Should().Be("acme/widgets", "{owner}/{repo} is reconstructed from the two route segments");
        _git.LastTenantId.Should().Be(tenant, "the acting tenant comes from ITenantContext (X-Tenant-Id), not the body");
    }

    // -----------------------------------------------------------------------
    // AC6 — status mapping
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_Success_Returns200()
    {
        _git.NextBranch = new GitMediationResult { Success = true, Outcome = "Created", BranchRef = "adl/7-thing", CredentialSource = "platform" };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("branchRef").GetString().Should().Be("adl/7-thing");
        body.GetProperty("credentialSource").GetString().Should().Be("platform");
    }

    [Test]
    public async Task Post_ExpectedPlatformFailure_Returns200SuccessFalse_WithPreservedStatus()
    {
        _git.NextBranch = new GitMediationResult
        {
            Success = false, Outcome = "Error",
            FailureCode = GitFailureCodes.GitConflict, FailureReason = "branch exists", PlatformStatusCode = 422,
            CredentialSource = "byok",
        };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "an expected platform failure rides inside 200 success:false");
        ((int)resp.StatusCode).Should().BeLessThan(500, "a raw 5xx must NEVER leak");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("failureCode").GetString().Should().Be(GitFailureCodes.GitConflict);
        body.GetProperty("platformStatusCode").GetInt32().Should().Be(422);
    }

    [Test]
    public async Task Post_RepoNotAuthorized_Returns403()
    {
        _git.NextBranch = new GitMediationResult { Success = false, Outcome = "Error", FailureCode = GitFailureCodes.RepoNotAuthorized };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("failureCode").GetString().Should().Be(GitFailureCodes.RepoNotAuthorized);
    }

    [Test]
    public async Task Post_TokenUnavailable_Returns503()
    {
        _git.NextBranch = new GitMediationResult { Success = false, Outcome = "Error", FailureCode = GitFailureCodes.TokenUnavailable };
        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(BranchesRoute, BranchBody());

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Get_Comments_Success_Returns200()
    {
        _git.NextComments = new GitMediationResult
        {
            Success = true, Outcome = "Done",
            Comments = new List<PrCommentDto> { new() { Id = 1, Body = "nit", Author = "a" } },
        };
        using var client = AuthedClient();
        var resp = await client.GetAsync("/api/v1/git/acme/widgets/pull-requests/9/comments?correlationId=wf-2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _git.LastRepo.Should().Be("acme/widgets");
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class CapturingGitMediationService : IGitMediationService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastRepo { get; private set; }
        public GitMediationResult? NextBranch { get; set; }
        public GitMediationResult? NextComments { get; set; }

        public Task<GitMediationResult> CreateBranchAsync(Guid? tenantId, string repo, CreateBranchRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(NextBranch ?? new GitMediationResult { Success = true, Outcome = "Created", BranchRef = body.BranchName });
        }

        public Task<GitMediationResult> CreatePullRequestAsync(Guid? tenantId, string repo, CreatePrRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(new GitMediationResult { Success = true, Outcome = "Created", PrNumber = 1 });
        }

        public Task<GitMediationResult> MergePullRequestAsync(Guid? tenantId, string repo, int prNumber, MergePrRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(new GitMediationResult { Success = true, Outcome = "Merged", Merged = true });
        }

        public Task<GitMediationResult> UpdateIssueAsync(Guid? tenantId, string repo, int issueNumber, UpdateIssueRequest body, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(new GitMediationResult { Success = true, Outcome = "Updated", IssueStatus = "updated" });
        }

        public Task<GitMediationResult> GetPullRequestCommentsAsync(Guid? tenantId, string repo, int prNumber, string correlationId, CancellationToken ct = default)
        {
            LastTenantId = tenantId; LastRepo = repo;
            return Task.FromResult(NextComments ?? new GitMediationResult { Success = true, Outcome = "Done", Comments = new List<PrCommentDto>() });
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
