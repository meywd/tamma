using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Providers;
using Tamma.Api.Tests.Infrastructure;

namespace Tamma.Api.Tests.ProviderSession;

/// <summary>
/// End-to-end HTTP tests for the provider-session endpoints. Replaces the
/// upstream provider client with a <see cref="CannedProviderClient"/> so the
/// test suite never hits Anthropic/OpenAI.
/// </summary>
[TestFixture]
public class ProviderSessionEndpointsIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private CannedProviderClient _providerClient = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ProviderSessionSetUpFixture.ResetDatabaseAsync();

        _providerClient = new CannedProviderClient
        {
            Next = new ProviderInvocationResult("hello from stub", 17, 0.0015m, 42),
        };

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.DisableAlertHostedServices();
                builder.ConfigureTestServices(services =>
                {
                    RemoveAll<IProviderSessionService>(services);
                    RemoveAll<IProviderClient>(services);
                    RemoveAll<ProviderSessionOptions>(services);

                    services.AddSingleton(new ProviderSessionOptions
                    {
                        InactivityTtl = TimeSpan.FromMinutes(30),
                        CleanupInterval = TimeSpan.FromHours(1), // don't interfere during tests
                    });
                    services.AddSingleton<IProviderClient>(_providerClient);
                    services.AddSingleton<IProviderSessionService, ProviderSessionService>();
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

    [Test]
    public async Task CreateProvider_ReturnsHandleAndEcho()
    {
        var resp = await _client.PostAsJsonAsync("/api/providers/providers/create",
            new { provider = "anthropic", model = "claude-sonnet-4" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("provider").GetString().Should().Be("anthropic");
        body.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-4");
        var handle = body.RootElement.GetProperty("handle").GetString();
        Guid.TryParse(handle, out _).Should().BeTrue();
    }

    [Test]
    public async Task CreateProvider_BadRequest_WhenProviderMissing()
    {
        var resp = await _client.PostAsJsonAsync("/api/providers/providers/create",
            new { model = "something" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ExecuteProvider_ReturnsContentAndRecordsInvocation()
    {
        var handle = await CreateSessionAsync("anthropic", "claude-sonnet-4");

        var resp = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{handle}/execute",
            new { prompt = "Say hello", maxTokens = 256, temperature = 0.2 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("content").GetString().Should().Be("hello from stub");
        body.RootElement.GetProperty("tokenUsage").GetInt32().Should().Be(17);
        body.RootElement.GetProperty("costUsd").GetDecimal().Should().Be(0.0015m);

        _providerClient.Calls.Should().HaveCount(1);
        _providerClient.Calls[0].Provider.Should().Be("anthropic");
        _providerClient.Calls[0].Model.Should().Be("claude-sonnet-4");
    }

    [Test]
    public async Task ExecuteProvider_InvalidHandleFormat_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/providers/providers/not-a-uuid/execute",
            new { prompt = "hi" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ExecuteProvider_UnknownHandle_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{Guid.NewGuid()}/execute",
            new { prompt = "hi" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ExecuteProvider_MissingInput_Returns400()
    {
        var handle = await CreateSessionAsync("anthropic", "claude-sonnet-4");
        var resp = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{handle}/execute",
            new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ListSessions_ReturnsCreatedSessions()
    {
        await CreateSessionAsync("anthropic", "claude-sonnet-4");
        await CreateSessionAsync("openai", "gpt-4o");

        var resp = await _client.GetAsync("/api/providers/providers/sessions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        var sessions = body.RootElement.GetProperty("sessions");
        sessions.ValueKind.Should().Be(JsonValueKind.Array);
        sessions.EnumerateArray()
            .Select(e => e.GetProperty("provider").GetString())
            .Should().Contain(new[] { "anthropic", "openai" });
    }

    [Test]
    public async Task DeleteProvider_RemovesSession()
    {
        var handle = await CreateSessionAsync("anthropic", "claude-sonnet-4");

        var del = await _client.DeleteAsync($"/api/providers/providers/{handle}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        // Subsequent execute should 404
        var exec = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{handle}/execute",
            new { prompt = "hi" });
        exec.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteProvider_InvalidHandleFormat_Returns400()
    {
        var resp = await _client.DeleteAsync("/api/providers/providers/not-uuid");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DeleteProvider_UnknownHandle_Returns404()
    {
        var resp = await _client.DeleteAsync($"/api/providers/providers/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task FullLifecycle_CreateExecuteListDelete()
    {
        var handle = await CreateSessionAsync("anthropic", "claude-sonnet-4");

        // Execute
        var exec = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{handle}/execute",
            new { input = "hi" });
        exec.EnsureSuccessStatusCode();

        // List — must include our session
        var list = await _client.GetAsync("/api/providers/providers/sessions");
        list.EnsureSuccessStatusCode();
        var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listBody.RootElement.GetProperty("sessions").EnumerateArray()
            .Any(e => e.GetProperty("handle").GetString() == handle).Should().BeTrue();

        // Delete
        var del = await _client.DeleteAsync($"/api/providers/providers/{handle}");
        del.EnsureSuccessStatusCode();

        // Re-execute → 404
        var again = await _client.PostAsJsonAsync(
            $"/api/providers/providers/{handle}/execute",
            new { input = "again" });
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void RemoveAll<T>(IServiceCollection services) =>
        ServiceCollectionDescriptorExtensions.RemoveAll<T>(services);

    private async Task<string> CreateSessionAsync(string provider, string model)
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/providers/providers/create",
            new { provider, model });
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("handle").GetString()!;
    }
}

/// <summary>
/// Test <see cref="IProviderClient"/> returning a pre-configured result. Used
/// so integration tests never reach Anthropic / OpenAI over the network.
/// </summary>
internal sealed class CannedProviderClient : IProviderClient
{
    public ProviderInvocationResult Next { get; set; } =
        new("ok", 1, 0m, 1);

    public List<(string Provider, string Model, ExecuteRequest Req)> Calls { get; } = new();

    public Task<ProviderInvocationResult> InvokeAsync(
        string provider, string model, ExecuteRequest req, CancellationToken ct = default)
    {
        Calls.Add((provider, model, req));
        return Task.FromResult(Next);
    }
}
