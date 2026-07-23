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
/// Story 39-10 (AC6/AC7/AC8, Design Decision D8) — crash re-entry + bookmark-resume
/// through the REAL Elsa runtime, extending the 39-6 execution-harness intent with the
/// REAL <see cref="LifecycleReEntryService"/> over faked 39-11 repos that stand in for
/// the durable store + stream a prior (crashed) run left behind.
///
/// <para>Scenarios: (i) AC7 — a fresh dispatch for an issue whose prior work reached
/// produced+validated (review pending) RE-ENTERS at Review, does NOT re-produce, runs
/// to Accepted, and the run's stream carries exactly ONE <c>DOCUMENT.ACCEPTED</c> plus a
/// <c>DOCUMENT.REENTERED</c>; variant — a prior ACCEPTED doc short-circuits to Complete
/// with <c>DOCUMENT.REENTERED</c> and NO duplicate acceptance. (ii) AC8 — a fresh run
/// suspends on the accept gate and resumes via the
/// <see cref="DocumentDecisionResumeEndpoint"/> resume seam with the re-entry service
/// wired (the canonical bookmark path).</para>
///
/// <para><b>CI-only</b>: the class name contains <c>Integration</c> (skipped by the fast
/// local filter) and <c>[Explicit]</c> keeps it out of the default gate, matching how
/// 39-6's execution suite is gated. The full-runtime + faked-store wiring proves the
/// WIRING; the property-level correctness lives in the unit suites.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime re-entry integration — runs in the CI jobs, skipped in the fast local gate")]
public class LifecycleReEntryIntegrationTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000009");
    private const string Issue = "issue-42";
    private const string Type = "decomposition";
    private static readonly Guid ExistingDoc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");

    // ── (i) AC7 — crash → fresh dispatch re-enters at Review, exactly one ACCEPTED ──

    [Test]
    public async Task Crash_FreshDispatch_ReEntersAtReview_NoReproduce_ExactlyOneAccepted()
    {
        var docs = new FakeDocuments(latestAccepted: Array.Empty<DocumentInstance>(),
            byId: new() { [ExistingDoc] = ValidatedRow() });
        var events = new FakeEvents(
            document: new[]
            {
                Ev("DOCUMENT.PRODUCED.SUCCESS", 1, ExistingDoc, 0),
                Ev("DOCUMENT.VALIDATED.SUCCESS", 2, ExistingDoc, 0),
            },
            approval: Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, docs, events);

        // Review stub approves; the fresh instance re-enters at Review and suspends on accept.
        Reviews.Enqueue(ApproveReview());

        var request = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("re-entry at Review must review the existing revision then suspend on the accept gate");

        var result = await ResumeAsync(provider, request!.DecisionSessionId, Accept());
        Status(result).Should().Be(DocumentLifecycleResult.StatusAccepted);

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Reentered, "the re-entry must record DOCUMENT.REENTERED");
        stream.Should().NotContain(DocumentEvents.ProducedSuccess, "produce is skipped on Review re-entry");
        stream.Count(t => t == DocumentEvents.Accepted).Should().Be(1, "exactly one acceptance per document");
    }

    [Test]
    public async Task Crash_AfterAccept_FreshDispatch_ShortCircuitsToComplete_NoDuplicateAcceptance()
    {
        var docs = new FakeDocuments(
            latestAccepted: new[] { AcceptedRow() },
            byId: new() { [ExistingDoc] = AcceptedRow() });
        var events = new FakeEvents(
            document: new[]
            {
                Ev("DOCUMENT.PRODUCED.SUCCESS", 1, ExistingDoc, 0),
                Ev("DOCUMENT.VALIDATED.SUCCESS", 2, ExistingDoc, 0),
                Ev("DOCUMENT.REVIEWED", 3, ExistingDoc, 0),
                Ev("DOCUMENT.ACCEPTED", 4, ExistingDoc, 0),
            },
            approval: Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, docs, events);

        var result = await RunToCompletionAsync(provider);

        Status(result).Should().Be(DocumentLifecycleResult.StatusAccepted, "an accepted doc short-circuits to complete");
        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Reentered);
        stream.Should().NotContain(DocumentEvents.Accepted, "the short-circuit must NOT re-emit DOCUMENT.ACCEPTED");
        stream.Should().NotContain(DocumentEvents.ProducedSuccess);
    }

    // ── (ii) AC8 — fresh run suspends on the accept gate, resumes via the seam ──

    [Test]
    public async Task FreshRun_SuspendsOnAcceptGate_ResumesViaEndpoint_ToAccepted()
    {
        var docs = new FakeDocuments(Array.Empty<DocumentInstance>(), new());
        var events = new FakeEvents(Array.Empty<DomainEvent>(), Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, docs, events);

        // Fresh issue → Produce path; the llm-call + review stubs drive it to the accept gate.
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var request = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("a fresh run publishes an AcceptanceRequest and suspends on the canonical bookmark");

        var result = await ResumeAsync(provider, request!.DecisionSessionId, Accept());
        Status(result).Should().Be(DocumentLifecycleResult.StatusAccepted);
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness (mirrors DocumentLifecycleExecutionTests; self-contained)
    // ════════════════════════════════════════════════════════════════════

    private static readonly ConcurrentQueue<string> Llm = new();
    private static readonly ConcurrentQueue<string> Reviews = new();

    [SetUp]
    public void Reset() { Llm.Clear(); Reviews.Clear(); }

    private static async Task<AcceptanceRequest?> RunToSuspendAsync(IServiceProvider provider)
    {
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("document-lifecycle"),
            Input = LifecycleInput(),
        });
        return publisher.Last;
    }

    private static async Task<IDictionary<string, object>> RunToCompletionAsync(IServiceProvider provider)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("document-lifecycle"),
            Input = LifecycleInput(),
        });
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IDictionary<string, object>> ResumeAsync(
        IServiceProvider provider, Guid session, string decisionJson)
    {
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        var name = WaitForDocumentDecisionActivity.DecisionBookmarkName(Tenant.ToString(), session);
        var bookmarks = (await bookmarkStore.FindManyAsync(
            new Elsa.Workflows.Runtime.Filters.BookmarkFilter { Name = name }, CancellationToken.None)).ToList();
        var instanceId = bookmarks.Count == 1 ? bookmarks[0].WorkflowInstanceId : string.Empty;

        await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session, TenantId: Tenant.ToString(), DecisionJson: decisionJson,
                Feedback: null, DeciderId: "orchestrator", DeciderDisplay: null,
                Channel: "orchestrator", RulesReference: "system-default@1"),
            bookmarkStore, runtime, loggerFactory, CancellationToken.None);

        if (string.IsNullOrEmpty(instanceId)) return new Dictionary<string, object>();
        var client = await runtime.CreateClientAsync(instanceId);
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static Dictionary<string, object> LifecycleInput() => new()
    {
        ["producerRole"] = "senior_developer",
        ["producerAction"] = "decompose-issue",
        ["producerVariablesJson"] = "{\"workItemJson\":\"x\"}",
        ["documentType"] = Type,
        ["issueId"] = Issue,
        ["correlationId"] = "corr-42",
        ["acceptanceRulesJson"] = "",
        ["tenantId"] = Tenant.ToString(),
    };

    private static string? Status(IDictionary<string, object> output)
        => output.TryGetValue("status", out var s) ? s?.ToString() : null;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    private static ServiceProvider BuildProvider(
        CapturingHandler capture, IDocumentInstanceRepository docs, IEventRepository events)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var publisher = new CapturingPublisher();
        services.AddSingleton(publisher);
        services.AddSingleton<IAcceptanceRequestPublisher>(publisher);

        // The REAL re-entry service over faked 39-11 repos + a fixed ambient tenant.
        services.AddSingleton(docs);
        services.AddSingleton(events);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Tenant));
        services.AddSingleton<ILifecycleReEntryService, LifecycleReEntryService>();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<DocumentLifecycleWorkflow>();
            elsa.AddWorkflow<StubLlmCallWorkflow>();
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

    // ── seed fixtures ──────────────────────────────────────────────────

    private static DomainEvent Ev(string type, long seq, Guid documentId, int round)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = Issue,
            ["documentType"] = Type,
            ["documentId"] = documentId.ToString(),
            ["round"] = round,
        };
        return new DomainEvent
        {
            Id = Guid.NewGuid(), Type = type, TenantId = Tenant,
            Tags = JsonSerializer.Serialize(tags),
            CreatedAt = new DateTime(2026, 7, 23, 0, 0, (int)seq, DateTimeKind.Utc),
            SequenceNumber = seq,
        };
    }

    private static DocumentInstance ValidatedRow() => Row("validated");
    private static DocumentInstance AcceptedRow() => Row("accepted");

    private static DocumentInstance Row(string status) => new()
    {
        Id = ExistingDoc, DocumentType = Type, IssueId = Issue, Revision = 1, Status = status,
        ProducedByRole = "senior_developer", ProducedByAction = "decompose-issue",
        ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = "corr-42",
        BodyJson = ValidDecomposition(), TenantId = Tenant,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidDecomposition() =>
        "{\"summary\":\"Split rate limiting into middleware then config.\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"Middleware\",\"description\":\"limiter\",\"estimateHours\":6,\"complexity\":\"medium\",\"dependsOn\":[]}]}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    // ── stubs + fakes ──────────────────────────────────────────────────

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

    private sealed class CapturingPublisher : IAcceptanceRequestPublisher
    {
        private readonly ConcurrentQueue<AcceptanceRequest> _requests = new();
        public AcceptanceRequest? Last => _requests.LastOrDefault();
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

    private sealed class FakeDocuments : IDocumentInstanceRepository
    {
        private readonly IReadOnlyList<DocumentInstance> _latestAccepted;
        private readonly Dictionary<Guid, DocumentInstance> _byId;
        public FakeDocuments(IReadOnlyList<DocumentInstance> latestAccepted, Dictionary<Guid, DocumentInstance> byId)
        {
            _latestAccepted = latestAccepted;
            _byId = byId;
        }
        public Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct)
            => Task.FromResult(_latestAccepted);
        public Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
            => Task.FromResult(_byId.TryGetValue(documentId, out var d) ? d : null);
        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocumentInstance>>(_byId.Values.ToList());
        public Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId, DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeEvents : IEventRepository
    {
        private readonly IReadOnlyList<DomainEvent> _document;
        private readonly IReadOnlyList<DomainEvent> _approval;
        public FakeEvents(IReadOnlyList<DomainEvent> document, IReadOnlyList<DomainEvent> approval)
        {
            _document = document;
            _approval = approval;
        }
        public Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryEventsAsync(
            Guid tenantId, string? type, bool typeIsPrefix, string? correlationId, string? actor,
            DateTimeOffset? from, DateTimeOffset? to, long? cursor, int limit, bool includeTotal = false)
        {
            var events = type == "APPROVAL." ? _approval : _document;
            return Task.FromResult<(IReadOnlyList<DomainEvent>, int?)>((events, events.Count));
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
