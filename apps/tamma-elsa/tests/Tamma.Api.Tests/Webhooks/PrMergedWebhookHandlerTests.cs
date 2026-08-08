using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Webhooks.Handlers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Webhooks;

/// <summary>
/// Epic 31 P4 M2 (DG-6) — the handler half of the merged-PR resume flow.
///
/// <para><b>Red-first claim (the plan's acceptance).</b> "Replaying a
/// recorded merged-PR webhook against a suspended cycle resumes
/// WaitForPRMerged on the Merged edge with mergeSha — fails today for every
/// platform." Before this milestone NO handler mapped any platform's
/// merged-PR event to any resume call:
/// <see cref="GitHub_ClosedMerged_ResumesTheWait_WithMergeSha"/> (and its
/// Gitea/GitLab siblings) fail on the pre-milestone tree because the handler
/// class does not exist and the dispatcher inventory has no PullRequest
/// binding. The engine half (bookmark lookup, Merged-edge input injection,
/// naming transition) is pinned in
/// <c>Tamma.Activities.Tests.Endpoints.PrMergedResumeEndpointTests</c>.</para>
/// </summary>
[TestFixture]
public class PrMergedWebhookHandlerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private RecordingHandler _engine = null!;
    private RecordingEventRepository _events = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new RecordingHandler();
        _events = new RecordingEventRepository();
    }

    [TearDown]
    public void TearDown() => _engine.Dispose();

    private PrMergedWebhookHandler Build(PlatformKind kind, string pattern)
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient(PrMergedWebhookHandler.ElsaClientName))
            .Returns(() => new HttpClient(_engine) { BaseAddress = new Uri("http://engine.test") });

        var services = new ServiceCollection();
        services.AddSingleton<IEventRepository>(_events);
        var provider = services.BuildServiceProvider();

        return new PrMergedWebhookHandler(
            httpFactory.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            kind,
            pattern,
            NullLogger<PrMergedWebhookHandler>.Instance);
    }

    private static PlatformWebhookEvent Event(
        PlatformKind kind, string eventType, string? action, string bodyJson,
        Guid? tenantId, string? repo)
    {
        var bytes = Encoding.UTF8.GetBytes(bodyJson);
        using var doc = JsonDocument.Parse(bodyJson);
        return new PlatformWebhookEvent(
            Kind: kind,
            EventType: eventType,
            Action: action,
            Category: WebhookEventCategory.PullRequest,
            DeliveryId: Guid.NewGuid().ToString(),
            InstallationExternalId: "42",
            RepoFullName: repo,
            TenantId: tenantId,
            Installation: null,
            RawBody: bytes,
            ParsedJson: doc.RootElement.Clone());
    }

    // ================================================================
    // GitHub — pull_request.closed(merged=true)
    // ================================================================

    [Test]
    public async Task GitHub_ClosedMerged_ResumesTheWait_WithMergeSha()
    {
        var handler = Build(PlatformKind.GitHub, "pull_request.closed");
        var evt = Event(PlatformKind.GitHub, "pull_request", "closed",
            """
            {
              "action": "closed",
              "number": 55,
              "pull_request": { "number": 55, "merged": true, "merge_commit_sha": "abc123" },
              "repository": { "full_name": "acme/widgets" }
            }
            """,
            Tenant, "acme/widgets");

        await handler.HandleAsync(evt);

        var post = _engine.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be(PrMergedWebhookHandler.ResumePath);
        var body = JsonDocument.Parse(post.Body).RootElement;
        body.GetProperty("prNumber").GetInt32().Should().Be(55);
        body.GetProperty("mergeSha").GetString().Should().Be("abc123");
        body.GetProperty("tenantId").GetString().Should().Be(Tenant.ToString());
        body.GetProperty("repository").GetString().Should().Be("acme/widgets");

        _events.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(PrMergedWebhookHandler.WaitResumedEventType);
    }

    [Test]
    public async Task GitHub_ClosedWithoutMerge_DoesNotResume()
    {
        var handler = Build(PlatformKind.GitHub, "pull_request.closed");
        var evt = Event(PlatformKind.GitHub, "pull_request", "closed",
            """
            {
              "action": "closed",
              "number": 55,
              "pull_request": { "number": 55, "merged": false, "merge_commit_sha": null },
              "repository": { "full_name": "acme/widgets" }
            }
            """,
            Tenant, "acme/widgets");

        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty("a close-without-merge is not a merge confirmation");
        _events.Appended.Should().BeEmpty();
    }

    // ================================================================
    // Gitea / Forgejo — pull_request.closed with merged=true
    // ================================================================

    [Test]
    public async Task Gitea_ClosedMerged_ResumesTheWait_WithMergedCommitId()
    {
        var handler = Build(PlatformKind.Gitea, "pull_request.closed");
        var evt = Event(PlatformKind.Gitea, "pull_request", "closed",
            """
            {
              "action": "closed",
              "number": 7,
              "pull_request": { "number": 7, "merged": true, "merged_commit_id": "feed42" },
              "repository": { "full_name": "acme/forge" }
            }
            """,
            Tenant, "acme/forge");

        await handler.HandleAsync(evt);

        var body = JsonDocument.Parse(_engine.Posts.Single().Body).RootElement;
        body.GetProperty("prNumber").GetInt32().Should().Be(7);
        body.GetProperty("mergeSha").GetString().Should().Be("feed42",
            "Gitea/Forgejo name the merge commit 'merged_commit_id'");
    }

    // ================================================================
    // GitLab — merge_request action=merge
    // ================================================================

    [Test]
    public async Task GitLab_MergeAction_ResumesTheWait_WithIidAndSha()
    {
        var handler = Build(PlatformKind.GitLab, "merge_request.merge");
        var evt = Event(PlatformKind.GitLab, "merge_request", "merge",
            """
            {
              "object_attributes": { "iid": 12, "action": "merge", "merge_commit_sha": "cafe99" },
              "project": { "path_with_namespace": "group/app" }
            }
            """,
            Tenant, "group/app");

        await handler.HandleAsync(evt);

        var body = JsonDocument.Parse(_engine.Posts.Single().Body).RootElement;
        body.GetProperty("prNumber").GetInt32().Should().Be(12, "GitLab's PR number is the MR iid");
        body.GetProperty("mergeSha").GetString().Should().Be("cafe99");
        body.GetProperty("repository").GetString().Should().Be("group/app");
    }

    [Test]
    public async Task GitLab_NonMergeAction_DoesNotResume()
    {
        var handler = Build(PlatformKind.GitLab, "merge_request.merge");
        var evt = Event(PlatformKind.GitLab, "merge_request", "close",
            """
            {
              "object_attributes": { "iid": 12, "action": "close" },
              "project": { "path_with_namespace": "group/app" }
            }
            """,
            Tenant, "group/app");

        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty();
    }

    // ================================================================
    // Degradation + idempotency
    // ================================================================

    [Test]
    public async Task NoSuspendedWait_Engine404_IsBenign_NoEventNoThrow()
    {
        // Most merges are not Tamma cycles; and a duplicate delivery racing
        // the SLA edge meets a burned bookmark. Both answer 404 = no-op.
        _engine.PostStatus = HttpStatusCode.NotFound;
        var handler = Build(PlatformKind.GitHub, "pull_request.closed");
        var evt = Event(PlatformKind.GitHub, "pull_request", "closed",
            """{"action":"closed","number":55,"pull_request":{"number":55,"merged":true,"merge_commit_sha":"abc"},"repository":{"full_name":"acme/widgets"}}""",
            Tenant, "acme/widgets");

        await handler.Invoking(h => h.HandleAsync(evt)).Should().NotThrowAsync();
        _events.Appended.Should().BeEmpty("no audit event for a resume that did not happen");
    }

    [Test]
    public async Task MissingRepository_DoesNotResume()
    {
        var handler = Build(PlatformKind.GitHub, "pull_request.closed");
        var evt = Event(PlatformKind.GitHub, "pull_request", "closed",
            """{"action":"closed","number":55,"pull_request":{"number":55,"merged":true}}""",
            Tenant, repo: null);

        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty(
            "without a repo slug the engine could not scope the bookmark name — refuse rather than guess");
    }

    // ================================================================
    // Plumbing (the WebhookHandlerTests fakes, duplicated locally to keep
    // the fixtures independent).
    // ================================================================

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;
        public List<(string Path, string Body)> Posts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Posts.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(PostStatus)
            {
                Content = new StringContent(
                    "{\"resumed\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
