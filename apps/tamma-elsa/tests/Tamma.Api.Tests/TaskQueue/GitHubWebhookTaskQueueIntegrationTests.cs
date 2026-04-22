using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// End-to-end test: posts a signed GitHub webhook to the real API
/// (Testcontainers Postgres via <see cref="ApiTestFixture"/>) and asserts a
/// <c>queued_tasks</c> row appears with the expected type + payload. This
/// replaces the TypeScript dispatch path that previously enqueued
/// <c>push</c>/<c>issues</c>/<c>pull_request</c> events.
/// </summary>
[TestFixture]
public class GitHubWebhookTaskQueueIntegrationTests
{
    private const string WebhookSecret = "test-webhook-secret-value";

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static HttpClient CreateClient()
        => ApiTestFixture.Factory.WithWebHostBuilder(b =>
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
                // The router needs both the installation services and the new
                // task-queue services so the three deferred events land in
                // queued_tasks instead of being skipped.
                services.AddGitHubInstallationServices();
                services.AddTaskQueue();
            });
        }).CreateClient();

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

    // ─── push event → queued row ──────────────────────────────────────────────

    [Test]
    public async Task Webhook_PushEvent_EnqueuesQueuedTaskRow()
    {
        using var client = CreateClient();
        const string body = """
            {
              "ref": "refs/heads/main",
              "installation": { "id": 9001 },
              "repository": { "full_name": "acme/app" }
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("push", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("queued").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("taskId").GetString().Should().NotBeNullOrEmpty();

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var task = await db.QueuedTasks.FirstOrDefaultAsync();
        task.Should().NotBeNull();
        task!.Type.Should().StartWith("github.push");
        task.InstallationId.Should().Be(9001L);
        task.Status.Should().Be("pending");
        task.Payload.Should().Contain("refs/heads/main");
    }

    // ─── issues.opened → queued ──────────────────────────────────────────────

    [Test]
    public async Task Webhook_IssuesOpened_EnqueuesQueuedTaskRow()
    {
        using var client = CreateClient();
        const string body = """
            {
              "action": "opened",
              "installation": { "id": 9002 },
              "issue": { "number": 7, "title": "It broke" }
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("issues", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var task = await db.QueuedTasks.FirstOrDefaultAsync(t => t.InstallationId == 9002L);
        task.Should().NotBeNull();
        task!.Type.Should().Be("github.issues.opened");
    }

    // ─── pull_request.opened → queued ────────────────────────────────────────

    [Test]
    public async Task Webhook_PullRequestOpened_EnqueuesQueuedTaskRow()
    {
        using var client = CreateClient();
        const string body = """
            {
              "action": "opened",
              "installation": { "id": 9003 },
              "pull_request": { "number": 42 }
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("pull_request", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var task = await db.QueuedTasks.FirstOrDefaultAsync(t => t.InstallationId == 9003L);
        task.Should().NotBeNull();
        task!.Type.Should().Be("github.pull_request.opened");
    }

    // ─── installation event still handled inline (not queued) ────────────────

    [Test]
    public async Task Webhook_InstallationEvent_StillHandledInline_NotQueued()
    {
        using var client = CreateClient();
        const string body = """
            {
              "action": "created",
              "installation": {
                "id": 9999,
                "app_id": 42,
                "account": { "login": "acme", "type": "Organization" }
              }
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("installation", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        // installation events should NOT land in queued_tasks — they are handled
        // inline by the existing installation router code path.
        var anyQueued = await db.QueuedTasks.AnyAsync();
        anyQueued.Should().BeFalse();
    }

    // ─── unknown event still skipped ──────────────────────────────────────────

    [Test]
    public async Task Webhook_UnknownEvent_StillReturnsSkipped_NotQueued()
    {
        using var client = CreateClient();
        const string body = """{ "foo": "bar" }""";

        var response = await client.SendAsync(BuildWebhookRequest("workflow_run", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"skipped\":true");

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        (await db.QueuedTasks.AnyAsync()).Should().BeFalse();
    }

    // ─── tenant binding via installation ──────────────────────────────────────

    [Test]
    public async Task Webhook_PushEvent_BindsTenant_WhenInstallationHasTenant()
    {
        // Program.cs runs the Tamma-tables wipe-and-migrate whenever the web
        // host boots; WithWebHostBuilder may trigger a fresh boot even when the
        // parent factory already started. Seeding AFTER CreateClient() has
        // forced that boot guarantees the seeded rows survive to the webhook
        // call, regardless of factory caching semantics.
        using var client = CreateClient();

        var tenantId = Guid.NewGuid();
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.Tenants.Add(new Data.Entities.Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = "acme-" + Guid.NewGuid().ToString("N")[..8]
            });
            db.GitHubInstallations.Add(new Data.Entities.GitHubInstallation
            {
                Id = Guid.NewGuid(),
                InstallationId = 7777,
                AccountLogin = "acme",
                AccountType = "Organization",
                AppId = 1,
                TenantId = tenantId
            });
            await db.SaveChangesAsync();
        }

        const string body = """
            {
              "ref": "refs/heads/main",
              "installation": { "id": 7777 }
            }
            """;

        var response = await client.SendAsync(BuildWebhookRequest("push", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var assertScope = ApiTestFixture.Factory.Services.CreateScope();
        var adb = assertScope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var queued = await adb.QueuedTasks.FirstOrDefaultAsync(t => t.InstallationId == 7777L);
        queued.Should().NotBeNull();
        queued!.TenantId.Should().Be(tenantId);
    }
}
