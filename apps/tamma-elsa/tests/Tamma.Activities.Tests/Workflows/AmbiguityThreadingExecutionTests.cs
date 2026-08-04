using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.Documents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.ElsaServer.Endpoints;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-25 (AC1 / AC2 behavioral / AC3 / AC5) — full-runtime pins that leg 1 of
/// <c>IsAmbiguityAboveThreshold</c> is LIVE: <see cref="IssueDecompositionWorkflow"/> (the
/// representative threading binding) fetches the latest ACCEPTED <c>ambiguity-assessment</c>
/// for its issue and threads its <c>score</c> as the <c>ambiguityScore</c> lifecycle input,
/// escalating the produce BEFORE review at dial 100. Clone of the
/// <see cref="IssueDecompositionLifecycleExecutionTests"/> harness (real
/// <see cref="DocumentLifecycleWorkflow"/>, real <see cref="LifecycleReEntryService"/> over
/// SEEDABLE fakes, scripted llm/review stubs) with the fakes made ISSUE-AWARE so two
/// interleaved runs read disjoint slices (AC3).
///
/// <para><b>Store seeding (inherited harness seam).</b> The persist hop and the read fake are
/// intentionally decoupled in-process, so the accepted assessment is seeded DIRECTLY into the
/// read fake — the row plus its <c>DOCUMENT.ACCEPTED</c> event, because
/// <c>LifecycleResumeCalculator</c> fail-louds on a store/stream disagreement.</para>
///
/// <para><b>CI-only</b>: class name contains <c>Execution</c> (skipped by the fast local filter
/// <c>FullyQualifiedName!~Execution</c>) and <c>[Explicit]</c> keeps it out of the default gate.
/// Known limitation (2026-08-03, family-wide and pre-existing): under the bare in-memory test
/// host, Task-kind activities suspend without draining (no bookmarks, no incidents), so this
/// suite — like <see cref="IssueDecompositionLifecycleExecutionTests"/> and
/// <c>ProseLifecycleExecutionTests</c> — stalls on a local runner; the bounded waits surface
/// the stalled state instead of hanging. The structural suites
/// (<see cref="AmbiguitySignalCoverageMapTests"/>) carry the drift burden the harness cannot.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class AmbiguityThreadingExecutionTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000025");
    private const string IssueA = "issue-39-25-a";
    private const string IssueB = "issue-39-25-b";
    private const string AssessmentType = "ambiguity-assessment";
    private static readonly Guid AssessmentDocA = Guid.Parse("0192a8b0-2525-7abc-8def-00000000000a");

    private static readonly ConcurrentQueue<string> Llm = new();
    private static readonly ConcurrentQueue<string> Reviews = new();

    [SetUp]
    public void Reset() { Llm.Clear(); Reviews.Clear(); }

    // ── AC1 — leg 1 live: high score escalates the NEXT dispatch, before review ──

    [Test]
    public async Task AcceptedHighScore_EscalatesNextDispatch_BeforeReview_AtDial100()
    {
        // RED before 39-25's wiring: nothing threads, so this run used to sail through
        // review + accept and complete — the escalated/ambiguity assertions failed.
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, StoreWithAssessment(IssueA, 0.95), EventsWithAssessment(IssueA));
        await StartRuntimeAsync(provider);

        var result = await RunParentToCompletionAsync(provider, IssueA);
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated,
            "an accepted assessment at 0.95 >= threshold 0.7 escalates the downstream decomposition " +
            "produce on the THREADED leg — even at dial 100 (the hatch is level-independent)");
        Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire());

        // BEFORE review: the review stub was never consumed and no acceptance request published.
        Reviews.Count.Should().Be(1, "the escalation fires post-VALIDATE, before the review ring");
        provider.GetRequiredService<CapturingPublisher>().Last.Should().BeNull(
            "an ambiguity escalation never reaches the accept gate");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Escalated);
        stream.Should().NotContain(DocumentEvents.ReviewRequested,
            "escalates BEFORE REVIEW — the review stage is never entered");
    }

    [Test]
    public async Task AcceptedLowScore_DoesNotEscalateOnThreadedLeg()
    {
        // Green trivially before the wiring; its permanent value is against a WRONG
        // implementation that escalates on assessment PRESENCE rather than score >= threshold.
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, StoreWithAssessment(IssueA, 0.2), EventsWithAssessment(IssueA));
        await StartRuntimeAsync(provider);

        var (instanceId, request) = await RunToSuspendAsync(provider, IssueA);
        request.Should().NotBeNull("a below-threshold score does not escalate — the run reaches the accept gate");

        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept());
        Status(result).Should().Be("completed",
            "a measured 0.2 threads but stays below the 0.7 threshold — no escalation on the threaded leg");
    }

    // ── AC3 — the score follows the run's issueId, not the process ──

    [Test]
    public async Task ScoreFollowsIssueId_TwoInterleavedRuns()
    {
        // Issue A carries an accepted 0.95 assessment; issue B carries none. Run B first,
        // then A, against the SAME provider — a fetch ignoring issueId would either escalate
        // B on A's score or dilute A's escalation.
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, StoreWithAssessment(IssueA, 0.95), EventsWithAssessment(IssueA));
        await StartRuntimeAsync(provider);

        // B (no upstream assessment): the key is omitted, the run completes normally (AC2 behavioral).
        var (instanceId, request) = await RunToSuspendAsync(provider, IssueB);
        request.Should().NotBeNull("issue B has no assessment — nothing escalates, the run reaches accept");
        var resultB = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept());
        Status(resultB).Should().Be("completed",
            "no upstream assessment ⇒ no ambiguityScore input ⇒ no threaded escalation (null stays null — " +
            "a stale score from issue A must never be picked up)");

        // A (accepted 0.95): escalates on ITS OWN score. RED before the wiring.
        Llm.Enqueue(ValidDecomposition());
        var resultA = await RunParentToCompletionAsync(provider, IssueA);
        Status(resultA).Should().Be(DocumentLifecycleResult.StatusEscalated,
            "issue A's accepted assessment follows issue A's run");
        Output(resultA, "outcome").Should().Be(DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire());
    }

    // ── AC5 (D7 matrix, scoped) — at dial 100 only two outcomes pull anyone in ──

    [Test]
    public async Task AtDial100_OnlyTwoOutcomesPullAHuman()
    {
        // Leg 1 — ambiguity-above-threshold: the ONLY content-side level-independent pull.
        {
            Llm.Enqueue(ValidDecomposition());
            var capture = new CapturingHandler();
            await using var provider = BuildProvider(capture, StoreWithAssessment(IssueA, 0.95), EventsWithAssessment(IssueA));
            var result = await RunParentToCompletionAsync(provider, IssueA);
        await StartRuntimeAsync(provider);
            Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
            Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire());
        }

        // No score + clean approval: accepted with NO human step (the orchestrator channel
        // answers the acceptance step — the dial picks the approver, not whether the step exists).
        {
            Llm.Enqueue(ValidDecomposition());
            Reviews.Enqueue(ApproveReview());
            var capture = new CapturingHandler();
            await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());
            var (instanceId, request) = await RunToSuspendAsync(provider, IssueB);
        await StartRuntimeAsync(provider);
            request.Should().NotBeNull();
            var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept());
            Status(result).Should().Be("completed",
                "at dial 100 a clean, unambiguous run is accepted on the orchestrator channel — no human pull");
        }

        // No score + undecidable review: review-undecidable, the OTHER level-independent pull.
        {
            Llm.Enqueue(ValidDecomposition());
            // Review queue left EMPTY — the stub returns "{}", an unusable review ⇒ undecidable.
            var capture = new CapturingHandler();
            await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());
            var result = await RunParentToCompletionAsync(provider, IssueB);
        await StartRuntimeAsync(provider);
            Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
            Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.ReviewUndecidable.ToWire());
            provider.GetRequiredService<CapturingPublisher>().Last.Should().BeNull(
                "an undecidable review escalates without ever reaching the accept gate");
        }
        // No other outcome in the matrix pulled anyone in: the three legs above exit exactly
        // { escalated:ambiguity-above-threshold, completed, escalated:review-undecidable }.
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness (clone of IssueDecompositionLifecycleExecutionTests; issue-aware fakes)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Populate the Elsa registries (so ByDefinitionId resolves the coded workflows)
    /// and start the hosted services (so the background dispatcher drains the mediated
    /// sub-workflow dispatches) — the ProseLifecycleExecutionTests startup idiom.</summary>
    private static async Task StartRuntimeAsync(ServiceProvider provider)
    {
        await provider.GetRequiredService<Elsa.Workflows.Runtime.IRegistriesPopulator>()
            .PopulateAsync(CancellationToken.None);
        foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
    }

    private static async Task<(string InstanceId, AcceptanceRequest? Request)> RunToSuspendAsync(
        IServiceProvider provider, string issueId)
    {
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        var before = publisher.Count;
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("issue-decomposition"),
            Input = DecompositionInput(issueId),
        });
        // The in-memory runtime drains mediated dispatches asynchronously — wait (bounded)
        // for the accept-gate publish to land.
        for (var i = 0; i < 100 && publisher.Count == before; i++)
            await Task.Delay(100);
        return (response.WorkflowInstanceId, publisher.Last);
    }

    private static async Task<IDictionary<string, object>> RunParentToCompletionAsync(
        IServiceProvider provider, string issueId)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("issue-decomposition"),
            Input = DecompositionInput(issueId),
        });
        return await AwaitParentOutputAsync(provider, response.WorkflowInstanceId);
    }

    private static async Task<IDictionary<string, object>> ResumeAndReadParentAsync(
        IServiceProvider provider, string parentInstanceId, Guid session, string decisionJson)
    {
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session, TenantId: Tenant.ToString(), DecisionJson: decisionJson,
                Feedback: null, DeciderId: "orchestrator", DeciderDisplay: null,
                Channel: "orchestrator", RulesReference: "system-default@1"),
            bookmarkStore, runtime, loggerFactory, CancellationToken.None);

        return await AwaitParentOutputAsync(provider, parentInstanceId);
    }

    /// <summary>Bounded wait for the parent binding's terminal ExposeOutput region — the
    /// background dispatcher may still be draining the child lifecycle when the create/resume
    /// call returns.</summary>
    private static async Task<IDictionary<string, object>> AwaitParentOutputAsync(
        IServiceProvider provider, string parentInstanceId)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        IDictionary<string, object>? output = null;
        for (var i = 0; i < 150; i++)
        {
            var client = await runtime.CreateClientAsync(parentInstanceId);
            var state = await client.ExportStateAsync();
            output = state.Output;
            if (output is { Count: > 0 }) return output;
            await Task.Delay(100);
        }

        // Bounded wait exhausted — surface the run's state so a hang/fault names itself.
        var final = await (await runtime.CreateClientAsync(parentInstanceId)).ExportStateAsync();
        TestContext.Out.WriteLine(
            $"AwaitParentOutput timed out: status={final.Status}/{final.SubStatus} " +
            $"incidents=[{string.Join("; ", final.Incidents.Select(x => $"{x.ActivityId}:{x.Message}"))}]");
        var bookmarks = await provider.GetRequiredService<IBookmarkStore>()
            .FindManyAsync(new Elsa.Workflows.Runtime.Filters.BookmarkFilter(), CancellationToken.None);
        TestContext.Out.WriteLine("bookmarks: " + string.Join(" | ",
            bookmarks.Select(b => $"{b.ActivityTypeName}/{b.Name}@{b.WorkflowInstanceId}")));
        return output ?? new Dictionary<string, object>();
    }

    /// <summary>Dial 100, threshold 0.7 (the AC1 posture) — explicit rules JSON so this suite is
    /// insulated from any 43-batch defaults change; validated by AcceptanceRules.Validate.</summary>
    private static string Dial100RulesJson() =>
        AcceptanceRulesJson.Serialize(AcceptanceDefaults.For(DocumentTypeKey.Decomposition) with
        {
            AutonomyLevel = 100,
            AmbiguityEscalationThreshold = 0.7,
        });

    private static Dictionary<string, object> DecompositionInput(string issueId) => new()
    {
        ["issueId"] = issueId,
        ["issueTitle"] = "Add per-tenant rate limiting",
        ["repository"] = "meywd/tamma",
        ["issueNumber"] = 42,
        ["workItemJson"] = "{\"type\":\"issue\",\"title\":\"rate limiting\"}",
        ["tenantId"] = Tenant.ToString(),
        ["acceptanceRulesJson"] = Dial100RulesJson(),
    };

    private static string? Status(IDictionary<string, object> output) => Output(output, "status");
    private static string? Output(IDictionary<string, object> output, string key)
        => output.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.TryGetProperty("events", out var evs)
                ? evs.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList()
                : new List<string?>())
            .ToList();

    private static ServiceProvider BuildProvider(
        CapturingHandler capture, IDocumentInstanceRepository docs, IEventRepository events)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var publisher = new CapturingPublisher();
        services.AddSingleton(publisher);
        services.AddSingleton<IAcceptanceRequestPublisher>(publisher);

        services.AddSingleton(docs);
        services.AddSingleton(events);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Tenant));
        services.AddSingleton<ILifecycleReEntryService, LifecycleReEntryService>();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<IssueDecompositionWorkflow>();
            elsa.AddWorkflow<DocumentLifecycleWorkflow>();
            elsa.AddWorkflow<StubLlmCallWorkflow>();
            elsa.AddWorkflow<StubContextGatheringWorkflow>();
            elsa.AddWorkflow<StubDocumentReviewWorkflow>();
            elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
                ["Engine:CallbackUrl"] = "http://engine.test",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        return services.BuildServiceProvider();
    }

    // ── seed fixtures — issue-aware ────────────────────────────────────

    private static FakeDocuments FreshStore() => new(Array.Empty<DocumentInstance>());
    private static FakeEvents FreshEvents() => new(Array.Empty<DomainEvent>());

    /// <summary>An accepted ambiguity-assessment row for <paramref name="issueId"/> carrying
    /// <paramref name="score"/> in its payload (the leg-1 source of truth).</summary>
    private static FakeDocuments StoreWithAssessment(string issueId, double score) => new(new[]
    {
        new DocumentInstance
        {
            Id = AssessmentDocA, DocumentType = AssessmentType, IssueId = issueId, Revision = 1,
            Status = "accepted",
            ProducedByRole = "product_owner", ProducedByAction = "assess-ambiguity",
            ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = issueId,
            BodyJson = AssessmentBody(score), TenantId = Tenant,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        },
    });

    /// <summary>The assessment's DOCUMENT.ACCEPTED event — the read model fail-louds on a
    /// store/stream disagreement, so the seeded row needs its stream half.</summary>
    private static FakeEvents EventsWithAssessment(string issueId) => new(new[]
    {
        new DomainEvent
        {
            Id = Guid.NewGuid(), Type = "DOCUMENT.ACCEPTED", TenantId = Tenant,
            Tags = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["issueId"] = issueId, ["documentType"] = AssessmentType,
                ["documentId"] = AssessmentDocA.ToString(), ["round"] = 1,
            }),
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 1, DateTimeKind.Utc),
            SequenceNumber = 1,
        },
    });

    private static string AssessmentBody(double score) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["score"] = score,
        ["rationale"] = "seeded assessment for 39-25 threading pins",
        ["ambiguities"] = Array.Empty<object>(),
        ["confidence"] = 0.9,
    });

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidDecomposition() =>
        "{\"summary\":\"Split rate limiting into middleware then config.\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"Middleware\",\"description\":\"limiter\",\"estimateHours\":6,\"complexity\":\"medium\",\"dependsOn\":[]}," +
        "{\"id\":\"ST-2\",\"title\":\"Config\",\"description\":\"per-tenant\",\"estimateHours\":4,\"complexity\":\"low\",\"dependsOn\":[\"ST-1\"]}]}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    // ── stub sub-workflows ─────────────────────────────────────────────

    public class StubLlmCallWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.DefinitionId = "llm-call";
            builder.Root = new Sequence
            {
                Activities =
                {
                    new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) },
                    new SetOutput { Id = "OutResponse", OutputName = new("llmResponse"),
                        OutputValue = new(_ => (object)(Llm.TryDequeue(out var v) ? v : "{}")) },
                },
            };
        }
    }

    public class StubContextGatheringWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.DefinitionId = "context-gathering";
            builder.Root = new Sequence
            {
                Activities =
                {
                    new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) },
                    new SetOutput { Id = "OutSummary", OutputName = new("summary"), OutputValue = new(_ => (object)"prior-art scan summary") },
                    new SetOutput { Id = "OutContextIds", OutputName = new("contextIds"), OutputValue = new(_ => (object)"[\"ctx-1\"]") },
                },
            };
        }
    }

    public class StubDocumentReviewWorkflow : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            builder.DefinitionId = "document-review";
            builder.Root = new Sequence
            {
                Activities =
                {
                    new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) },
                    new SetOutput { Id = "OutReview", OutputName = new("reviewJson"),
                        OutputValue = new(_ => (object)(Reviews.TryDequeue(out var v) ? v : "{}")) },
                },
            };
        }
    }

    // ── fakes (issue-aware — the AC3 requirement) ──────────────────────

    private sealed class CapturingPublisher : IAcceptanceRequestPublisher
    {
        private readonly ConcurrentQueue<AcceptanceRequest> _requests = new();
        public AcceptanceRequest? Last => _requests.LastOrDefault();
        public int Count => _requests.Count;
        public Task PublishAsync(AcceptanceRequest request, CancellationToken ct)
        {
            _requests.Enqueue(request);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true,\"persisted\":1}"),
            };
        }
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        private Guid? _tenant;
        public FixedTenantContext(Guid tenant) => _tenant = tenant;
        public Guid? TenantId => _tenant;
        public void SetTenantId(Guid tenantId) => _tenant = tenantId;
        public void ClearTenantId() => _tenant = null;
    }

    /// <summary>Unlike the 39-12 harness fake (which returns one fixed list), this fake FILTERS
    /// by issueId — the very dimension AC3 pins ("a stale score from a different issue is never
    /// picked up"), so the fake must not blur it.</summary>
    private sealed class FakeDocuments : IDocumentInstanceRepository
    {
        private readonly IReadOnlyList<DocumentInstance> _rows;
        public FakeDocuments(IReadOnlyList<DocumentInstance> rows) => _rows = rows;

        public Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocumentInstance>>(
                _rows.Where(r => r.IssueId == issueId && r.Status == "accepted" && r.TenantId == tenantId).ToList());
        public Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
            => Task.FromResult(_rows.FirstOrDefault(r => r.Id == documentId && r.TenantId == tenantId));
        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, string? audience, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocumentInstance>>(_rows.Where(r => r.IssueId == issueId).ToList());
        public Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId, DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeEvents : IEventRepository
    {
        private readonly IReadOnlyList<DomainEvent> _document;
        public FakeEvents(IReadOnlyList<DomainEvent> document) => _document = document;

        public Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryEventsAsync(
            Guid tenantId, string? type, bool typeIsPrefix, string? correlationId, string? actor,
            DateTimeOffset? from, DateTimeOffset? to, long? cursor, int limit, bool includeTotal = false)
        {
            var events = type == "APPROVAL." ? Array.Empty<DomainEvent>() : _document;
            return Task.FromResult<(IReadOnlyList<DomainEvent>, int?)>((events.ToList(), events.Count()));
        }
        public Task<DomainEvent> AppendAsync(DomainEvent evt) => throw new NotSupportedException();
        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }
}
