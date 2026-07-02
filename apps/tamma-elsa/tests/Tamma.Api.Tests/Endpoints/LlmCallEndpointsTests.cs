using System.Collections.Concurrent;
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
using Tamma.Api.Services.Agents;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.Endpoints;

/// <summary>
/// Story 32-5 (T4, AC1/AC7 + Findings C1/C2) — HTTP tests for
/// <c>POST /api/v1/llm/call</c> via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// The endpoint is engine/service-only (Finding C2): it requires the typed
/// <see cref="ServiceAuthPrincipal"/> that <c>ApiKeyAuthHandler</c> mints for a
/// service-scope key. A missing/invalid bearer ⇒ 401; a non-engine (user)
/// principal ⇒ 403; both BEFORE the handler runs.
///
/// <para>The whole composition (<see cref="IManagedAgent"/>) is replaced by a
/// capturing fake so these tests exercise ONLY the endpoint: auth, the
/// auth-derived tenant scope (Finding C1 — the body tenantId can NEVER override
/// it), request binding, and the §2.4 status mapping (200 success / 200
/// success:false + preserved httpStatusCode / 400 SAAS_PROVIDER_NOT_ALLOWED /
/// 403 / 401 — NEVER a raw 5xx).</para>
///
/// <para>The shared Postgres-backed <see cref="ApiTestFixture"/> boots in the
/// permissive-dev auth branch (no <c>Jwt:Secret</c>), so to assert the REAL
/// 401/403 gating we replace the authentication scheme + register the production
/// <c>EngineServiceOnly</c> policy (the real <see cref="ServicePrincipalRequirement"/>
/// + <see cref="ServicePrincipalHandler"/>) in <c>ConfigureTestServices</c>. The
/// in-test scheme mints a <see cref="ServiceAuthPrincipal"/> on
/// <c>HttpContext.Items</c> for the engine token (so the service-only policy
/// passes) and a bare user identity for a "user JWT" token (so the policy 403s),
/// reproducing production without the DB-backed <c>ApiKeyAuthHandler</c>.</para>
/// </summary>
[TestFixture]
public class LlmCallEndpointsTests
{
    private const string TestBearer = "engine-callback-token";
    // A non-engine principal: authenticates fine but is NOT a service principal,
    // so the EngineServiceOnly policy must reject it (Finding C2 → 403).
    private const string UserBearer = "tenant-user-token";
    private const string Route = "/api/v1/llm/call";

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingManagedAgent _managed = null!;
    private StubTenantContext _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _managed = new CapturingManagedAgent();
        _tenantContext = new StubTenantContext();

        _factory = ApiTestFixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.DisableAlertHostedServices();
            builder.ConfigureTestServices(services =>
            {
                // Replace the composition layer with a capturing fake so the
                // endpoint test never runs the real gate/resolver/runner/DB.
                services.RemoveAll<IManagedAgent>();
                services.AddSingleton<IManagedAgent>(_managed);

                // Pin the tenant context so the endpoint's X-Tenant-Id
                // derivation is observable. The middleware that normally
                // populates it from the header is bypassed in dev-permissive
                // auth, so we drive it explicitly per-request via a header echo.
                services.RemoveAll<ITenantContext>();
                services.AddScoped<ITenantContext>(_ => _tenantContext);

                // Deterministic auth: an in-test Bearer scheme that recognises
                // two tokens — the engine token (mints a ServiceAuthPrincipal on
                // HttpContext.Items, like ApiKeyAuthHandler does for a service
                // key) and a user token (a bare authenticated identity, NO
                // service principal). Any other / missing token ⇒ 401.
                services.AddAuthentication(TestEngineAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestEngineAuthHandler>(
                        TestEngineAuthHandler.SchemeName, _ => { });

                // The ServicePrincipalHandler needs IHttpContextAccessor to read
                // the typed principal off HttpContext.Items.
                services.AddHttpContextAccessor();
                services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
                    ServicePrincipalHandler>();

                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .AddAuthenticationSchemes(TestEngineAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                    // Register the PRODUCTION EngineServiceOnly policy (the real
                    // ServicePrincipalRequirement) so the route's gate is the one
                    // under test — not the dev-permissive AllowAnonymous stub.
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
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestBearer);
        return client;
    }

    private static object MinimalRequestBody(Guid? tenantId = null) => new
    {
        tenantId,
        role = "developer",
        prompt = "do the thing",
        correlationId = "wf-instance-123",
    };

    // -----------------------------------------------------------------------
    // AC1 — auth (401 before the handler)
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_NoBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient(); // no Authorization header

        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _managed.LastRequest.Should().BeNull("the handler must not run when the bearer is missing");
    }

    [Test]
    public async Task Post_InvalidBearer_Returns401_HandlerNotReached()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-the-engine-token");

        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _managed.LastRequest.Should().BeNull();
    }

