using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.Ci;

/// <summary>
/// Epic 31 P3 Milestone 2 (DG-5) — the CI completion poller.
///
/// <para><b>Red-first claim.</b> Before this milestone NOTHING in the tree
/// resumed the CI-result bookmark — the execution plan's "live hole": the only
/// exit from <c>WaitForCIResultsActivity</c> was the 30-minute timeout edge, on
/// every platform including GitHub.
/// <see cref="TerminalRun_ResumesTheSuspendedWait_BeforeTheTimeoutSla"/> is the
/// test that FAILS against the pre-milestone tree (the poller class does not
/// exist; no resume call is ever made) and passes after: a completed run
/// resumes the wait via the engine seam within one poll tick — minutes, not
/// the 30m SLA.</para>
///
/// <para>The poller's collaborators are all faked: the engine's list/resume
/// endpoints (recording HTTP handler), the platform plane
/// (<see cref="IPlatformResolver"/> → mocked <see cref="IGitPlatformActionsClient"/>),
/// and the DCB event sink.</para>
/// </summary>
[TestFixture]
public class CiCompletionPollerServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Session = Guid.NewGuid();

    private RecordingHandler _engine = null!;
    private Mock<IPlatformResolver> _resolver = null!;
    private Mock<IGitPlatformActionsClient> _actions = null!;
    private RecordingEventRepository _events = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new RecordingHandler();
        _resolver = new Mock<IPlatformResolver>(MockBehavior.Loose);
        _actions = new Mock<IGitPlatformActionsClient>(MockBehavior.Loose);
        _events = new RecordingEventRepository();
    }

    [TearDown]
    public void TearDown() => _engine.Dispose();

    private Tamma.Api.Services.Ci.CiCompletionPollerService BuildSut()
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(f => f.CreateClient(Tamma.Api.Services.Ci.CiCompletionPollerService.ElsaClientName))
            .Returns(() => new HttpClient(_engine) { BaseAddress = new Uri("http://engine.test") });

        var services = new ServiceCollection();
        services.AddSingleton(_resolver.Object);
        services.AddSingleton<IEventRepository>(_events);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Ci:CompletionPoll:IntervalSeconds"] = "5" }).Build();

        return new Tamma.Api.Services.Ci.CiCompletionPollerService(
            httpFactory.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<Tamma.Api.Services.Ci.CiCompletionPollerService>.Instance);
    }

    private void EngineListsOneWait(string bookmarkId = "bm-1", string runId = "42", string repo = "acme/widgets") =>
        _engine.OnGet = _ => Json(new
        {
            waits = new[]
            {
                new
                {
                    bookmarkId,
                    workflowInstanceId = "wf-1",
                    sessionId = Session,
                    runId,
                    repository = repo,
                    tenantId = Tenant.ToString(),
                },
            },
        });

    private void DriverResolves(IGitPlatformActionsClient? actions) => _resolver
        .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediationDriverResolution(
            new FakeDriver(actions), MediationCredentialSource.TenantInstallation));

    private void RunStatus(string runId, string status, string? conclusion) => _actions
        .Setup(a => a.GetRunStatusAsync("acme", "widgets", runId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(PlatformResult<WorkflowRun>.FromOk(new WorkflowRun(
            runId, status, conclusion, "https://ci/run", DateTimeOffset.UtcNow,
            conclusion is null ? null : DateTimeOffset.UtcNow, null)));

    // ================================================================
    // THE red-first test (DG-5): the wait resumes on completion, not
    // via the 30m timeout SLA.
    // ================================================================

    [Test]
    public async Task TerminalRun_ResumesTheSuspendedWait_BeforeTheTimeoutSla()
    {
        EngineListsOneWait();
        DriverResolves(_actions.Object);
        RunStatus("42", "completed", "success");

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(1,
            "a completed run must resume the suspended CI wait within ONE poll tick — "
            + "pre-milestone, nothing resumed this bookmark and only the 30m timeout ended the wait");

        var resume = _engine.Posts.Should().ContainSingle().Subject;
        resume.Path.Should().Be(Tamma.Api.Services.Ci.CiCompletionPollerService.ResumePath);
        var body = JsonDocument.Parse(resume.Body).RootElement;
        body.GetProperty("bookmarkId").GetString().Should().Be("bm-1");
        body.GetProperty("status").GetString().Should().Be("success");
        body.GetProperty("buildPassed").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task TerminalFailure_ResumesWithBuildPassedFalse()
    {
        EngineListsOneWait();
        DriverResolves(_actions.Object);
        RunStatus("42", "completed", "failure");

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(1);
        var body = JsonDocument.Parse(_engine.Posts.Single().Body).RootElement;
        body.GetProperty("status").GetString().Should().Be("failure");
        body.GetProperty("buildPassed").GetBoolean().Should().BeFalse(
            "a failed conclusion must resume the FAIL path, never read as a pass");
    }

    [Test]
    public async Task SuccessfulResume_EmitsOneWaitResumedAuditEvent()
    {
        EngineListsOneWait();
        DriverResolves(_actions.Object);
        RunStatus("42", "completed", "success");

        await BuildSut().PollOnceAsync(CancellationToken.None);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(Tamma.Api.Services.Ci.CiCompletionPollerService.WaitResumedEventType);
        evt.TenantId.Should().Be(Tenant);
        evt.Tags.Should().Contain("acme/widgets").And.Contain("42");
    }

    // ================================================================
    // Not-yet-terminal / no-driver / race — the wait keeps its SLA
    // ================================================================

    [Test]
    public async Task RunStillInProgress_DoesNotResume()
    {
        EngineListsOneWait();
        DriverResolves(_actions.Object);
        RunStatus("42", "in_progress", conclusion: null);

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(0);
        _engine.Posts.Should().BeEmpty("an in-progress run must not resume the wait");
        _events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task NoResolvableDriver_SkipsTheWait_TimeoutSlaRemainsTheBackstop()
    {
        EngineListsOneWait();
        _resolver
            .Setup(r => r.ResolveForMediationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediationDriverResolution?)null);

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(0);
        _engine.Posts.Should().BeEmpty();
    }

    [Test]
    public async Task DriverWithoutActionsSurface_SkipsTheWait()
    {
        EngineListsOneWait();
        DriverResolves(actions: null);

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(0);
        _engine.Posts.Should().BeEmpty();
    }

    [Test]
    public async Task ResumeAnswers404_TimeoutWonTheRace_BenignNoOp_NoEvent()
    {
        // The idempotency guard: the engine burned the bookmark (timeout edge
        // or a sibling tick) — a late resume is a no-op, never a double-advance
        // and never an error.
        EngineListsOneWait();
        DriverResolves(_actions.Object);
        RunStatus("42", "completed", "success");
        _engine.PostStatus = HttpStatusCode.NotFound;

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(0);
        _events.Appended.Should().BeEmpty("no audit event for a resume that did not happen");
    }

    [Test]
    public async Task EngineUnreachable_TickFailsSoft_ReturnsZero()
    {
        _engine.OnGet = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(0, "a dead engine skips the tick; the timeout SLA is the backstop");
    }

    [Test]
    public async Task OneBadWait_DoesNotStopTheSweep()
    {
        _engine.OnGet = _ => Json(new
        {
            waits = new object[]
            {
                new { bookmarkId = "bm-bad", workflowInstanceId = "wf-b", sessionId = Session, runId = "13", repository = "acme/widgets", tenantId = Tenant.ToString() },
                new { bookmarkId = "bm-good", workflowInstanceId = "wf-g", sessionId = Session, runId = "42", repository = "acme/widgets", tenantId = Tenant.ToString() },
            },
        });
        DriverResolves(_actions.Object);
        _actions
            .Setup(a => a.GetRunStatusAsync("acme", "widgets", "13", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        RunStatus("42", "completed", "success");

        var resumed = await BuildSut().PollOnceAsync(CancellationToken.None);

        resumed.Should().Be(1, "a throwing wait must not stop the sweep for the others");
        JsonDocument.Parse(_engine.Posts.Single().Body).RootElement
            .GetProperty("bookmarkId").GetString().Should().Be("bm-good");
    }

    // ================================================================
    // Plumbing fakes
    // ================================================================

    private sealed class FakeDriver : IGitPlatformDriver
    {
        public FakeDriver(IGitPlatformActionsClient? actions) => Actions = actions;
        public PlatformKind Kind => PlatformKind.GitHub;
        public IGitPlatformClient Client { get; } = Mock.Of<IGitPlatformClient>();
        public IGitPlatformActionsClient? Actions { get; }
        public IReadOnlySet<PlatformCapability> Capabilities { get; } =
            new HashSet<PlatformCapability> { PlatformCapability.Actions };
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
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
                Content = new StringContent("{\"resumed\":true}", Encoding.UTF8, "application/json"),
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
