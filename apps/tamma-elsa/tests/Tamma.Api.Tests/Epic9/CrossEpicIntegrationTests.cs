using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic9;

/// <summary>
/// Story 9-12: Cross-Epic Integration Test.
///
/// Exercises the full chain across Epic 9 (Agent Management), Epic 17
/// (Multi-Tenancy), and Epic 27 (Prompt Store) at the service-layer level.
/// We wire real repositories + services against the Testcontainers Postgres
/// fixture and prove tenant isolation end-to-end.
///
/// Note: Dev-mode permissive auth does not propagate a tenant context to
/// HTTP requests, so we invoke the services via DI directly — the same
/// path the endpoints take.
/// </summary>
[TestFixture]
public class CrossEpicIntegrationTests
{
    private Guid _tenantA;
    private Guid _tenantB;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _tenantA = Guid.NewGuid();
        _tenantB = Guid.NewGuid();

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // Seed two tenant rows — agent_configs FK requires them.
        db.Tenants.Add(new Tenant
        {
            Id = _tenantA,
            Name = $"TenantA {_tenantA:N}",
            Slug = $"a-{_tenantA:N}",
            Plan = "free",
        });
        db.Tenants.Add(new Tenant
        {
            Id = _tenantB,
            Name = $"TenantB {_tenantB:N}",
            Slug = $"b-{_tenantB:N}",
            Plan = "free",
        });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------
    // Test 1 — Agent config isolation (Epic 9 + Epic 17)
    // ---------------------------------------------------------------------

    [Test]
    public async Task AgentResolver_TenantA_HasCustomProvider_TenantB_UsesDefault()
    {
        // Seed tenant A with a custom agent config override.
        var overrideJson = """
            {
              "roles": {
                "developer": {
                  "provider": "openai",
                  "model": "gpt-4o"
                }
              }
            }
            """;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();
            await repo.UpsertAsync(_tenantA, overrideJson, userId: null);
        }

