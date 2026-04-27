using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Integration tests for agent endpoints. Uses a real Postgres via
/// Testcontainers (see <see cref="ApiTestFixture"/>).
///
/// In Development mode, auth is permissive so endpoints can be hit without
/// JWTs. Tenant context is not set (no auth user), so the repository gets
/// a null tenant — we seed a platform-default row to exercise resolution.
/// </summary>
[TestFixture]
public class AgentEndpointsIntegrationTests
{
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _client = ApiTestFixture.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
    }

    // -----------------------------------------------------------------------
    // ResolveAgent — no tenant override, returns platform default
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveAgent_Developer_NoOverride_Returns_PlatformDefault()
    {
        var resp = await _client.GetAsync("/api/v1/agents/developer/resolve");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("role").GetString().Should().Be("developer");
        body.GetProperty("provider").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("model").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("source").GetString().Should().Be("platform-default");
    }

    [Test]
    public async Task ResolveAgent_UnknownRole_Returns_BadRequest()
    {
        var resp = await _client.GetAsync("/api/v1/agents/unknown_role/resolve");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // ResolveAgent — tenant override from DB
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveAgent_Developer_WithTenantOverride_Returns_MergedConfig()
    {
        // Seed a tenant-scoped override via repository.
        // Phase-1 hardening (finding 031) added an FK on agent_configs.TenantId
        // → tenants.Id, so the tenant row must exist before the override insert.
        var tenantId = Guid.NewGuid();
        var configJson = """
            {
              "roles": {
                "developer": {
                  "provider": "openai",
                  "model": "gpt-4o",
                  "temperature": 0.3
                }
              }
            }
            """;

        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tamma.Data.ControlPlaneDbContext>();
            db.Tenants.Add(new Tamma.Data.Entities.Tenant
            {
                Id = tenantId,
                Name = $"Test {tenantId:N}",
                Slug = $"t-{tenantId:N}",
                Plan = "free"
            });
            await db.SaveChangesAsync();

            var repo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();
            await repo.UpsertAsync(tenantId, configJson, null);
        }

        // With dev-mode permissive auth there's no tenant header propagation.
        // Exercise the service directly to prove merge end-to-end via the
        // service DI container, which the endpoint also uses.
        using var scope2 = ApiTestFixture.Factory.Services.CreateScope();
        var resolver = scope2.ServiceProvider
            .GetRequiredService<Tamma.Api.Services.Agents.IAgentResolverService>();
        var resolved = await resolver.ResolveAsync(tenantId, "developer");

        resolved.Provider.Should().Be("openai");
        resolved.Model.Should().Be("gpt-4o");
        resolved.Temperature.Should().BeApproximately(0.3, 0.001);
        resolved.Source.Should().Be("tenant-override");
    }

    // -----------------------------------------------------------------------
    // GetConfig / UpdateConfig roundtrip
    // -----------------------------------------------------------------------

    [Test]
    public async Task PutConfig_WithoutTenantContext_Returns400_AndGetStillReturnsPlatformDefault()
    {
        // Story 28-1 PR A (Decision #1) + PR #338 wave-4 review fix:
        // platform defaults moved to code (DefaultAgentConfig.ForRole) AND
        // the endpoint now rejects null-tenant writes with 400 instead of
        // returning a misleading 200 + emitting a false
        // AGENT_CONFIG.UPDATED.SUCCESS audit event. Honest response shape +
        // honest event-store contents.
        var payload = new
        {
            config = new
            {
                roles = new
                {
                    developer = new
                    {
                        provider = "openai",
                        model = "gpt-4o"
                    }
                }
            }
        };

        var put = await _client.PutAsJsonAsync("/api/v1/agents/config", payload);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        putBody.GetProperty("error").GetString().Should().Be("no_tenant_context");

        var get = await _client.GetAsync("/api/v1/agents/config");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();

        // GET surfaces the "platform-default" source marker; the PUT was
        // rejected so no developer override leaks through.
        body.GetProperty("source").GetString().Should().Be("platform-default");
    }

    [Test]
    public async Task PutConfig_WithoutTenantContext_DoesNotEmitAgentConfigUpdatedSuccessEvent()
    {
        // PR #338 wave-4 review HIGH regression test. The original bug:
        // when tenantContext.TenantId was null the endpoint would skip the
        // persistence write but still call events.AppendAsync with a
        // SUCCESS event, poisoning the DCB audit trail. Pin the
        // no-op-no-audit invariant — every emitted event must correspond
        // to a real state transition.
        var payload = new
        {
            config = new
            {
                roles = new
                {
                    developer = new { provider = "openai", model = "gpt-4o" }
                }
            }
        };

        var put = await _client.PutAsJsonAsync("/api/v1/agents/config", payload);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Inspect the event store directly. AppendAsync writes platform-
        // scope events to the CP DomainEvents table when tenantId is null,
        // and tenant-scope events route through the tenant factory — so
        // the cross-tenant QueryAsync(null, type, ...) path catches both.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var events = await eventRepo.QueryAsync(
            tenantId: null,
            type: "AGENT_CONFIG.UPDATED.SUCCESS",
            issueNumber: null,
            limit: 50);

        events.Should().BeEmpty(
            because: "no-op admin PUTs must not emit *.UPDATED.SUCCESS — " +
                     "the audit trail can only contain real state transitions.");
    }

    [Test]
    public async Task ValidateConfig_WithValidPayload_Returns_Ok()
    {
        var payload = new
        {
            config = new
            {
                roles = new
                {
                    developer = new { provider = "anthropic", model = "claude-sonnet-4" }
                }
            }
        };

        var resp = await _client.PostAsJsonAsync("/api/v1/agents/config/validate", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // ResolveForPhase — POST body
    // -----------------------------------------------------------------------

    [Test]
    public async Task ResolveForPhase_Implement_Developer_ReturnsConfig()
    {
        var payload = new { phase = "implement", role = "developer" };
        var resp = await _client.PostAsJsonAsync("/api/v1/agents/resolve-for-phase", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("phase").GetString().Should().Be("implement");
        body.GetProperty("role").GetString().Should().Be("developer");
        body.GetProperty("provider").GetString().Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ResolveForPhase_IneligiblePair_Returns_BadRequest()
    {
        var payload = new { phase = "plan", role = "tester" };
        var resp = await _client.PostAsJsonAsync("/api/v1/agents/resolve-for-phase", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