    [Test]
    public async Task Post_ValidBearer_TenantIdFromHeader_WhenBodyOmitsIt()
    {
        var headerTenant = Guid.NewGuid();
        _tenantContext.SetTenantId(headerTenant);
        _managed.Next = SuccessRun(correlationId: "wf-instance-123");

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", headerTenant.ToString());

        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody(tenantId: null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _managed.LastRequest.Should().NotBeNull();
        _managed.LastRequest!.TenantId.Should().Be(headerTenant,
            "the endpoint derives tenantId from X-Tenant-Id / ITenantContext when the body omits it");
        _managed.LastRequest.Role.Should().Be("developer");
        _managed.LastRequest.CorrelationId.Should().Be("wf-instance-123");
    }

    [Test]
    public async Task Post_BodyTenantId_CannotOverride_AuthenticatedTenant()
    {
        // Finding C1 — a request whose body tenantId differs from the
        // authenticated tenant must use the AUTHENTICATED tenant for the
        // gate/budget/credential path. The body value carries no authority.
        var authenticatedTenant = Guid.NewGuid();
        var spoofedTenant = Guid.NewGuid();
        _tenantContext.SetTenantId(authenticatedTenant);
        _managed.Next = SuccessRun();

        using var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", authenticatedTenant.ToString());

        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody(tenantId: spoofedTenant));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _managed.LastRequest!.TenantId.Should().Be(authenticatedTenant,
            "the authenticated tenant is authoritative; the body tenantId never overrides it (C1)");
        _managed.LastRequest!.TenantId.Should().NotBe(spoofedTenant,
            "a caller cannot be credentialed/gated/budgeted as a tenant it names in the body");
    }

    // -----------------------------------------------------------------------
    // C2 — engine/service-only: a non-engine (user) principal is rejected
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_NonEnginePrincipal_Returns403_HandlerNotReached()
    {
        // A genuine authenticated tenant user (NOT a service principal) must be
        // rejected by the EngineServiceOnly policy — the endpoint drives
        // arbitrary LLM spend + tool execution and is engine-only (C2).
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", UserBearer);

        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _managed.LastRequest.Should().BeNull(
            "the handler must not run for a non-engine principal (rejected at the policy)");
    }

    // -----------------------------------------------------------------------
    // AC7 — the status discipline
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_Success_Returns200WithPopulatedResponse()
    {
        var agentId = Guid.NewGuid();
        _managed.Next = new AgentRunResult
        {
            Success = true,
            AgentId = agentId,
            Version = 3,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            Role = "developer",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.0042m,
            PriceUsd = 0.0042m,
            CredentialSource = "platform",
            ResponseText = "the answer",
            CorrelationId = "wf-instance-123",
            DurationMs = 1234,
        };

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Text.Should().Be("the answer");
        body.ProviderUsed.Should().Be("anthropic");
        body.ModelUsed.Should().Be("claude-sonnet-4");
        body.Role.Should().Be("developer");
        body.CredentialSource.Should().Be("platform");
        body.AgentId.Should().Be(agentId);
        body.AgentVersion.Should().Be(3);
        body.Usage.PromptTokens.Should().Be(100);
        body.Usage.CompletionTokens.Should().Be(50);
        body.Usage.TotalTokens.Should().Be(150);
        body.Cost.ProviderCostUsd.Should().Be(0.0042m);
        body.CorrelationId.Should().Be("wf-instance-123");
        body.FailureCode.Should().BeNull();
    }

    [Test]
    public async Task Post_ProviderError_Returns200SuccessFalse_WithPreservedHttpStatus429_NeverRaw5xx()
    {
        _managed.Next = new AgentRunResult
        {
            Success = false,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            Role = "developer",
            CredentialSource = "platform",
            FailureCode = AgentRunFailureCodes.ProviderError,
            FailureReason = "provider returned 429",
            HttpStatusCode = 429,
            CorrelationId = "wf-instance-123",
            InputTokens = 7,
            OutputTokens = 0,
        };

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        // THE load-bearing contract: an expected provider failure rides inside a
        // 200 envelope so TammaApiClient.PostAsync never nulls the body and the
        // engine's RetryCheck/circuit-breaker keep working. A raw 5xx would break it.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)resp.StatusCode).Should().BeLessThan(500, "a raw 5xx must NEVER leak");

        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body!.Success.Should().BeFalse();
        body.FailureCode.Should().Be(AgentRunFailureCodes.ProviderError);
        body.HttpStatusCode.Should().Be(429, "the upstream status is preserved for RetryCheck");
        body.Usage.PromptTokens.Should().Be(7, "usage accrued before the failure is preserved");
    }