        // Resolve both tenants
        ResolvedAgentConfig resolvedA;
        ResolvedAgentConfig resolvedB;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IAgentResolverService>();
            resolvedA = await resolver.ResolveAsync(_tenantA, "developer");
            resolvedB = await resolver.ResolveAsync(_tenantB, "developer");
        }

        resolvedA.Provider.Should().Be("openai");
        resolvedA.Model.Should().Be("gpt-4o");
        resolvedA.Source.Should().Be("tenant-override");

        // Tenant B has no override → platform default
        resolvedB.Source.Should().Be("platform-default");
        resolvedB.Provider.Should().NotBe("openai");
    }

    // ---------------------------------------------------------------------
    // Test 2 — Prompt override isolation (Epic 27)
    // ---------------------------------------------------------------------

    [Test]
    public async Task PromptStore_TenantScopedOverride_IsVisibleOnlyToThatTenant()
    {
        // prompt_overrides is keyed on UserId in the current schema; we use
        // each tenant id as the user id for this cross-epic test so the
        // tenant isolation story still holds — two different actors, two
        // different override sets, one shared system fallback.
        const string role = "developer";
        const string action = "implement";
        const string tenantASpecificMarker =
            "TENANT-A-CUSTOM-TEMPLATE-{{taskDescription}}";

        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IPromptRepository>();
            await repo.UpsertAsync(new PromptOverride
            {
                UserId = _tenantA,
                Scope = "role-action",
                Role = role,
                Action = action,
                Template = tenantASpecificMarker,
                SystemPrompt = "tenant A system prompt",
                Variables = new[] { "taskDescription" },
                EnableTools = false,
                MaxTokens = 4096,
            });
        }

        ResolvedPrompt? resolvedA;
        ResolvedPrompt? resolvedB;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromptStoreService>();
            resolvedA = await svc.ResolveRoleActionAsync(_tenantA, role, action);
            resolvedB = await svc.ResolveRoleActionAsync(_tenantB, role, action);
        }

        resolvedA.Should().NotBeNull();
        resolvedA!.Source.Should().Be(PromptSource.UserOverride);
        resolvedA.Template.Should().Contain("TENANT-A-CUSTOM-TEMPLATE");

        // Tenant B resolves to the system default, not tenant A's override.
        resolvedB.Should().NotBeNull();
        resolvedB!.Source.Should().NotBe(PromptSource.UserOverride);
        resolvedB.Template.Should().NotContain("TENANT-A-CUSTOM-TEMPLATE");
    }

    // ---------------------------------------------------------------------
    // Test 3 — Diagnostics isolation (Epic 9 Story 9-2)
    // ---------------------------------------------------------------------

    [Test]
    public async Task Diagnostics_QueryPerTenant_ReturnsOnlyOwnRows()
    {
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();

            // 2 rows for tenant A, 3 rows for tenant B
            for (var i = 0; i < 2; i++)
            {
                await repo.InsertAsync(new ProviderDiagnostic
                {
                    Id = Guid.NewGuid(),
                    ProviderKey = "anthropic",
                    Cost = 0.10m * (i + 1),
                    RequestDurationMs = 100 + i * 10,
                    TokensUsed = 100,
                    Success = true,
                    TenantId = _tenantA,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            for (var i = 0; i < 3; i++)
            {
                await repo.InsertAsync(new ProviderDiagnostic
                {
                    Id = Guid.NewGuid(),
                    ProviderKey = "anthropic",
                    Cost = 0.20m * (i + 1),
                    RequestDurationMs = 200 + i * 10,
                    TokensUsed = 200,
                    Success = true,
                    TenantId = _tenantB,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        int totalA, totalB;
        decimal sumA, sumB;
        var since = DateTime.UtcNow.AddMinutes(-5);
        var until = DateTime.UtcNow.AddMinutes(5);
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();

            var (_, tA) = await repo.QueryAsync(
                providerKey: null, from: null, to: null,
                limit: 100, offset: 0,
                tenantId: _tenantA, success: null, model: null);
            var (_, tB) = await repo.QueryAsync(
                providerKey: null, from: null, to: null,
                limit: 100, offset: 0,
                tenantId: _tenantB, success: null, model: null);
            totalA = tA;
            totalB = tB;

            sumA = await repo.GetCostSumAsync(_tenantA, since, until);
            sumB = await repo.GetCostSumAsync(_tenantB, since, until);
        }

        totalA.Should().Be(2);
        totalB.Should().Be(3);

        // Tenant A cost: 0.10 + 0.20 = 0.30
        sumA.Should().Be(0.30m);
        // Tenant B cost: 0.20 + 0.40 + 0.60 = 1.20
        sumB.Should().Be(1.20m);
    }

    // ---------------------------------------------------------------------
    // Test 4 — Combined chain: resolve agent → record diagnostics → query budget
    // ---------------------------------------------------------------------

    [Test]
    public async Task FullChain_TenantA_EndsToEndWithIsolatedDiagnostics()
    {
        // 1) Seed tenant-A agent override
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();
            await repo.UpsertAsync(_tenantA, """
                {"roles":{"developer":{"provider":"openai","model":"gpt-4o"}}}
                """, userId: null);
        }

        // 2) Resolve agent for tenant A
        ResolvedAgentConfig resolved;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IAgentResolverService>();
            resolved = await resolver.ResolveAsync(_tenantA, "developer");
        }
        resolved.Provider.Should().Be("openai");

        // 3) Record a successful-call diagnostic tagged with tenant A
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
            await repo.InsertAsync(new ProviderDiagnostic
            {
                Id = Guid.NewGuid(),
                ProviderKey = resolved.Provider,
                Model = resolved.Model,
                Cost = 0.42m,
                RequestDurationMs = 777,
                TokensUsed = 500,
                InputTokens = 300,
                OutputTokens = 200,
                Success = true,
                TenantId = _tenantA,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // 4) Budget-sum lookup isolates tenant A from tenant B
        decimal spentA, spentB;
        var since = DateTime.UtcNow.AddMinutes(-5);
        var until = DateTime.UtcNow.AddMinutes(5);
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDiagnosticsRepository>();
            spentA = await repo.GetCostSumAsync(_tenantA, since, until);
            spentB = await repo.GetCostSumAsync(_tenantB, since, until);
        }
        spentA.Should().Be(0.42m);
        spentB.Should().Be(0m);
    }

    // ---------------------------------------------------------------------
    // Test 5 — System defaults visible to every tenant
    //          (Epic 27 prompt tables exempt from tenant RLS)
    // ---------------------------------------------------------------------

    [Test]
    public async Task PromptStore_SystemDefault_IsReadableByAnyTenant_WithoutOverrides()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PromptStoreService>();

        // developer/implement is shipped in SystemPrompts — both tenants
        // should resolve to it when neither has an override.
        var resolvedA = await svc.ResolveRoleActionAsync(_tenantA, "developer", "implement");
        var resolvedB = await svc.ResolveRoleActionAsync(_tenantB, "developer", "implement");

        resolvedA.Should().NotBeNull();
        resolvedA!.Source.Should().Be(PromptSource.SystemRoleAction);
        resolvedB.Should().NotBeNull();
        resolvedB!.Source.Should().Be(PromptSource.SystemRoleAction);

        // Same template bytes — no tenant leakage, just the system default.
        resolvedA.Template.Should().Be(resolvedB.Template);
    }
}
