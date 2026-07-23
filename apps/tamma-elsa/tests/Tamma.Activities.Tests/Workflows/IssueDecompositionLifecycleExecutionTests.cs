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
/// Story 39-12 (Test Plan step 10) — full-runtime execution coverage for the rebuilt
/// <see cref="IssueDecompositionWorkflow"/> binding driving the REAL
/// <see cref="DocumentLifecycleWorkflow"/> through the Elsa runtime, with the mediated
/// <c>llm-call</c> / <c>context-gathering</c> / <c>document-review</c> dispatches served by
/// SCRIPTED stub sub-workflows, a capturing acceptance publisher + durable event drain, the
/// REAL <see cref="LifecycleReEntryService"/> over faked 39-11 repos, and decisions injected
/// through 39-8's <see cref="DocumentDecisionResumeEndpoint"/> resume seam.
///
/// <para>Scenarios: (a) happy path → Accept → <c>status=completed</c>, both event families,
/// <c>DECOMPOSITION.COMPLETED</c> carrying the sub-task count; (b) full ring (invalid →
/// repair → concerns → revise → approve) with CONTEXT_GATHERED once + REVISION_STARTED;
/// (c) validation exhaustion → typed ValidationExhausted escalation + DECOMPOSITION.FAILED,
/// no dead terminal; (d) supervised accept suspend/resume, wrong-tenant resume 404s; (e)
/// crash after acceptance → short-circuit to complete with the SAME documentId, no re-emit;
/// (f) crash mid-review → re-enter at review, no second produce / no second STARTED. All run
/// with the 39-9 managed repair ring OFF (default).</para>
///
/// <para><b>CI-only</b>: the class name contains <c>Execution</c> (skipped by the fast local
/// filter <c>FullyQualifiedName!~Execution</c>) and <c>[Explicit]</c> keeps it out of the
/// default gate; the Postgres CI jobs run it. The property-level correctness burden lives in
/// <see cref="DecompositionBindingHelperTests"/> + <see cref="DocumentLifecycleHelperTests"/>
/// — this proves the WIRING.</para>
///
/// <para><b>Store seeding (why it stays).</b> The lifecycle now WIRES
/// <see cref="PersistDocumentInstanceActivity"/> at supersede + every terminal (39-11 D6), so
/// a live run DOES project <c>document_instances</c> rows. But in THIS harness the persist
/// hop goes through <c>TammaApiClient</c> → the <see cref="CapturingHandler"/> stub (which just
/// records the POST body and 201s); it does NOT flow into the <see cref="IDocumentInstanceRepository"/>
/// fake that the read path (<see cref="LifecycleReEntryService"/>) queries. Those two seams are
/// intentionally decoupled in-process, so the crash re-entry scenarios (e)/(f) still SEED the
/// read fake with the state a crashed run left behind (the 39-10 harness pattern) to exercise
/// the store-read path AC5 depends on. Wiring the stub handler to feed the read fake is a
/// harness-only follow-up; the persist WIRING itself is proved structurally by
/// <c>DocumentLifecycleWorkflowStructureTests</c>.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class IssueDecompositionLifecycleExecutionTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000012");
    private const string Issue = "issue-39-12";
    private const string Type = "decomposition";
    private static readonly Guid ExistingDoc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");

    private static readonly ConcurrentQueue<string> Llm = new();
    private static readonly ConcurrentQueue<string> Reviews = new();

    [SetUp]
    public void Reset() { Llm.Clear(); Reviews.Clear(); }

    // ── (a) happy path ──────────────────────────────────────────────────

    [Test]
    public async Task HappyPath_ValidDraft_Approve_Accept_CompletesWithSubtaskCount()
    {
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());

        var (instanceId, request) = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("the binding dispatches document-lifecycle, which publishes an AcceptanceRequest and suspends");

        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed", "acceptance surfaces the compat status 'completed' (D1)");
        SubtaskCount(result).Should().Be(2, "the sub-task count is sourced from the accepted Decomposition payload");
        (Output(result, "decomposition") ?? "").Should().Contain("subtasks");

        var stream = CapturedTypes(capture);
        // Both event families present (AC4).
        stream.Should().Contain("DECOMPOSITION.STARTED");
        stream.Should().Contain("DECOMPOSITION.CONTEXT_GATHERED");
        stream.Should().Contain(DecompositionEventNames.Completed);
        stream.Should().Contain(DocumentEvents.ProducedSuccess);
        stream.Should().Contain(DocumentEvents.Accepted);

        // DECOMPOSITION.COMPLETED carries the sub-task count (AC4).
        SubtaskCountOnCompleted(capture).Should().Be(2);
        // Every DECOMPOSITION.* event is tagged with the issue id (AC4 matching tags).
        DecompositionEventIssueIds(capture).Should().OnlyContain(id => id == Issue);
    }

    // ── (b) full ring ───────────────────────────────────────────────────

    [Test]
    public async Task FullRing_InvalidThenRepairThenReviseThenApprove_ContextGatheredOnce_RevisionStarted()
    {
        // invalid (dangling dependsOn) → repair → valid → concerns → revise → valid → approve.
        Llm.Enqueue(InvalidDanglingDecomposition());
        Llm.Enqueue(ValidDecomposition());
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ConcernsReview());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());

        var (instanceId, request) = await RunToSuspendAsync(provider);
        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed");

        var stream = CapturedTypes(capture);
        stream.Count(t => t == "DECOMPOSITION.CONTEXT_GATHERED").Should().Be(1,
            "context is gathered once per decomposition, not once per lifecycle round");
        stream.Should().Contain(DocumentEvents.RevisionStarted, "reviewer concerns drive a bounded revise round");
    }

    // ── (c) validation exhaustion ───────────────────────────────────────

    [Test]
    public async Task AlwaysInvalid_EscalatesValidationExhausted_WithFailedEvent_NoDeadTerminal()
    {
        for (var i = 0; i < 8; i++) Llm.Enqueue(InvalidEmptyDecomposition());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());

        var result = await RunParentToCompletionAsync(provider);
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated,
            "an always-invalid producer exits as a typed escalation, never a dead terminal (AC3)");
        Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Escalated);
        stream.Should().Contain(DecompositionEventNames.Failed,
            "DECOMPOSITION.FAILED is mirrored on the escalation (AC4)");
    }

    // ── (d) supervised accept suspend/resume, wrong-tenant 404 ──────────

    [Test]
    public async Task Supervised_SuspendsOnCanonicalBookmark_WrongTenant404_CorrectTenantResumes()
    {
        Llm.Enqueue(ValidDecomposition());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());

        var (instanceId, request) = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("the run genuinely suspends on the canonical tenant-folded accept bookmark");

        // A resume keyed by the WRONG tenant cannot resolve this tenant's gate → 404.
        var wrong = await ResumeRawAsync(provider, request!.DecisionSessionId, Accept(), tenantId: Guid.NewGuid().ToString());
        StatusCode(wrong).Should().Be(404, "a wrong-tenant resume cannot resolve the tenant-folded bookmark");

        // The correct tenant resumes to completion.
        var result = await ResumeAndReadParentAsync(provider, instanceId, request.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed");
    }

    // ── (e) crash after acceptance → short-circuit to complete ──────────

    [Test]
    public async Task CrashAfterAccept_FreshDispatch_ShortCircuits_SameDocumentId_NoReEmit()
    {
        var store = new FakeDocuments(
            latestAccepted: new[] { Row("accepted") },
            byId: new() { [ExistingDoc] = Row("accepted") });
        var events = new FakeEvents(new[]
        {
            Ev("DOCUMENT.PRODUCED.SUCCESS", 1), Ev("DOCUMENT.VALIDATED.SUCCESS", 2),
            Ev("DOCUMENT.REVIEWED", 3), Ev("DOCUMENT.ACCEPTED", 4),
        }, Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, store, events);

        var result = await RunParentToCompletionAsync(provider);
        Status(result).Should().Be("completed", "an already-accepted document short-circuits to completed");
        Output(result, "documentId").Should().Be(ExistingDoc.ToString(), "the re-entry completes with the SAME documentId");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Reentered);
        // Exactly-once across the whole system: the prior run emitted these; THIS re-entry adds none.
        stream.Should().NotContain(DocumentEvents.Accepted, "the short-circuit must NOT re-emit DOCUMENT.ACCEPTED");
        stream.Should().NotContain(DecompositionEventNames.Completed, "no duplicate DECOMPOSITION.COMPLETED on a complete re-entry (D3)");
        stream.Should().NotContain("DECOMPOSITION.STARTED", "a re-entry is not a new decomposition (D7)");
    }

    // ── (f) crash mid-review → re-enter at review ───────────────────────

    [Test]
    public async Task CrashMidReview_FreshDispatch_ReEntersAtReview_NoSecondProduce_NoSecondStarted()
    {
        var store = new FakeDocuments(
            latestAccepted: Array.Empty<DocumentInstance>(),
            byId: new() { [ExistingDoc] = Row("validated") });
        var events = new FakeEvents(new[]
        {
            Ev("DOCUMENT.PRODUCED.SUCCESS", 1), Ev("DOCUMENT.VALIDATED.SUCCESS", 2),
        }, Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, store, events);

        Reviews.Enqueue(ApproveReview());

        var (instanceId, request) = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("re-entry at Review reviews the existing revision then suspends on the accept gate");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Reentered);
        stream.Should().NotContain(DocumentEvents.ProducedSuccess, "produce is skipped on a Review re-entry");
        stream.Should().NotContain("DECOMPOSITION.STARTED", "no second DECOMPOSITION.STARTED on re-entry (D7)");

        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed");
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness (mirrors LifecycleReEntryIntegrationTests; parent = issue-decomposition)
    // ════════════════════════════════════════════════════════════════════

    private static async Task<(string InstanceId, AcceptanceRequest? Request)> RunToSuspendAsync(IServiceProvider provider)
    {
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("issue-decomposition"),
            Input = DecompositionInput(),
        });
        return (response.WorkflowInstanceId, publisher.Last);
    }

    private static async Task<IDictionary<string, object>> RunParentToCompletionAsync(IServiceProvider provider)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("issue-decomposition"),
            Input = DecompositionInput(),
        });
        var client2 = await runtime.CreateClientAsync(response.WorkflowInstanceId);
        var state = await client2.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IDictionary<string, object>> ResumeAndReadParentAsync(
        IServiceProvider provider, string parentInstanceId, Guid session, string decisionJson, string tenantId)
    {
        await ResumeRawAsync(provider, session, decisionJson, tenantId);
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(parentInstanceId);
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IResult> ResumeRawAsync(
        IServiceProvider provider, Guid session, string decisionJson, string tenantId)
    {
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        return await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session, TenantId: tenantId, DecisionJson: decisionJson,
                Feedback: null, DeciderId: "orchestrator", DeciderDisplay: null,
                Channel: "orchestrator", RulesReference: "system-default@1"),
            bookmarkStore, runtime, loggerFactory, CancellationToken.None);
    }

    private static Dictionary<string, object> DecompositionInput() => new()
    {
        ["issueId"] = Issue,
        ["issueTitle"] = "Add per-tenant rate limiting",
        ["repository"] = "meywd/tamma",
        ["issueNumber"] = 42,
        ["workItemJson"] = "{\"type\":\"issue\",\"title\":\"rate limiting\"}",
        ["tenantId"] = Tenant.ToString(),
        ["acceptanceRulesJson"] = "",
    };

    private static string? Status(IDictionary<string, object> output) => Output(output, "status");
    private static string? Output(IDictionary<string, object> output, string key)
        => output.TryGetValue(key, out var v) ? v?.ToString() : null;
    private static int SubtaskCount(IDictionary<string, object> output)
        => output.TryGetValue("subtaskCount", out var v) && int.TryParse(v?.ToString(), out var n) ? n : -1;

    private static int StatusCode(IResult result)
        => result is IStatusCodeHttpResult s ? s.StatusCode ?? 0 : 200;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        CapturedEvents(capture).Select(e => e.GetProperty("eventType").GetString()).ToList();

    private static IEnumerable<JsonElement> CapturedEvents(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.TryGetProperty("events", out var evs)
                ? evs.EnumerateArray()
                : System.Linq.Enumerable.Empty<JsonElement>())
            .ToList();

    private static int? SubtaskCountOnCompleted(CapturingHandler capture)
    {
        foreach (var e in CapturedEvents(capture))
        {
            if (e.GetProperty("eventType").GetString() != DecompositionEventNames.Completed) continue;
            if (e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("subtaskCount", out var c) && c.ValueKind == JsonValueKind.Number)
                return c.GetInt32();
        }
        return null;
    }

    private static List<string?> DecompositionEventIssueIds(CapturingHandler capture)
    {
        var ids = new List<string?>();
        foreach (var e in CapturedEvents(capture))
        {
            var type = e.GetProperty("eventType").GetString();
            if (type is null || !type.StartsWith("DECOMPOSITION.", StringComparison.Ordinal)) continue;
            if (e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
                tags.TryGetProperty("issueId", out var id))
                ids.Add(id.GetString());
        }
        return ids;
    }

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

    // ── seed fixtures ──────────────────────────────────────────────────

    private static FakeDocuments FreshStore() => new(Array.Empty<DocumentInstance>(), new());
    private static FakeEvents FreshEvents() => new(Array.Empty<DomainEvent>(), Array.Empty<DomainEvent>());

    private static DomainEvent Ev(string type, long seq)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = Issue, ["documentType"] = Type,
            ["documentId"] = ExistingDoc.ToString(), ["round"] = 0,
        };
        return new DomainEvent
        {
            Id = Guid.NewGuid(), Type = type, TenantId = Tenant,
            Tags = JsonSerializer.Serialize(tags),
            CreatedAt = new DateTime(2026, 7, 23, 0, 0, (int)seq, DateTimeKind.Utc),
            SequenceNumber = seq,
        };
    }

    private static DocumentInstance Row(string status) => new()
    {
        Id = ExistingDoc, DocumentType = Type, IssueId = Issue, Revision = 1, Status = status,
        ProducedByRole = "senior_developer", ProducedByAction = "decompose-issue",
        ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = Issue,
        BodyJson = ValidDecomposition(), TenantId = Tenant,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidDecomposition() =>
        "{\"summary\":\"Split rate limiting into middleware then config.\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"Middleware\",\"description\":\"limiter\",\"estimateHours\":6,\"complexity\":\"medium\",\"dependsOn\":[]}," +
        "{\"id\":\"ST-2\",\"title\":\"Config\",\"description\":\"per-tenant\",\"estimateHours\":4,\"complexity\":\"low\",\"dependsOn\":[\"ST-1\"]}]}";

    private static string InvalidDanglingDecomposition() =>
        "{\"summary\":\"one task depends on a missing id\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"a\",\"description\":\"x\",\"estimateHours\":4,\"complexity\":\"low\",\"dependsOn\":[\"ST-99\"]}]}";

    private static string InvalidEmptyDecomposition() => "{\"summary\":\"\",\"subtasks\":[]}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    private static string ConcernsReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"request-changes\",\"summary\":\"please revise\",\"issues\":[" +
        "{\"severity\":\"major\",\"category\":\"clarity\",\"description\":\"unclear\",\"suggestedFix\":\"clarify\"}]}";

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

    // ── fakes ──────────────────────────────────────────────────────────

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

    /// <summary>The legacy DECOMPOSITION.* event names (mirror of DecompositionEvents, kept local to avoid the activities-project using).</summary>
    private static class DecompositionEventNames
    {
        public const string Completed = "DECOMPOSITION.COMPLETED";
        public const string Failed = "DECOMPOSITION.FAILED";
    }
}
