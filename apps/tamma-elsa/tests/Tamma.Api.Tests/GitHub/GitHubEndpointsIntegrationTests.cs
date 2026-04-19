using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Integration tests for the GitHub App router endpoints. Boots the real API
/// against a Testcontainers Postgres, posts signed webhooks, and asserts
/// state changes in the DB + event log.
/// </summary>
[TestFixture]
public class GitHubEndpointsIntegrationTests
{
    private const string WebhookSecret = "test-webhook-secret-value";

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static HttpClient CreateClient()
    {
        // Ensure the factory has a known webhook secret for signature tests,
        // and wire up the installation router service (parent Program.cs will
        // add this once Phase 2 integration lands; in tests we register here).
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitHub:WebhookSecret"] = WebhookSecret
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddGitHubInstallationServices();
            });
        }).CreateClient();
    }

    private static string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpRequestMessage BuildWebhookRequest(string @event, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhooks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-GitHub-Event", @event);
        req.Headers.Add("X-Hub-Signature-256", Sign(body));
        return req;
    }

    // ─── HMAC signature tests ─────────────────────────────────────────────────

    [Test]
    public async Task Webhook_MissingSignature_Returns401()
    {
        using var client = CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhooks")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-GitHub-Event", "ping");

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Webhook_InvalidSignature_Returns401()
    {
        using var client = CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhooks")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Webhook_NoSecretConfigured_Returns401()
    {
        // Audit finding 001 (P0). When GitHub:WebhookSecret is empty, the
        // handler must reject every webhook outright instead of silently
        // skipping verification.
        var noSecretClient = ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitHub:WebhookSecret"] = ""
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddGitHubInstallationServices();
            });
        }).CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhooks")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-Hub-Signature-256", "sha256=anything");

        var response = await noSecretClient.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Webhook_ValidSignature_MissingEvent_Returns400()
    {
        using var client = CreateClient();
        var body = "{}";
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhooks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Hub-Signature-256", Sign(body));

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── installation.created persists ────────────────────────────────────────

    [Test]
    public async Task Webhook_InstallationCreated_PersistsInstallationAndEvent()
    {
        using var client = CreateClient();
        var body = """
            {
              "action": "created",
              "installation": {
                "id": 5550001,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" },
                "permissions": { "issues": "write" }
              },
              "repositories": [
                { "id": 701, "full_name": "acme/repo-one" }
              ]
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("installation", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var installRepo = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventRepository>();

        var install = await installRepo.GetByInstallationIdAsync(5550001L);
        install.Should().NotBeNull();
        install!.AccountLogin.Should().Be("acme");
        install.Repos.Should().ContainSingle(r => r.RepoId == 701L && r.RepoFullName == "acme/repo-one");

        var events = await eventRepo.QueryAsync(null, "INSTALLATION.CREATED.SUCCESS", null, 10);
        events.Should().ContainSingle();
    }

    // ─── installation.deleted soft-deletes ────────────────────────────────────

    [Test]
    public async Task Webhook_InstallationDeleted_HardDeletesInstallation()
    {
        using var client = CreateClient();

        // Arrange: seed an existing installation via create webhook
        await client.SendAsync(BuildWebhookRequest("installation", """
            {
              "action": "created",
              "installation": {
                "id": 5550002,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """));

        // Act
        var deleteBody = """
            {
              "action": "deleted",
              "installation": {
                "id": 5550002,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """;
        var response = await client.SendAsync(BuildWebhookRequest("installation", deleteBody));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: hard-deleted (audit finding 030) — the row is gone but the
        // INSTALLATION.DELETED.SUCCESS event preserves audit. Soft-delete via
        // SuspendedAt collided with the suspend/unsuspend lifecycle.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var installation = await db.GitHubInstallations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.InstallationId == 5550002L);
        installation.Should().BeNull("deleted installations should be removed; audit lives in the event store");

        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var events = await eventRepo.QueryAsync(null, "INSTALLATION.DELETED.SUCCESS", null, 10);
        events.Should().ContainSingle();
    }

    // ─── installation.suspend flips SuspendedAt ───────────────────────────────

    [Test]
    public async Task Webhook_InstallationSuspend_FlipsSuspendedAt()
    {
        using var client = CreateClient();

        await client.SendAsync(BuildWebhookRequest("installation", """
            {
              "action": "created",
              "installation": {
                "id": 5550003,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """));

        var response = await client.SendAsync(BuildWebhookRequest("installation", """
            {
              "action": "suspend",
              "installation": {
                "id": 5550003,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var installRepo = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        var install = await installRepo.GetByInstallationIdAsync(5550003L);
        install.Should().NotBeNull();
        install!.SuspendedAt.Should().NotBeNull();
    }

    // ─── installation_repositories.added inserts rows ─────────────────────────

    [Test]
    public async Task Webhook_InstallationRepositoriesAdded_InsertsRepoRows()
    {
        using var client = CreateClient();

        await client.SendAsync(BuildWebhookRequest("installation", """
            {
              "action": "created",
              "installation": {
                "id": 5550004,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """));

        var response = await client.SendAsync(BuildWebhookRequest("installation_repositories", """
            {
              "action": "added",
              "installation": { "id": 5550004 },
              "repositories_added": [
                { "id": 801, "full_name": "acme/new-repo" },
                { "id": 802, "full_name": "acme/another-repo" }
              ]
            }
            """));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var installRepo = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        var install = await installRepo.GetByInstallationIdAsync(5550004L);
        install!.Repos.Should().HaveCount(2);
        install.Repos.Should().Contain(r => r.RepoId == 801L && r.RepoFullName == "acme/new-repo");
        install.Repos.Should().Contain(r => r.RepoId == 802L && r.RepoFullName == "acme/another-repo");
    }

    // ─── Ignored event returns ok + skipped:true ─────────────────────────────

    [Test]
    public async Task Webhook_IgnoredEvent_ReturnsOkWithSkipped()
    {
        // After the task-queue workstream merged, `issues`/`push`/`pull_request`
        // events are now queued (not skipped). Pick an event that has no
        // dispatch branch — `meta` — to assert the skipped path.
        using var client = CreateClient();
        var body = """{"action":"deleted","hook_id":1}""";
        var response = await client.SendAsync(BuildWebhookRequest("meta", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"skipped\":true");
    }

    // ─── Callback endpoint ───────────────────────────────────────────────────

    [Test]
    public async Task Callback_MissingInstallationId_Returns400()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/github/callback?setup_action=install");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
