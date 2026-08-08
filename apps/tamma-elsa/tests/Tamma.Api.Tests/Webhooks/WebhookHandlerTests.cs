using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.GitHub;
using Tamma.Api.Services.Webhooks;
using Tamma.Api.Services.Webhooks.Handlers;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Api.Tests.Webhooks;

/// <summary>
/// Epic 31 P4 M1 — the FIRST production webhook handlers on the 31-7
/// dispatcher.
///
/// <para><b>Red-first claim.</b> Before this milestone the dispatcher had
/// ZERO registered handlers — every verified delivery on
/// <c>/api/webhooks/{platform}</c> was verified, deduped, and then dropped
/// (dispatched=0). <see cref="Registration_ProductionDispatcher_HasHandlers"/>
/// fails on the pre-milestone tree (HandlerCount == 0) and passes now.</para>
/// </summary>
[TestFixture]
public class WebhookHandlerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    // ================================================================
    // Registration — the production DI wiring registers real handlers.
    // ================================================================

    [Test]
    public void Registration_ProductionDispatcher_HasHandlers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        // The receiver extension also registers the delivery repo (scoped,
        // DbContext-backed) — not resolved by this test.
        services.AddTammaWebhookReceiver();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IWebhookEventDispatcher>();

        // 2 GitHub installation patterns + 4 CI-wake handlers
        // (GitHub, Gitea, Forgejo, GitLab).
        dispatcher.HandlerCount.Should().Be(6,
            "P4 M1 registers the first production handlers; before this milestone "
            + "the dispatcher had ZERO and verified deliveries were dropped");
    }

    [Test]
    public void Registration_HandlerKindsAndPatterns_AreTheDocumentedInventory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddTammaWebhookReceiver();

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<IWebhookHandler>()
            .Select(h => (h.Kind, h.EventTypePattern))
            .ToArray();

        handlers.Should().BeEquivalentTo(new[]
        {
            (PlatformKind.GitHub, "installation.*"),
            (PlatformKind.GitHub, "installation_repositories.*"),
            (PlatformKind.GitHub, "workflow_run.completed"),
            (PlatformKind.Gitea, "workflow_run"),
            (PlatformKind.Forgejo, "workflow_run"),
            (PlatformKind.GitLab, "pipeline"),
        });
    }

    // ================================================================
    // (a) GitHub installation handler — ports the legacy route's
    // behavior by delegating to the SAME IInstallationRouterService.
    // ================================================================

    [Test]
    public async Task InstallationHandler_DelegatesToTheLegacyRouter()
    {
        var router = new Mock<IInstallationRouterService>(MockBehavior.Strict);
        router
            .Setup(r => r.HandleWebhookAsync("installation", It.IsAny<JsonElement>()))
            .ReturnsAsync(new WebhookResult("installation", "created", Skipped: false));

        var services = new ServiceCollection();
        services.AddSingleton(router.Object);
        using var provider = services.BuildServiceProvider();

        var handler = new GitHubInstallationWebhookHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            "installation.*",
            NullLogger<GitHubInstallationWebhookHandler>.Instance);

        var evt = Event(PlatformKind.GitHub, "installation", "created",
            """{"action":"created","installation":{"id":42}}""");
        await handler.HandleAsync(evt);

        router.Verify(
            r => r.HandleWebhookAsync("installation", It.IsAny<JsonElement>()),
            Times.Once);
    }

    [Test]
    public async Task InstallationHandler_RouterUnregistered_SkipsWithoutThrowing()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        var handler = new GitHubInstallationWebhookHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            "installation.*",
            NullLogger<GitHubInstallationWebhookHandler>.Instance);

        var evt = Event(PlatformKind.GitHub, "installation", "deleted",
            """{"action":"deleted","installation":{"id":42}}""");

        await handler.Invoking(h => h.HandleAsync(evt)).Should().NotThrowAsync();
    }

    // ================================================================
    // (b) CI-run completion wake — the DG-5 webhook accelerator.
    // ================================================================

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

    private CiRunCompletionWebhookHandler BuildCiHandler(
        PlatformKind kind, string pattern)
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient(CiRunCompletionWebhookHandler.ElsaClientName))
            .Returns(() => new HttpClient(_engine) { BaseAddress = new Uri("http://engine.test") });

        var services = new ServiceCollection();
        services.AddSingleton<IEventRepository>(_events);
        var provider = services.BuildServiceProvider();

        return new CiRunCompletionWebhookHandler(
            httpFactory.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            kind,
            pattern,
            NullLogger<CiRunCompletionWebhookHandler>.Instance);
    }

    private void EngineListsWait(
        string bookmarkId = "bm-1",
        string runId = "42",
        string repo = "acme/widgets",
        string? tenantId = null) =>
        _engine.OnGet = _ => Json(new
        {
            waits = new[]
            {
                new
                {
                    bookmarkId,
                    workflowInstanceId = "wf-1",
                    sessionId = Guid.NewGuid(),
                    runId,
                    repository = repo,
                    tenantId = tenantId ?? Tenant.ToString(),
                },
            },
        });

    [Test]
    public async Task GitHubWorkflowRunCompleted_WakesTheMatchingWait()
    {
        EngineListsWait();
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "completed",
            """{"action":"completed","workflow_run":{"id":42,"status":"completed","conclusion":"success"},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: Tenant, repoFullName: "acme/widgets");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().HaveCount(1, "the terminal run wakes the suspended wait");
        var body = JsonDocument.Parse(_engine.Posts.Single().Body).RootElement;
        body.GetProperty("bookmarkId").GetString().Should().Be("bm-1");
        body.GetProperty("status").GetString().Should().Be("success");
        body.GetProperty("buildPassed").GetBoolean().Should().BeTrue();

        _events.Appended.Should().ContainSingle()
            .Which.Type.Should().Be("CI.WAIT.RESUMED");
    }

    [Test]
    public async Task NonTerminalRun_DoesNotWake()
    {
        EngineListsWait();
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "in_progress",
            """{"action":"in_progress","workflow_run":{"id":42,"status":"in_progress","conclusion":null},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: Tenant, repoFullName: "acme/widgets");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty("a null conclusion is not terminal");
    }

    [Test]
    public async Task TenantMismatch_RefusesToResume_TheCrossTenantGuard()
    {
        // The wait belongs to ANOTHER tenant; the delivery resolved to Tenant.
        EngineListsWait(tenantId: Guid.NewGuid().ToString());
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "completed",
            """{"action":"completed","workflow_run":{"id":42,"conclusion":"success"},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: Tenant, repoFullName: "acme/widgets");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty(
            "cross-tenant resume is the plan's named risk — a webhook resolved to "
            + "tenant A must never resume tenant B's wait");
        _events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task SingleUserWait_NoTenantOnEitherSide_Wakes()
    {
        EngineListsWait(tenantId: "");
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "completed",
            """{"action":"completed","workflow_run":{"id":42,"conclusion":"failure"},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: null, repoFullName: "acme/widgets");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().HaveCount(1);
        JsonDocument.Parse(_engine.Posts.Single().Body).RootElement
            .GetProperty("buildPassed").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task GitLabPipelineSuccess_WakesTheMatchingWait()
    {
        EngineListsWait(runId: "9001", repo: "group/app");
        var handler = BuildCiHandler(PlatformKind.GitLab, "pipeline");

        var evt = Event(PlatformKind.GitLab, "pipeline", null,
            """{"object_attributes":{"id":9001,"status":"success"},"project":{"path_with_namespace":"group/app"}}""",
            tenantId: Tenant, repoFullName: "group/app");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().HaveCount(1);
        var body = JsonDocument.Parse(_engine.Posts.Single().Body).RootElement;
        body.GetProperty("runId").GetString().Should().Be("9001");
        body.GetProperty("buildPassed").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task GitLabPipelineRunning_DoesNotWake()
    {
        EngineListsWait(runId: "9001", repo: "group/app");
        var handler = BuildCiHandler(PlatformKind.GitLab, "pipeline");

        var evt = Event(PlatformKind.GitLab, "pipeline", null,
            """{"object_attributes":{"id":9001,"status":"running"},"project":{"path_with_namespace":"group/app"}}""",
            tenantId: Tenant, repoFullName: "group/app");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty("'running' is not a terminal pipeline status");
    }

    [Test]
    public async Task LateWake_Resume404_IsBenign_NoEventNoThrow()
    {
        // The poller (or the timeout edge) burned the bookmark first: the
        // engine answers 404 and the webhook wake is a no-op — never a
        // double-advance, never an error.
        EngineListsWait();
        _engine.PostStatus = HttpStatusCode.NotFound;
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "completed",
            """{"action":"completed","workflow_run":{"id":42,"conclusion":"success"},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: Tenant, repoFullName: "acme/widgets");

        await handler.Invoking(h => h.HandleAsync(evt)).Should().NotThrowAsync();
        _events.Appended.Should().BeEmpty("no audit event for a resume that did not happen");
    }

    [Test]
    public async Task RepoMismatch_DoesNotWake()
    {
        EngineListsWait(repo: "acme/other");
        var handler = BuildCiHandler(PlatformKind.GitHub, "workflow_run.completed");

        var evt = Event(PlatformKind.GitHub, "workflow_run", "completed",
            """{"action":"completed","workflow_run":{"id":42,"conclusion":"success"},"repository":{"full_name":"acme/widgets"}}""",
            tenantId: Tenant, repoFullName: "acme/widgets");
        await handler.HandleAsync(evt);

        _engine.Posts.Should().BeEmpty();
    }

    // ================================================================
    // Plumbing
    // ================================================================

    private static PlatformWebhookEvent Event(
        PlatformKind kind,
        string eventType,
        string? action,
        string bodyJson,
        Guid? tenantId = null,
        string? repoFullName = null)
    {
        var bytes = Encoding.UTF8.GetBytes(bodyJson);
        using var doc = JsonDocument.Parse(bodyJson);
        return new PlatformWebhookEvent(
            Kind: kind,
            EventType: eventType,
            Action: action,
            Category: WebhookEventCategory.Unknown,
            DeliveryId: Guid.NewGuid().ToString(),
            InstallationExternalId: "42",
            RepoFullName: repoFullName,
            TenantId: tenantId,
            Installation: null,
            RawBody: bytes,
            ParsedJson: doc.RootElement.Clone());
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnGet { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;
        public List<(string Path, string Body)> Posts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return OnGet(request);
            }

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