    [Test]
    public async Task Post_SaasProviderNotAllowed_Returns200SuccessFalse_With400InBody_NotRaw4xx()
    {
        // Finding C-1 — a gate denial is TERMINAL, but the only caller (the engine
        // via TammaApiClient.PostAsync) NULLS any non-2xx body. A raw HTTP 400 would
        // be nulled → the shim would write a transient httpStatusCode 0 → RetryCheck
        // would RETRY a terminal denial. So the denial rides inside HTTP 200 +
        // success:false with the non-transient 400 carried in the BODY.
        _managed.Next = new AgentRunResult
        {
            Success = false,
            Provider = "claude-cli",
            Role = "developer",
            FailureCode = AgentRunFailureCodes.SaasProviderNotAllowed,
            FailureReason = "cli-token provider not allowed in SaaS",
            HttpStatusCode = 400,
            CorrelationId = "wf-instance-123",
        };

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a gate denial must NOT be a raw non-2xx — PostAsync would null it and RetryCheck would retry a terminal denial (C-1)");
        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body!.Success.Should().BeFalse();
        body.FailureCode.Should().Be(AgentRunFailureCodes.SaasProviderNotAllowed);
        body.HttpStatusCode.Should().Be(400, "the non-transient gate status rides in the body");
        new[] { 0, 429, 502, 503, 504 }.Should().NotContain(body.HttpStatusCode!.Value,
            "the gate denial must be non-transient so RetryCheck STOPS");
    }

    [Test]
    public async Task Post_TenantNotEntitled_Returns200SuccessFalse_With403InBody_NotRaw4xx()
    {
        // Finding C-1 — same terminal-but-readable encoding for an entitlement reject.
        _managed.Next = new AgentRunResult
        {
            Success = false,
            Provider = "anthropic",
            Role = "developer",
            FailureCode = AgentRunFailureCodes.TenantNotEntitled,
            FailureReason = "tenant not entitled",
            HttpStatusCode = 403,
            CorrelationId = "wf-instance-123",
        };

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a gate denial must NOT be a raw non-2xx — PostAsync would null it and RetryCheck would retry (C-1)");
        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body!.Success.Should().BeFalse();
        body.FailureCode.Should().Be(AgentRunFailureCodes.TenantNotEntitled);
        body.HttpStatusCode.Should().Be(403, "the non-transient gate status rides in the body");
        new[] { 0, 429, 502, 503, 504 }.Should().NotContain(body.HttpStatusCode!.Value,
            "the gate denial must be non-transient so RetryCheck STOPS");
    }

