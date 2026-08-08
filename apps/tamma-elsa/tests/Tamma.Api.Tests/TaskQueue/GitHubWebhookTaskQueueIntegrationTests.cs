using System.Net;
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

namespace Tamma.Api.Tests.TaskQueue;

/// <summary>
/// Epic 31 P4 M2/M4 — the DELETED deferred-task write, pinned as absent.
///
/// <para>This fixture used to assert the opposite: a signed
/// push / issues / pull_request webhook enqueued a <c>github.*</c> row on the
/// task queue. That write was a DEAD END — no <c>ITaskHandler</c> or
/// platform-task handler ever consumed a <c>github.*</c> task type — and the
/// execution plan (§6) chose the 31-7 webhook-handler route for the
/// merged-PR resume, DELETING the enqueue rather than implementing it so the
/// two paths can never double-resume. These tests keep the deletion honest:
/// the deferred events now return <c>skipped=true</c>, enqueue NOTHING on
/// either queue, and never advertise a <c>taskId</c>.</para>
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
                // Same registrations the old enqueue-era test wired — proving
                // the events stay skipped even WITH the task queue available.
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
        req.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        req.Headers.Add("X-Hub-Signature-256", Sign(body));
        return req;
    }

    private static async Task AssertNothingQueuedAnywhereAsync()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var cp = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        (await cp.PlatformQueuedTasks.AsNoTracking().AnyAsync())
            .Should().BeFalse("the github.* platform-queue write was deleted (Epic 31 P4)");

        var factory = scope.ServiceProvider
            .GetRequiredService<Tamma.Data.Abstractions.ITenantDbContextFactory>();
        foreach (var tid in await cp.Tenants.AsNoTracking()
            .Where(t => t.DeletedAt == null).Select(t => t.Id).ToListAsync())
        {
            await using var tdb = await factory.CreateAsync(tid);
            (await tdb.QueuedTasks.AnyAsync()).Should().BeFalse(
                "the github.* tenant-queue write was deleted (Epic 31 P4)");
        }
    }

    [TestCase("push", """
        {
          "ref": "refs/heads/main",
          "installation": { "id": 9001 },
          "repository": { "full_name": "acme/app" }
        }
        """)]
    [TestCase("issues", """
        {
          "action": "opened",
          "installation": { "id": 9002 },
          "issue": { "number": 7, "title": "It broke" }
        }
        """)]
    [TestCase("pull_request", """
        {
          "action": "opened",
          "installation": { "id": 9003 },
          "pull_request": { "number": 11 }
        }
        """)]
    public async Task DeferredEvents_AreSkipped_AndEnqueueNothing(string @event, string body)
    {
        using var client = CreateClient();

        var response = await client.SendAsync(BuildWebhookRequest(@event, body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("skipped").GetBoolean().Should().BeTrue(
            "push/issues/pull_request no longer defer to the task queue — the "
            + "31-7 webhook handlers own inbound processing");
        doc.RootElement.TryGetProperty("taskId", out _).Should().BeFalse(
            "no task id may be advertised for a write that no longer happens");

        await AssertNothingQueuedAnywhereAsync();
    }

    // ─── installation event still handled inline (unchanged behavior) ────────

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

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"skipped\":false", "installation events process inline");

        await AssertNothingQueuedAnywhereAsync();
    }

    [Test]
    public async Task Webhook_UnknownEvent_StillReturnsSkipped_NotQueued()
    {
        using var client = CreateClient();
        const string body = """{ "foo": "bar" }""";

        var response = await client.SendAsync(BuildWebhookRequest("workflow_run", body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"skipped\":true");

        await AssertNothingQueuedAnywhereAsync();
    }
}
