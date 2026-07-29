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
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Repositories;
using Tamma.ElsaServer.Endpoints;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-14 (Test Plan step 10) — full-runtime execution coverage for the rebuilt planning
/// family: <see cref="PlanGenerationWorkflow"/> driving the REAL
/// <see cref="DocumentLifecycleWorkflow"/> (produce → validate → review(panel) → revise → accept)
/// and the <see cref="PlanReviewWorkflow"/> read-through shim, with the mediated
/// <c>llm-call</c> / <c>document-review</c> dispatches served by SCRIPTED stubs, the REAL
/// <see cref="LifecycleReEntryService"/> over faked 39-11 repos, and decisions injected through
/// 39-8's <see cref="DocumentDecisionResumeEndpoint"/>.
///
/// <para><b>CI-only</b>: the class name contains <c>Execution</c> (skipped by the fast local
/// filter) and <c>[Explicit]</c> keeps it out of the default gate; the Postgres CI jobs run it.
/// The property-level correctness burden lives in <see cref="PlanBindingHelperTests"/> +
/// <see cref="PlanReviewDecisionPortedTests"/> + the structure suites — this proves the WIRING.</para>
///
/// <para><b>Known gap (filed against 39-11/39-6, not patched here per the plan).</b>
/// <see cref="PersistDocumentInstanceActivity"/> is NOT yet wired into the lifecycle graph
/// (<c>.dev/findings/document-lifecycle-persist-not-wired.md</c>), so a LIVE run persists no
/// <c>document_instances</c> rows — AC2's "store holds the Plan + member/aggregate Reviews"
/// cannot be asserted from a fresh run. The scenarios that depend on the store-read path (the
/// crash re-entry (f) and the shim (h)) instead SEED the store with the state a prior run left
/// behind (the 39-10 harness pattern), which exercises the read path those ACs depend on.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class PlanningFamilyLifecycleExecutionTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000014");
    private const string Issue = "meywd/tamma#42";
    private const string PlanType = "plan";
    private const string DecompType = "decomposition";
    private static readonly Guid PlanDoc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000014");
    private static readonly Guid DecompDoc = Guid.Parse("0192a8b0-2222-7abc-8def-000000000014");

    private static readonly ConcurrentQueue<string> Llm = new();
    private static readonly ConcurrentQueue<string> Reviews = new();

    [SetUp]
    public void Reset() { Llm.Clear(); Reviews.Clear(); }

    // ── (a) happy path ──────────────────────────────────────────────────

    [Test]
    public async Task HappyPath_ValidPlan_Approve_Accept_CompletesApproved()
    {
        Llm.Enqueue(ValidPlan());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, SeededDecompositionStore(), DecompositionAcceptedEvents());

        var (instanceId, request) = await RunToSuspendAsync(provider, "plan-generation", PlanGenInput());
        request.Should().NotBeNull("the binding dispatches document-lifecycle, which publishes an AcceptanceRequest and suspends");

        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed", "acceptance surfaces the compat status 'completed' (D1)");
        Output(result, "decision").Should().Be("approved");
        (Output(result, "planJson") ?? "").Should().Contain("tasks");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.ProducedSuccess);
        stream.Should().Contain(DocumentEvents.Accepted);
        // D5 — the aggregate plan-review persists via the StoreRoleFindingActivity (CONTEXT.STORE_ROLE.*).
        stream.Should().Contain("CONTEXT.STORE_ROLE.COMPLETED");
        // NOTE (persist gap): "store holds the accepted Plan + Reviews" (AC2) cannot be asserted from
        // a fresh live run — PersistDocumentInstanceActivity is not wired. Exercised in (h) by seeding.
    }

    // ── (b) panel rounds = revise rounds ────────────────────────────────

    [Test]
    public async Task PanelConcerns_DriveAReviseRound_ThenApprove()
    {
        Llm.Enqueue(ValidPlan());          // round 1 produce
        Llm.Enqueue(ValidPlan());          // round 2 revise
        Reviews.Enqueue(ConcernsReview()); // round 1 panel → concerns
        Reviews.Enqueue(ApproveReview());  // round 2 panel → approve

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, SeededDecompositionStore(), DecompositionAcceptedEvents());

        var (instanceId, request) = await RunToSuspendAsync(provider, "plan-generation", PlanGenInput());
        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.RevisionStarted,
            "reviewer concerns drive a bounded revise round expressed entirely in lifecycle terms (AC3)");
    }

    // ── (c) rounds exhausted ────────────────────────────────────────────

    [Test]
    public async Task AlwaysConcerns_ExhaustsRounds_PlanJsonEmpty_ErrorNamesOutcome()
    {
        for (var i = 0; i < 6; i++) Llm.Enqueue(ValidPlan());
        for (var i = 0; i < 6; i++) Reviews.Enqueue(ConcernsReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, SeededDecompositionStore(), DecompositionAcceptedEvents());

        var result = await RunParentToCompletionAsync(provider, "plan-generation", PlanGenInput());
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated,
            "always-concerns exhausts the round budget and exits as a typed escalation (AC1/AC3)");
        (Output(result, "planJson") ?? "").Should().BeEmpty("a non-accept exit outputs planJson=\"\" so the parent's empty-plan edge fires (D1)");
        (Output(result, "error") ?? "").Should().Contain("escalated");
        Output(result, "decision").Should().Be("needsHuman");
    }

    // ── (d) validation exhausted ────────────────────────────────────────

    [Test]
    public async Task AlwaysInvalid_EscalatesValidationExhausted()
    {
        for (var i = 0; i < 8; i++) Llm.Enqueue(InvalidPlan());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, SeededDecompositionStore(), DecompositionAcceptedEvents());

        var result = await RunParentToCompletionAsync(provider, "plan-generation", PlanGenInput());
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
        Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
    }

    // ── (f) crash mid-review → re-enter at review ───────────────────────

    [Test]
    public async Task CrashMidReview_ReEntersAtReview_NoSecondProduce()
    {
        var store = new FakeDocuments(
            latestAccepted: System.Array.Empty<DocumentInstance>(),
            byId: new() { [PlanDoc] = PlanRow("validated") });
        var events = new FakeEvents(new[]
        {
            Ev("DOCUMENT.PRODUCED.SUCCESS", PlanType, PlanDoc, 1),
            Ev("DOCUMENT.VALIDATED.SUCCESS", PlanType, PlanDoc, 2),
        }, System.Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, store, events);

        Reviews.Enqueue(ApproveReview());

        var (instanceId, request) = await RunToSuspendAsync(provider, "plan-generation", PlanGenInput());
        request.Should().NotBeNull("re-entry at Review reviews the existing revision then suspends on the accept gate");

        var stream = CapturedTypes(capture);
        stream.Should().Contain(DocumentEvents.Reentered);
        stream.Should().NotContain(DocumentEvents.ProducedSuccess, "produce is skipped on a Review re-entry (AC7)");

        var result = await ResumeAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept(), Tenant.ToString());
        Status(result).Should().Be("completed");
    }

    // ── (h) shim compat ─────────────────────────────────────────────────

    [Test]
    public async Task Shim_WithAcceptedPlan_ReturnsApproved_NonEmptyDiscussionLog()
    {
        var store = new FakeDocuments(
            latestAccepted: new[] { PlanRow("accepted") },
            byId: new() { [PlanDoc] = PlanRow("accepted") });
        var events = new FakeEvents(new[]
        {
            Ev("DOCUMENT.PRODUCED.SUCCESS", PlanType, PlanDoc, 1),
            Ev("DOCUMENT.VALIDATED.SUCCESS", PlanType, PlanDoc, 2),
            Ev("DOCUMENT.REVIEWED", PlanType, PlanDoc, 3),
            Ev("DOCUMENT.ACCEPTED", PlanType, PlanDoc, 4),
        }, System.Array.Empty<DomainEvent>());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, store, events);

        var result = await RunParentToCompletionAsync(provider, "plan-review", PlanReviewInput());
        Output(result, "decision").Should().Be("approved", "an accepted plan in the store maps to the legacy 'approved' verdict (D1)");
        (Output(result, "discussionLog") ?? "[]").Should().NotBe("[]", "the shim projects the plan's round lineage into a non-empty discussion log");
        Output(result, "deferred").Should().Be("[]", "defer/split retire from the review surface (D2)");
        Output(result, "split").Should().Be("[]");
    }

    [Test]
    public async Task Shim_WithNoAcceptedPlan_ReturnsNeedsHuman()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, FreshStore(), FreshEvents());

        var result = await RunParentToCompletionAsync(provider, "plan-review", PlanReviewInput());
        Output(result, "decision").Should().Be("needsHuman", "no accepted plan → the shim escalates to human (D1)");
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness (mirrors IssueDecompositionLifecycleExecutionTests)
    // ════════════════════════════════════════════════════════════════════

    private static async Task<(string InstanceId, AcceptanceRequest? Request)> RunToSuspendAsync(
        IServiceProvider provider, string definitionId, Dictionary<string, object> input)
    {
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(definitionId),
            Input = input,
        });
        return (response.WorkflowInstanceId, publisher.Last);
    }

    private static async Task<IDictionary<string, object>> RunParentToCompletionAsync(
        IServiceProvider provider, string definitionId, Dictionary<string, object> input)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(definitionId),
            Input = input,
        });
        var client2 = await runtime.CreateClientAsync(response.WorkflowInstanceId);
        var state = await client2.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IDictionary<string, object>> ResumeAndReadParentAsync(
        IServiceProvider provider, string parentInstanceId, Guid session, string decisionJson, string tenantId)
    {
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session, TenantId: tenantId, DecisionJson: decisionJson,
                Feedback: null, DeciderId: "orchestrator", DeciderDisplay: null,
                Channel: "orchestrator", RulesReference: "system-default@1"),
            bookmarkStore, runtime, loggerFactory, CancellationToken.None);

        var client = await runtime.CreateClientAsync(parentInstanceId);
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static Dictionary<string, object> PlanGenInput() => new()
    {
        ["issueId"] = Issue,
        ["repository"] = "meywd/tamma",
        ["issueNumber"] = 42,
        ["poSummary"] = "Add per-tenant rate limiting",
        ["contextIds"] = "[]",
        ["workItemJson"] = "{\"type\":\"issue\",\"title\":\"rate limiting\"}",
        ["reviewNotes"] = "",
        ["revisionNumber"] = 0,
        ["tenantId"] = Tenant.ToString(),
        ["acceptanceRulesJson"] = "",
    };

    private static Dictionary<string, object> PlanReviewInput() => new()
    {
        ["issueId"] = Issue,
        ["repository"] = "meywd/tamma",
        ["issueNumber"] = 42,
        ["planJson"] = "{}",
        ["contextIds"] = "[]",
        ["workItemJson"] = "{\"type\":\"issue\"}",
        ["tenantId"] = Tenant.ToString(),
    };

    private static string? Status(IDictionary<string, object> output) => Output(output, "status");
    private static string? Output(IDictionary<string, object> output, string key)
        => output.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.TryGetProperty("events", out var evs)
                ? evs.EnumerateArray()
                : System.Linq.Enumerable.Empty<JsonElement>())
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

        services.AddSingleton(docs);
        services.AddSingleton(events);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Tenant));
        services.AddSingleton<ILifecycleReEntryService, LifecycleReEntryService>();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<PlanGenerationWorkflow>();
            elsa.AddWorkflow<PlanReviewWorkflow>();
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

    private static FakeDocuments FreshStore() => new(System.Array.Empty<DocumentInstance>(), new());
    private static FakeEvents FreshEvents() => new(System.Array.Empty<DomainEvent>(), System.Array.Empty<DomainEvent>());

    private static FakeDocuments SeededDecompositionStore() => new(
        latestAccepted: new[] { DecompRow() },
        byId: new() { [DecompDoc] = DecompRow() });

    private static FakeEvents DecompositionAcceptedEvents() => new(new[]
    {
        Ev("DOCUMENT.PRODUCED.SUCCESS", DecompType, DecompDoc, 1),
        Ev("DOCUMENT.VALIDATED.SUCCESS", DecompType, DecompDoc, 2),
        Ev("DOCUMENT.REVIEWED", DecompType, DecompDoc, 3),
        Ev("DOCUMENT.ACCEPTED", DecompType, DecompDoc, 4),
    }, System.Array.Empty<DomainEvent>());

    private static DomainEvent Ev(string type, string docType, Guid docId, long seq)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = Issue, ["documentType"] = docType,
            ["documentId"] = docId.ToString(), ["round"] = 0,
        };
        return new DomainEvent
        {
            Id = Guid.NewGuid(), Type = type, TenantId = Tenant,
            Tags = JsonSerializer.Serialize(tags),
            CreatedAt = new DateTime(2026, 7, 23, 0, 0, (int)seq, DateTimeKind.Utc),
            SequenceNumber = seq,
        };
    }

    private static DocumentInstance PlanRow(string status) => new()
    {
        Id = PlanDoc, DocumentType = PlanType, IssueId = Issue, Revision = 1, Status = status,
        ProducedByRole = "architect", ProducedByAction = "plan-system-design",
        ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = Issue,
        BodyJson = ValidPlan(), TenantId = Tenant,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static DocumentInstance DecompRow() => new()
    {
        Id = DecompDoc, DocumentType = DecompType, IssueId = Issue, Revision = 1, Status = "accepted",
        ProducedByRole = "senior_developer", ProducedByAction = "decompose-issue",
        ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = Issue,
        BodyJson = "{\"summary\":\"split it\",\"subtasks\":[{\"id\":\"ST-1\",\"title\":\"a\",\"description\":\"x\",\"estimateHours\":4,\"complexity\":\"low\",\"dependsOn\":[]}]}",
        TenantId = Tenant, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidPlan() =>
        "{\"tasks\":[{\"id\":\"T-1\",\"description\":\"add table\",\"files\":[\"db/001.sql\"],\"dependsOn\":[],\"testing\":\"migration applies\"}]," +
        "\"files\":[\"db/001.sql\"]}";

    private static string InvalidPlan() => "{\"tasks\":[]}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000014\",\"documentType\":\"plan\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    private static string ConcernsReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000014\",\"documentType\":\"plan\"}," +
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
        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, string? audience, CancellationToken ct)
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
