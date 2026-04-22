using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Providers;

/// <summary>
/// End-to-end HTTP tests for the provider health endpoints. Exercises the real
/// circuit-breaker state machine against a live Postgres container via
/// <see cref="ApiTestFixture"/>.
///
/// <para>
/// Because the production <c>Program.cs</c> does not yet register
/// <see cref="ICircuitBreakerService"/>, this fixture overrides the WebHost to
/// register the new services and wire the chain endpoint. When Program.cs is
/// updated by the parent composition step, these overrides become redundant
/// but remain harmless.
/// </para>
/// </summary>
[TestFixture]
public class ProviderHealthEndpointsIntegrationTests
{
    private const string Tenant = "22222222-3333-4444-5555-666666666666";
    private static readonly DateTimeOffset StartClock =
        new(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly CircuitBreakerOptions TestBreakerOptions = new()
    {
        FailureThreshold = 3,
        FailureWindow = TimeSpan.FromSeconds(60),
        CooldownDuration = TimeSpan.FromSeconds(300),
    };

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private TestSystemClock _clock = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ProvidersSetUpFixture.ResetDatabaseAsync();

        _clock = new TestSystemClock(StartClock);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                {
                    // Remove any previously registered clock/breaker/resolver so
                    // test doubles win. Safe regardless of whether Program.cs has
                    // already wired them (per Epic 19 it has not).
                    RemoveAll<ISystemClock>(services);
                    RemoveAll<CircuitBreakerOptions>(services);
                    RemoveAll<ICircuitBreakerService>(services);
                    RemoveAll<IProviderChainResolver>(services);

                    services.AddSingleton<ISystemClock>(_clock);
                    services.AddSingleton(TestBreakerOptions);
                    services.AddScoped<ICircuitBreakerService, CircuitBreakerService>();
                    services.AddScoped<IProviderChainResolver, ProviderChainResolver>();

                    // Bolt on the POST /api/providers/chain/resolve route via a
                    // terminal middleware — parent will replace this once
                    // Program.cs calls MapProviderChainEndpoints().
                    services.AddSingleton<IStartupFilter>(new ProviderHealthStartupFilter());
                });
            });

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // ── Failure / success round-trip ─────────────────────────────────────────

    [Test]
    public async Task RecordFailure_BelowThreshold_StaysClosed()
    {
        await PostFailureAsync("anthropic");
        await PostFailureAsync("anthropic");

        var state = await GetStateAsync("anthropic");
        state.RootElement.GetProperty("state").GetString().Should().Be("Closed");
        state.RootElement.GetProperty("failureCount").GetInt32().Should().Be(2);
    }

    [Test]
    public async Task RecordFailure_AtThreshold_OpensCircuit()
    {
        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        var state = await GetStateAsync("anthropic");
        state.RootElement.GetProperty("state").GetString().Should().Be("Open");
        state.RootElement.GetProperty("circuitOpenUntil").ValueKind
            .Should().NotBe(JsonValueKind.Null);
    }

    [Test]
    public async Task Cooldown_PromotesOpenToHalfOpen()
    {
        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        _clock.Advance(TimeSpan.FromSeconds(301));

        var state = await GetStateAsync("anthropic");
        state.RootElement.GetProperty("state").GetString().Should().Be("HalfOpen");
    }

    [Test]
    public async Task RecordSuccess_ResetsState()
    {
        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        var ok = await _client.PostAsync($"/api/providers/health/providers/anthropic/success", content: null);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await GetStateAsync("anthropic");
        state.RootElement.GetProperty("state").GetString().Should().Be("Closed");
        state.RootElement.GetProperty("failureCount").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task Reset_ClearsState()
    {
        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        var resp = await _client.PostAsync("/api/providers/health/providers/anthropic/reset", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await GetStateAsync("anthropic");
        state.RootElement.GetProperty("state").GetString().Should().Be("Closed");
        state.RootElement.GetProperty("failureCount").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task GetProviderHealth_UnknownKey_ReturnsHealthyTwoHundred()
    {
        // Finding 012 — TS GET /health/providers/:key returned 200 with a
        // synthesized healthy body for unseen keys so dashboards can poll
        // without branching. C# now matches.
        var resp = await _client.GetAsync("/api/providers/health/providers/nonexistent-provider");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        body!.RootElement.GetProperty("state").GetString().Should().Be("Closed");
        body.RootElement.GetProperty("failureCount").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("healthy").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task ListProviderHealth_ReturnsAllTrackedRows()
    {
        await PostFailureAsync("anthropic");
        await PostFailureAsync("openai");

        var resp = await _client.GetAsync("/api/providers/health/providers");
        resp.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var keys = body.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("providerKey").GetString()!)
            .ToHashSet();
        keys.Should().Contain(new[] { "anthropic", "openai" });
    }

    [Test]
    public async Task GetHealthSummary_ReturnsProvidersArray()
    {
        await PostFailureAsync("anthropic");

        var resp = await _client.GetAsync("/api/providers/health");
        resp.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("providers", out var providers).Should().BeTrue();
        providers.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── Persistence round-trip with actual DB ───────────────────────────────

    [Test]
    public async Task RecordFailure_PersistsCircuitOpenUntilColumn()
    {
        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.ProviderHealths
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.ProviderKey == "anthropic");

        row.Should().NotBeNull();
        row!.CircuitOpenUntil.Should().NotBeNull();
        row.FailureCount.Should().BeGreaterThanOrEqualTo(3);
    }

    // ── Chain resolve endpoint ───────────────────────────────────────────────

    [Test]
    public async Task ResolveChain_WithNoConfig_ReturnsEmptyChainError()
    {
        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("EMPTY_PROVIDER_CHAIN");
    }

    [Test]
    public async Task ResolveChain_WithConfig_ReturnsOrderedProviders()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [
              {"provider": "anthropic", "model": "claude-sonnet-4"},
              {"provider": "openai",    "model": "gpt-4o"}
            ]
          }
        }
        """);

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ordered = body.RootElement.GetProperty("ordered");
        ordered.GetArrayLength().Should().Be(2);
        ordered[0].GetProperty("provider").GetString().Should().Be("anthropic");
        ordered[1].GetProperty("provider").GetString().Should().Be("openai");
    }

    [Test]
    public async Task ResolveChain_SkipsOpenProviders()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [
              {"provider": "anthropic"},
              {"provider": "openai"}
            ]
          }
        }
        """);

        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ordered = body.RootElement.GetProperty("ordered");
        ordered.GetArrayLength().Should().Be(1);
        ordered[0].GetProperty("provider").GetString().Should().Be("openai");

        var skipped = body.RootElement.GetProperty("skipped");
        skipped.GetArrayLength().Should().Be(1);
        skipped[0].GetProperty("reason").GetString().Should().Be("CircuitOpen");
    }

    [Test]
    public async Task ResolveChain_AppendsHalfOpenProvidersAtTail()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [
              {"provider": "anthropic"},
              {"provider": "openai"}
            ]
          }
        }
        """);

        for (var i = 0; i < 3; i++)
            await PostFailureAsync("anthropic");

        _clock.Advance(TimeSpan.FromSeconds(301));

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ordered = body.RootElement.GetProperty("ordered");
        ordered.GetArrayLength().Should().Be(2);
        ordered[0].GetProperty("provider").GetString().Should().Be("openai");
        ordered[1].GetProperty("provider").GetString().Should().Be("anthropic");
        ordered[1].GetProperty("reason").GetString().Should().Be("HalfOpenProbeCandidate");
    }

    [Test]
    public async Task ResolveChain_BadRequest_WhenRoleMissing()
    {
        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { action = "code_generation" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Story 9-5 — recommendedProvider / allExhausted / entries[] ──────────

    [Test]
    public async Task ResolveChain_HealthyChain_ReturnsRecommendedProviderAndEntries()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [
              {"provider": "anthropic", "model": "claude-sonnet-4"},
              {"provider": "openai",    "model": "gpt-4o"}
            ]
          }
        }
        """);

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("recommendedProvider").GetString().Should().Be("anthropic");
        body.RootElement.GetProperty("allExhausted").GetBoolean().Should().BeFalse();

        // entries[] mirrors ordered[] and surfaces per-entry status.
        var entries = body.RootElement.GetProperty("entries");
        entries.GetArrayLength().Should().Be(2);
        entries[0].GetProperty("recommended").GetBoolean().Should().BeTrue();
        entries[0].GetProperty("budgetAllowed").GetBoolean().Should().BeTrue();
        entries[0].GetProperty("healthy").GetBoolean().Should().BeTrue();
        entries[0].GetProperty("circuitOpen").GetBoolean().Should().BeFalse();
        entries[1].GetProperty("recommended").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task ResolveChain_AllOpen_ReturnsAllExhaustedTrue()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [
              {"provider": "anthropic"},
              {"provider": "openai"}
            ]
          }
        }
        """);
        for (var i = 0; i < 3; i++) await PostFailureAsync("anthropic");
        for (var i = 0; i < 3; i++) await PostFailureAsync("openai");

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("recommendedProvider").ValueKind
            .Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("allExhausted").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("error").GetString().Should().Be("NO_AVAILABLE_PROVIDER");

        // skipped[] entries carry full status — circuitOpen=true and the
        // open-until timestamp is preserved on the wire.
        var skipped = body.RootElement.GetProperty("skipped");
        skipped.GetArrayLength().Should().Be(2);
        skipped[0].GetProperty("circuitOpen").GetBoolean().Should().BeTrue();
        skipped[0].GetProperty("circuitOpenUntil").ValueKind
            .Should().NotBe(JsonValueKind.Null);
    }

    [Test]
    public async Task ResolveChain_EmptyConfig_ReturnsAllExhaustedTrue()
    {
        // No agent_config row at all — empty chain still surfaces the new
        // shape so dashboards can render the "no providers" state without
        // branching.
        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("EMPTY_PROVIDER_CHAIN");
        body.RootElement.GetProperty("allExhausted").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("recommendedProvider").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task ResolveChain_HalfOpenProbe_SurfacesAsRecommendedAtTail()
    {
        await SeedAgentConfigAsync("""
        {
          "chains": {
            "default": [{"provider": "anthropic"}]
          }
        }
        """);
        // Trip the breaker, then advance past the cooldown so it flips to
        // HalfOpen — only one provider configured so the probe is the
        // recommended choice.
        for (var i = 0; i < 3; i++) await PostFailureAsync("anthropic");
        _clock.Advance(TimeSpan.FromSeconds(301));

        var resp = await _client.PostAsJsonAsync("/api/providers/chain/resolve",
            new { role = "developer", action = "code_generation" });

        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("recommendedProvider").GetString().Should().Be("anthropic");
        body.RootElement.GetProperty("allExhausted").GetBoolean().Should().BeFalse();
        var entries = body.RootElement.GetProperty("entries");
        entries[0].GetProperty("reason").GetString().Should().Be("HalfOpenProbeCandidate");
        entries[0].GetProperty("recommended").GetBoolean().Should().BeTrue();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void RemoveAll<T>(IServiceCollection services) =>
        ServiceCollectionDescriptorExtensions.RemoveAll<T>(services);

    private async Task PostFailureAsync(string key)
    {
        var resp = await _client.PostAsync($"/api/providers/health/providers/{key}/failure", content: null);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<JsonDocument> GetStateAsync(string key)
    {
        var resp = await _client.GetAsync($"/api/providers/health/providers/{key}");
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// <see cref="IStartupFilter"/> that wraps the production pipeline to
    /// inject a tiny middleware branch handling
    /// <c>POST /api/providers/chain/resolve</c>.
    /// </summary>
    private sealed class ProviderHealthStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                // Branch middleware for the new chain-resolve route — terminal.
                app.Use(async (ctx, contNext) =>
                {
                    if (HttpMethods.IsPost(ctx.Request.Method) &&
                        ctx.Request.Path.Equals("/api/providers/chain/resolve",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ResolveChainRequest? req = null;
                        try
                        {
                            req = await JsonSerializer.DeserializeAsync<ResolveChainRequest>(
                                ctx.Request.Body,
                                new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        }
                        catch
                        {
                            req = null;
                        }

                        if (req is null || string.IsNullOrWhiteSpace(req.Role) ||
                            string.IsNullOrWhiteSpace(req.Action))
                        {
                            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await ctx.Response.WriteAsJsonAsync(new { error = "role and action are required" });
                            return;
                        }

                        var resolver = ctx.RequestServices.GetRequiredService<IProviderChainResolver>();
                        var tenantCtx = ctx.RequestServices.GetRequiredService<ITenantContext>();

                        // Story 9-5 — opt-in accountId override on the body.
                        Guid? accountId = null;
                        if (!string.IsNullOrWhiteSpace(req.AccountId) &&
                            Guid.TryParse(req.AccountId, out var parsed))
                        {
                            accountId = parsed;
                        }
                        var options = new ChainResolveOptions(AccountId: accountId);
                        var result = await resolver.ResolveAsync(
                            tenantCtx.TenantId, req.Role, req.Action, options);

                        var orderedDtos = result.Ordered.Select(MapEntry).ToList();
                        var skippedDtos = result.Skipped.Select(MapEntry).ToList();

                        if (!result.HasCandidates)
                        {
                            await ctx.Response.WriteAsJsonAsync(new
                            {
                                ordered = Array.Empty<object>(),
                                entries = orderedDtos,
                                skipped = skippedDtos,
                                recommendedProvider = (string?)null,
                                allExhausted = true,
                                error = result.ErrorCode,
                                message = result.ErrorMessage,
                            });
                            return;
                        }

                        await ctx.Response.WriteAsJsonAsync(new
                        {
                            ordered = orderedDtos,
                            entries = orderedDtos,
                            skipped = skippedDtos,
                            recommendedProvider = result.RecommendedProvider,
                            allExhausted = result.AllExhausted,
                        });
                        return;
                    }

                    await contNext();
                });

                next(app);
            };
        }

        /// <summary>
        /// Project a <see cref="ChainEntry"/> into the wire DTO that mirrors
        /// the production <c>ProviderEndpoints.MapEntryToDto</c> shape (Story
        /// 9-5: <c>healthy</c> / <c>circuitOpen</c> / <c>circuitOpenUntil</c> /
        /// <c>budgetAllowed</c> / <c>budgetSpent</c> / <c>recommended</c>).
        /// </summary>
        private static object MapEntry(ChainEntry e) => new
        {
            provider = e.Provider.Provider,
            model = e.Provider.Model,
            key = e.Provider.Key,
            reason = e.Reason.ToString(),
            healthy = e.Healthy,
            circuitOpen = e.CircuitOpen,
            circuitOpenUntil = e.CircuitOpenUntil,
            budgetAllowed = e.BudgetAllowed,
            budgetSpent = e.BudgetSpent,
            recommended = e.Recommended,
        };
    }

    private async Task SeedAgentConfigAsync(string configJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var existing = await db.AgentConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == null);
        if (existing is null)
        {
            db.AgentConfigs.Add(new AgentConfig
            {
                Id = Guid.NewGuid(),
                TenantId = null,
                Config = configJson,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Config = configJson;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }
}