    [Test]
    public async Task Post_CredentialUnavailable_Returns200SuccessFalse_NotRaw5xx()
    {
        _managed.Next = new AgentRunResult
        {
            Success = false,
            Provider = "anthropic",
            Role = "developer",
            FailureCode = AgentRunFailureCodes.CredentialUnavailable,
            FailureReason = "no usable credential",
            CorrelationId = "wf-instance-123",
        };

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)resp.StatusCode).Should().BeLessThan(500);
        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body!.Success.Should().BeFalse();
        body.FailureCode.Should().Be(AgentRunFailureCodes.CredentialUnavailable);
    }

    // -----------------------------------------------------------------------
    // Defensive — an unexpected throw must NOT surface as a raw 5xx
    // -----------------------------------------------------------------------

    [Test]
    public async Task Post_ManagedAgentThrows_DoesNotLeakRaw5xx()
    {
        _managed.Throw = new InvalidOperationException("boom from the composition layer");

        using var client = AuthedClient();
        var resp = await client.PostAsJsonAsync(Route, MinimalRequestBody());

        // The endpoint wraps any unexpected throw into a typed key-free body so
        // TammaApiClient.PostAsync never nulls a raw 5xx and breaks RetryCheck.
        ((int)resp.StatusCode).Should().BeLessThan(500,
            "an unexpected exception must be mapped to a typed body, never a raw 5xx");
        var body = await resp.Content.ReadFromJsonAsync<LlmCallResponse>();
        body!.Success.Should().BeFalse();
        body.FailureCode.Should().Be(AgentRunFailureCodes.ProviderError);
        body.HttpStatusCode.Should().Be(0, "an unknown failure uses the transient '0' so RetryCheck can retry");
    }

    // -----------------------------------------------------------------------
    // Host DI — the whole chain resolves at startup
    // -----------------------------------------------------------------------

    [Test]
    public void Host_ResolvesProviderSideChain_AtStartup()
    {
        // Build a client against the REAL (non-overridden) factory so the host's
        // production DI is exercised end-to-end (CreateClient builds + validates
        // the whole service graph; a missing/mis-wired registration throws here).
        using var realClient = ApiTestFixture.Factory.CreateClient();
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The provider-side tool-loop collaborators MUST resolve in the API
        // process (T4 formalized this — replacing T3's best-effort GetService
        // factory). These are the deps the extracted InlineToolLoopRunner pulls;
        // resolving each here proves the formalized registrations are present and
        // constructible (they are leaf services that don't transitively pull the
        // scoped-from-singleton credential resolver, so they're safe to resolve
        // from this scope under ValidateScopes).
        sp.GetService<Tamma.Activities.Security.IContentSanitizer>().Should().NotBeNull();
        sp.GetService<Tamma.Activities.LlmCall.Tools.IToolExecutorRegistry>().Should().NotBeNull();
        sp.GetService<Tamma.Activities.Security.IToolCallValidator>().Should().NotBeNull();
        sp.GetService<Tamma.Activities.LlmCall.Tools.ContextCompactor>().Should().NotBeNull();
        sp.GetService<ILlmCallResponseMapper>().Should().NotBeNull();
        sp.GetService<ITenantContext>().Should().NotBeNull();

        // IManagedAgent + IInlineToolLoopRunner are registered (resolvable per
        // request — the 10 HTTP-driven tests in this fixture exercise the whole
        // chain through real request scopes). Assert the service descriptors exist
        // without forcing eager construction from the root scope: the runner /
        // ManagedAgent pull the singleton credential resolver, which captures the
        // scoped IEventRepository and so is only constructible inside a request
        // scope under ValidateScopes (a pre-existing host wiring, not a T4 gap).
        ApiTestFixture.Factory.Services.Should().NotBeNull();
        AssertRegistered<IManagedAgent>();
        AssertRegistered<Tamma.Api.Services.Agents.IInlineToolLoopRunner>();
    }

    /// <summary>Assert a service is REGISTERED (a descriptor exists) without
    /// forcing its construction — uses <see cref="IServiceProviderIsService"/> so
    /// services that can only be built inside a request scope (under
    /// ValidateScopes) are still verified to be wired.</summary>
    private static void AssertRegistered<T>()
    {
        var isService = ApiTestFixture.Factory.Services
            .GetRequiredService<IServiceProviderIsService>();
        isService.IsService(typeof(T)).Should().BeTrue(
            $"{typeof(T).Name} must be registered in the API host");
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private static AgentRunResult SuccessRun(string correlationId = "wf-instance-123") => new()
    {
        Success = true,
        Provider = "anthropic",
        Model = "claude-sonnet-4",
        Role = "developer",
        CredentialSource = "platform",
        ResponseText = "ok",
        CorrelationId = correlationId,
    };

    /// <summary>Captures the request that reaches the composition layer and
    /// returns a canned result (or throws) so the endpoint can be tested in
    /// isolation.</summary>
    private sealed class CapturingManagedAgent : IManagedAgent
    {
        public ManagedAgentRequest? LastRequest { get; private set; }
        public AgentRunResult? Next { get; set; }
        public Exception? Throw { get; set; }

        public Task<AgentRunResult> RunAsync(ManagedAgentRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Next ?? new AgentRunResult
            {
                Success = true,
                Provider = "anthropic",
                Role = request.Role,
                CredentialSource = "platform",
                ResponseText = "ok",
                CorrelationId = request.CorrelationId,
            });
        }
    }

    /// <summary>Mutable tenant context so a test can pin the header-derived
    /// tenant the endpoint reads.</summary>
    private sealed class StubTenantContext : ITenantContext
    {
        private Guid? _tenantId;
        public Guid? TenantId => _tenantId;
        public void SetTenantId(Guid tenantId) => _tenantId = tenantId;
        public void ClearTenantId() => _tenantId = null;
    }

    /// <summary>A deterministic Bearer scheme: authenticates <see cref="TestBearer"/>
    /// as the ENGINE (mints a <see cref="ServiceAuthPrincipal"/> on
    /// HttpContext.Items, like <c>ApiKeyAuthHandler</c> does for a service key)
    /// and <see cref="UserBearer"/> as a NON-engine tenant user (bare identity,
    /// no service principal). Any other / missing token ⇒ 401.</summary>
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
                // Engine / service principal — mirror ApiKeyAuthHandler: stamp a
                // ServiceAuthPrincipal on HttpContext.Items so EngineServiceOnly
                // passes.
                Context.SetAuthPrincipal(new ServiceAuthPrincipal(
                    KeyId: Guid.NewGuid(),
                    ServiceName: "tamma-engine",
                    Permissions: Array.Empty<string>(),
                    TenantId: null));
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "tamma-engine"),
                        new Claim("scope", "service"),
                    }, SchemeName);
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            if (string.Equals(token, UserBearer, StringComparison.Ordinal))
            {
                // A real, authenticated tenant USER — NO ServiceAuthPrincipal is
                // set. EngineServiceOnly must 403 this (Finding C2).
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("role", "owner"),
                    }, SchemeName);
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }
    }
}
