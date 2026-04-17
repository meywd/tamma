using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.SaaS;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.SaaS;

/// <summary>
/// End-to-end HTTP tests for the SaaS-lane endpoints through the real
/// Postgres-backed <see cref="ApiTestFixture"/>. Verifies wiring
/// (DI + route + service + repository + DB) for each endpoint.
/// </summary>
/// <remarks>
/// Because <c>Program.cs</c> does not yet register <c>AddSaaSServices</c>
/// (per-task constraint), each test boots a dedicated
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// that does the registration inside <c>ConfigureTestServices</c>. The
/// <c>anthropic</c> HttpClient is overridden with a canned
/// <see cref="HttpMessageHandler"/> so the LLM proxy test is hermetic.
/// </remarks>
[TestFixture]
public class SaaSEndpointsIntegrationTests
{
    private static readonly object _factoryLock = new();
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _cachedFactory;
#pragma warning disable NUnit1032 // shared by all tests; disposed by OneTimeTearDown
    private static CannedHandler _activeHandler = new(HttpStatusCode.OK, "{}");
#pragma warning restore NUnit1032

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _activeHandler = new CannedHandler(HttpStatusCode.OK, "{}");
    }

    // ─── LLM proxy ──────────────────────────────────────────────────────────

    [Test]
    public async Task PostLlmChat_HappyPath_Returns200AndRecordsDiagnostic()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, """
            {
              "id": "msg_1",
              "model": "claude-sonnet-4.5",
              "content": [ { "type": "text", "text": "canned reply" } ],
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """);

        using var client = CreateClientWithCannedAnthropic(handler);
        var body = new
        {
            model = "claude-sonnet-4.5",
            messages = new[]
            {
                new { role = "user", content = "ping" }
            },
            maxTokens = 50
        };

        var resp = await client.PostAsJsonAsync("/api/v1/llm/chat", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("text").GetString().Should().Be("canned reply");
        json.GetProperty("usage").GetProperty("totalTokens").GetInt32().Should().Be(15);

        // A diagnostic row must have been persisted.
        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var diagnosticCount = await db.ProviderDiagnostics
            .IgnoreQueryFilters()
            .CountAsync(d => d.ProviderKey == "anthropic-claude");
        diagnosticCount.Should().Be(1);
    }

    [Test]
    public async Task PostLlmChat_EmptyMessages_Returns400()
    {
        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsJsonAsync("/api/v1/llm/chat", new
        {
            model = "claude-sonnet-4.5",
            messages = Array.Empty<object>()
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PostLlmChat_UpstreamError_Returns502AndRecordsFailureDiagnostic()
    {
        var handler = new CannedHandler(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
        using var client = CreateClientWithCannedAnthropic(handler);

        var resp = await client.PostAsJsonAsync("/api/v1/llm/chat", new
        {
            model = "claude-sonnet-4.5",
            messages = new[] { new { role = "user", content = "hi" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var failures = await db.ProviderDiagnostics
            .IgnoreQueryFilters()
            .Where(d => !d.Success)
            .CountAsync();
        failures.Should().Be(1);
    }

    // ─── Workflow status ────────────────────────────────────────────────────

    [Test]
    public async Task PostWorkflowStatus_HappyPath_Returns200AndPersistsVariables()
    {
        var (defId, instanceId) = await SeedWorkflowInstanceAsync();

        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/workflows/{instanceId}/status",
            new
            {
                status = "running",
                variables = new { progress = 50, message = "half-way" }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var instance = await db.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == instanceId);
        instance.Should().NotBeNull();
        instance!.Status.Should().Be("running");
        var vars = JsonDocument.Parse(instance.Variables).RootElement;
        vars.GetProperty("progress").GetInt32().Should().Be(50);
        vars.GetProperty("message").GetString().Should().Be("half-way");
    }

    [Test]
    public async Task PostWorkflowStatus_UnknownInstance_Returns404()
    {
        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/workflows/{Guid.NewGuid()}/status",
            new { status = "running" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Workflow result ────────────────────────────────────────────────────

    [Test]
    public async Task PostWorkflowResult_Success_MarksCompletedAndStoresResultAndEmitsEvent()
    {
        var (defId, instanceId) = await SeedWorkflowInstanceAsync();

        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/workflows/{instanceId}/result",
            new
            {
                status = "completed",
                result = new { prNumber = 42, duration = 1234 }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var instance = await db.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == instanceId);
        instance.Should().NotBeNull();
        instance!.Status.Should().Be("completed");
        instance.Result.Should().NotBeNull();
        var payload = JsonDocument.Parse(instance.Result!).RootElement;
        payload.GetProperty("prNumber").GetInt32().Should().Be(42);

        var events = await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.Type == "WORKFLOW.COMPLETED")
            .ToListAsync();
        events.Should().ContainSingle();
    }

    [Test]
    public async Task PostWorkflowResult_Failed_MarksFailedAndEmitsFailedEvent()
    {
        var (defId, instanceId) = await SeedWorkflowInstanceAsync();

        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/workflows/{instanceId}/result",
            new
            {
                status = "failed",
                result = new { error = "timed out" }
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var events = await db.DomainEvents.IgnoreQueryFilters()
            .Where(e => e.Type == "WORKFLOW.FAILED")
            .ToListAsync();
        events.Should().ContainSingle();
    }

    // ─── Key rotation ───────────────────────────────────────────────────────
    //
    // Dev-mode auth is permissive (no principal is stamped onto the request),
    // so the endpoint's "TryGetUserId -> null" guard fires on every HTTP call
    // without a real token. The happy-path / forbidden-path / not-found cases
    // are therefore exercised by invoking the service directly through the
    // boot-scoped DI container. The HTTP surface is still covered: we assert
    // that an unauthenticated call returns 401.

    [Test]
    public async Task PostRotateInstallationKey_Unauthenticated_Returns401()
    {
        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        var resp = await client.PostAsync(
            $"/api/v1/installations/{Guid.NewGuid()}/rotate-key",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task RotateInstallationKey_ThroughService_HappyPath_PersistsAndEmits()
    {
        var (userId, _, installationEntityId) = await SeedInstallationWithOwnerAsync();

        // Use the shared factory that registers AddSaaSServices so the scoped
        // service resolves exactly as it would behind the HTTP endpoint.
        using var scope = SharedFactory().Services.CreateScope();

        var rotation = scope.ServiceProvider.GetRequiredService<IApiKeyRotationService>();
        var result = await rotation.RotateAsync(installationEntityId, userId);

        result.Success.Should().BeTrue();
        result.PlaintextKey.Should().StartWith("tamma_sk_");

        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        (await db.ApiKeys.IgnoreQueryFilters()
            .CountAsync(k => k.OwnerId == installationEntityId.ToString()))
            .Should().Be(1);

        (await db.DomainEvents.IgnoreQueryFilters()
            .CountAsync(e => e.Type == "API_KEY.ROTATED"))
            .Should().Be(1);
    }

    [Test]
    public async Task RotateInstallationKey_ThroughService_NonOwner_Rejected()
    {
        var (_, _, installationEntityId) = await SeedInstallationWithOwnerAsync();
        var intruder = Guid.NewGuid();

        using var scope = SharedFactory().Services.CreateScope();
        var rotation = scope.ServiceProvider.GetRequiredService<IApiKeyRotationService>();

        var result = await rotation.RotateAsync(installationEntityId, intruder);
        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("forbidden");
    }

    [Test]
    public async Task RotateInstallationKey_ThroughService_UnknownInstallation_NotFound()
    {
        using var scope = SharedFactory().Services.CreateScope();
        var rotation = scope.ServiceProvider.GetRequiredService<IApiKeyRotationService>();

        var result = await rotation.RotateAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Success.Should().BeFalse();
        result.ErrorReason.Should().Be("not_found");
    }

    // ─── Harness ────────────────────────────────────────────────────────────

    private static HttpClient CreateClientWithCannedAnthropic(CannedHandler handler)
    {
        _activeHandler = handler;
        return SharedFactory().CreateClient();
    }

    /// <summary>
    /// Returns the single wrapped <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
    /// used across tests in this fixture. Because <c>Program.cs</c> wipes and
    /// re-migrates Tamma tables on every startup (per Epic 19), re-wrapping
    /// the factory per test would destroy seeded data. We therefore cache a
    /// single wrapped factory and flip the canned HTTP handler via a static
    /// reference before each test.
    /// </summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> SharedFactory()
    {
        if (_cachedFactory is not null) return _cachedFactory;
        lock (_factoryLock)
        {
            _cachedFactory ??= ApiTestFixture.Factory.WithWebHostBuilder(b =>
            {
                b.ConfigureServices(services =>
                {
                    services.AddSaaSServices();
                    services.AddDiagnosticsServices();

                    // Override the named "anthropic" HttpClient so tests stay hermetic.
                    // The primary handler closes over a static field so each test can
                    // swap its canned response without rebuilding the host.
                    services.AddHttpClient("anthropic")
                        .ConfigurePrimaryHttpMessageHandler(() => new DelegatingToCurrent());
                });
            });
        }
        return _cachedFactory;
    }

    private static async Task<(Guid DefinitionId, Guid InstanceId)> SeedWorkflowInstanceAsync()
    {
        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var def = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "test-def",
            Description = null,
            Steps = "[]",
            Version = 1
        };
        db.WorkflowDefinitions.Add(def);

        var inst = new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            DefinitionId = def.Id,
            Status = "pending",
            Variables = "{\"seeded\":true}"
        };
        db.WorkflowInstances.Add(inst);
        await db.SaveChangesAsync();

        return (def.Id, inst.Id);
    }

    private static async Task<(Guid UserId, Guid TenantId, Guid InstallationEntityId)> SeedInstallationWithOwnerAsync()
    {
        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var installationEntityId = Guid.NewGuid();

        // EF cannot resolve the User ↔ Tenant circular FK in a single batch,
        // so we insert the user without a tenant first, then backfill
        // TenantId after the tenant row exists.
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"owner-{userId:N}@example.com",
            Role = "owner",
            TenantId = null
        });
        await db.SaveChangesAsync();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "acme",
            Slug = $"acme-{tenantId:N}",
            Type = "org",
            Plan = "free",
            Settings = "{}",
            OwnerId = userId
        });
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(userId);
        user!.TenantId = tenantId;
        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = "owner"
        });
        db.GitHubInstallations.Add(new GitHubInstallation
        {
            Id = installationEntityId,
            InstallationId = Random.Shared.NextInt64(1_000_000, 9_999_999),
            AccountLogin = "acme",
            AccountType = "Organization",
            AppId = 0,
            Permissions = "{}",
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        return (userId, tenantId, installationEntityId);
    }

    /// <summary>Returns a canned HTTP response regardless of the request.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CannedHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public Task<HttpResponseMessage> TestSend(HttpRequestMessage request, CancellationToken ct)
            => SendAsync(request, ct);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Indirection handler that forwards to the static <see cref="_activeHandler"/>
    /// so tests can swap the canned response without rebuilding the host.
    /// </summary>
    private sealed class DelegatingToCurrent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Reflection-free path: invoke the active handler's protected
            // SendAsync via its public TestSend wrapper.
            return _activeHandler.TestSend(request, cancellationToken);
        }
    }
}
