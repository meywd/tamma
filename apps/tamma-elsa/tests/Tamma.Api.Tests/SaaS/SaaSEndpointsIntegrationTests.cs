using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.SaaS;
using Tamma.Data;
using Tamma.Data.Abstractions;
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
    /// <summary>
    /// Story 28-1 PR D — workflow_instances + domain_events moved to the
    /// per-tenant DB; <see cref="WorkflowRepository.UpdateInstanceAsync"/>
    /// now requires an ambient tenant id. Dev-mode permissive auth doesn't
    /// stamp a principal, so the middleware never resolves a tenant from
    /// claims/membership. We replace the scoped <see cref="ITenantContext"/>
    /// with a fixed-tenant stub used by every workflow seed + endpoint
    /// invocation in this fixture.
    /// </summary>
    private static readonly Guid TestTenantId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

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
        await EnsurePinnedTenantProvisionedAsync();
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
        // Story 28-1 PR D — provider_diagnostics live on the tenant DB.
        using var scope = SharedFactory().Services.CreateScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var diagnosticCount = await tdb.ProviderDiagnostics
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
        await EnsurePinnedTenantProvisionedAsync();
        var handler = new CannedHandler(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
        using var client = CreateClientWithCannedAnthropic(handler);

        var resp = await client.PostAsJsonAsync("/api/v1/llm/chat", new
        {
            model = "claude-sonnet-4.5",
            messages = new[] { new { role = "user", content = "hi" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        // Story 28-1 PR D — provider_diagnostics live on the tenant DB.
        using var scope = SharedFactory().Services.CreateScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var failures = await tdb.ProviderDiagnostics
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
                step = "CodeGeneration",
                progress = 50,
                message = "half-way"
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        // Story 28-1 PR D — workflow_instances live on the tenant DB.
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var instance = await tdb.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == instanceId);
        instance.Should().NotBeNull();
        instance!.Status.Should().Be("running");
        instance.CurrentActivity.Should().Be("CodeGeneration");
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
            new { status = "running", step = "Init" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Workflow result ────────────────────────────────────────────────────

    [Test]
    public async Task PostWorkflowResult_Success_MarksCompletedAndStoresResultAndEmitsEvent()
    {
        var (defId, instanceId) = await SeedWorkflowInstanceAsync();

        using var client = CreateClientWithCannedAnthropic(
            new CannedHandler(HttpStatusCode.OK, "{}"));

        // Audit finding 019: typed fields are now first-class on the DTO; the
        // endpoint stores them at the top level of the persisted result blob.
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/workflows/{instanceId}/result",
            new
            {
                status = "completed",
                prNumber = 42,
                duration = 1234
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        // Story 28-1 PR D — workflow_instances + domain_events live on
        // the tenant DB.
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var instance = await tdb.WorkflowInstances.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == instanceId);
        instance.Should().NotBeNull();
        instance!.Status.Should().Be("completed");
        instance.Result.Should().NotBeNull();
        var payload = JsonDocument.Parse(instance.Result!).RootElement;
        payload.GetProperty("prNumber").GetInt32().Should().Be(42);
        payload.GetProperty("duration").GetInt64().Should().Be(1234);

        var events = await tdb.DomainEvents.IgnoreQueryFilters()
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
                error = "timed out"
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory().Services.CreateScope();
        // Story 28-1 PR D — domain_events live on the tenant DB.
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var events = await tdb.DomainEvents.IgnoreQueryFilters()
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

        // Audit finding 020 — route param is now `long` (the GitHub-issued
        // installation id), not the internal Guid.
        var resp = await client.PostAsync(
            $"/api/v1/installations/12345678/rotate-key",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task RotateInstallationKey_ThroughService_HappyPath_PersistsAndEmits()
    {
        var (userId, ownerTenantId, installationEntityId) = await SeedInstallationWithOwnerAsync();

        // Use the shared factory that registers AddSaaSServices so the scoped
        // service resolves exactly as it would behind the HTTP endpoint.
        using var scope = SharedFactory().Services.CreateScope();

        var rotation = scope.ServiceProvider.GetRequiredService<IApiKeyRotationService>();
        var result = await rotation.RotateAsync(installationEntityId, userId);

        result.Success.Should().BeTrue();
        result.PlaintextKey.Should().StartWith("tamma_sk_");

        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        (await db.ApiKeys.IgnoreQueryFilters()
            .CountAsync(k => k.OwnerId == installationEntityId.ToString()))
            .Should().Be(1);

        // Story 28-1 PR D — domain_events live on the tenant DB. The
        // RotateAsync path emits API_KEY.ROTATED tagged with the
        // installation's owner tenant id.
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(ownerTenantId);
        (await tdb.DomainEvents.IgnoreQueryFilters()
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

                    // Story 28-1 PR D — pin TenantContext to the test tenant
                    // so WorkflowRepository (which now requires an ambient
                    // tenant id) resolves to the seeded workflow instance.
                    services.RemoveAll<ITenantContext>();
                    services.AddScoped<ITenantContext>(_ =>
                    {
                        var ctx = new TenantContext();
                        ctx.SetTenantId(TestTenantId);
                        return ctx;
                    });
                });
            });
        }
        return _cachedFactory;
    }

    /// <summary>
    /// Make sure the pinned tenant exists on CP and is provisioned —
    /// TenantContextMiddleware doesn't run for dev-mode permissive auth,
    /// but every code path that touches the pinned tenant's data
    /// (diagnostics, workflows, events) rides the unified resolver, which
    /// requires a provisioned tenant (Phase 3).
    /// </summary>
    private static async Task EnsurePinnedTenantProvisionedAsync()
    {
        using var scope = SharedFactory().Services.CreateScope();
        var cp = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var tenantExists = await cp.Tenants.IgnoreQueryFilters()
            .AnyAsync(t => t.Id == TestTenantId);
        if (!tenantExists)
        {
            cp.Tenants.Add(new Tenant
            {
                Id = TestTenantId,
                Name = "saas-test",
                Slug = $"saas-{TestTenantId:N}",
                Type = "personal",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await cp.SaveChangesAsync();
        }

        await Infrastructure.TestTenantProvisioning.ProvisionAsync(
            SharedFactory().Services, TestTenantId);
    }

    private static async Task<(Guid DefinitionId, Guid InstanceId)> SeedWorkflowInstanceAsync()
    {
        await EnsurePinnedTenantProvisionedAsync();

        using var scope = SharedFactory().Services.CreateScope();

        // Story 28-1 PR D — workflow_definitions + workflow_instances live
        // on the tenant DB now. Seed via ITenantDbContextFactory.
        var factory = scope.ServiceProvider
            .GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(TestTenantId);
        var def = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "test-def",
            Description = null,
            Steps = "[]",
            Version = 1,
            TenantId = TestTenantId,
        };
        tdb.WorkflowDefinitions.Add(def);

        var inst = new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            DefinitionId = def.Id,
            Status = "pending",
            Variables = "{\"seeded\":true}",
            TenantId = TestTenantId,
        };
        tdb.WorkflowInstances.Add(inst);
        await tdb.SaveChangesAsync();

        return (def.Id, inst.Id);
    }

    private static async Task<(Guid UserId, Guid TenantId, Guid InstallationEntityId)> SeedInstallationWithOwnerAsync()
    {
        using var scope = SharedFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

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

        // Phase 3 -- key-rotation audit events land in the tenant store,
        // which is only reachable for provisioned tenants.
        await Infrastructure.TestTenantProvisioning.ProvisionAsync(
            SharedFactory().Services, tenantId);

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
