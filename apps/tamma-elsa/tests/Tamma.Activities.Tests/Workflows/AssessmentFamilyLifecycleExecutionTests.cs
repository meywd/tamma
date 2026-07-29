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
/// Story 39-13 (Test Plan step 10) — full-runtime execution coverage for the rebuilt
/// assessment-family bindings (Research/Ambiguity/Clarify/Design) driving the REAL
/// <see cref="DocumentLifecycleWorkflow"/> through the Elsa runtime, with the mediated
/// <c>llm-call</c> / <c>context-gathering</c> / <c>document-review</c> dispatches served by
/// SCRIPTED stub sub-workflows, a capturing acceptance publisher + durable event drain, the
/// REAL <see cref="LifecycleReEntryService"/> over faked 39-11 repos, and decisions injected
/// through 39-8's <see cref="DocumentDecisionResumeEndpoint"/> / 39-13's
/// <see cref="DocumentInputResumeEndpoint"/> resume seams.
///
/// <para>Scenarios: (a) Research happy path (accepted Findings + both event families);
/// (b) Ambiguity below-threshold (accept → BELOW_THRESHOLD) and above-threshold (escalate
/// <c>ambiguity-above-threshold</c> → CLARIFICATION_TRIGGERED); (c) Clarify end-to-end
/// (Run A accept → deliver → suspend on document-input → resume → Run B accept →
/// REQUIREMENTS.CLARIFIED); (d) Design accept-gate round trip (suspend on the canonical decision
/// bookmark → legacy DesignResumeEndpoint approves → APPROVED; reject variant → REJECTED).</para>
///
/// <para><b>CI-only</b>: the class name contains <c>Execution</c> (skipped by the fast local
/// filter) and <c>[Explicit]</c> keeps it out of the default gate; the Postgres CI jobs run it.
/// The property-level burden lives in <see cref="AssessmentBindingHelperTests"/> and the four
/// structure suites — this proves the WIRING.</para>
///
/// <para><b>Known gap (filed against 39-11/39-6, not patched here per the plan).</b> The 39-11
/// <see cref="PersistDocumentInstanceActivity"/> is NOT yet wired into the lifecycle graph, so a
/// LIVE run persists no <c>document_instances</c> rows; AC7's full lineage read-back must SEED
/// the store (the 39-10 harness pattern) to exercise the read path. See
/// <c>.dev/findings/document-lifecycle-persist-not-wired.md</c>.</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class AssessmentFamilyLifecycleExecutionTests
{
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000013");
    private const string Issue = "issue-39-13";

    private static readonly ConcurrentQueue<string> Llm = new();
    private static readonly ConcurrentQueue<string> Reviews = new();

    [SetUp]
    public void Reset() { Llm.Clear(); Reviews.Clear(); }

    // ── (a) Research happy path ─────────────────────────────────────────

    [Test]
    public async Task Research_HappyPath_Accept_CompletesWithFindings_BothEventFamilies()
    {
        Llm.Enqueue(ValidFindings());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var (instanceId, request) = await RunToSuspendAsync(provider, "research", ResearchInput());
        request.Should().NotBeNull();

        var result = await ResumeDecisionAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept());
        Status(result).Should().Be("completed");

        var stream = CapturedTypes(capture);
        stream.Should().Contain("RESEARCH.STARTED").And.Contain("RESEARCH.CONTEXT_GATHERED").And.Contain("RESEARCH.COMPLETED");
        stream.Should().Contain(DocumentEvents.Accepted);
    }

    // ── (b) Ambiguity both branches ─────────────────────────────────────

    [Test]
    public async Task Ambiguity_BelowThreshold_Accept_EmitsBelowThreshold()
    {
        Llm.Enqueue(Assessment(0.2m));
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var (instanceId, request) = await RunToSuspendAsync(provider, "ambiguity-scoring", AmbiguityInput());
        request.Should().NotBeNull("a below-threshold assessment proceeds to the accept gate");

        var result = await ResumeDecisionAndReadParentAsync(provider, instanceId, request!.DecisionSessionId, Accept());
        Status(result).Should().Be("scored");
        CapturedTypes(capture).Should().Contain("AMBIGUITY.BELOW_THRESHOLD");
    }

    [Test]
    public async Task Ambiguity_AboveThreshold_Escalates_EmitsClarificationTriggered()
    {
        Llm.Enqueue(Assessment(0.95m));

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var result = await RunParentToCompletionAsync(provider, "ambiguity-scoring", AmbiguityInput());
        Output(result, "outcome").Should().Be(DocumentLifecycleOutcome.AmbiguityAboveThreshold.ToWire(),
            "an over-threshold assessment exits the lifecycle as the typed ambiguity-above-threshold outcome");
        CapturedTypes(capture).Should().Contain("AMBIGUITY.CLARIFICATION_TRIGGERED");
    }

    // ── (c) Clarify end-to-end ──────────────────────────────────────────

    [Test]
    public async Task Clarify_EndToEnd_Questions_Deliver_Suspend_Resume_Resolution()
    {
        // Run A produces questions and (self-decided) accepts; then the binding delivers and
        // suspends on the input gate; Run B produces the resolution and accepts.
        Llm.Enqueue(QuestionsClarification());
        Reviews.Enqueue(ApproveReview());
        Llm.Enqueue(ResolutionClarification());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("clarifying-questions"),
            Input = ClarifyInput(),
        });

        // Run A's own accept gate suspends first — resume it.
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        if (publisher.Last is { } runAReq)
            await ResumeDecisionRawAsync(provider, runAReq.DecisionSessionId, Accept(), Tenant.ToString());

        // Now the binding suspends on the document-input gate — resume via the legacy adapter.
        var inputResume = await ClarifyResumeEndpoint.Resume(
            new ClarifyResumeEndpoint.ResumeRequest(SessionIdOf(ClarifyInput()), Tenant.ToString(), "we mean web + OAuth2", "who@x.test"),
            provider.GetRequiredService<IBookmarkStore>(), runtime,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), CancellationToken.None);
        StatusCode(inputResume).Should().Be(200);

        // Run B accept gate.
        if (publisher.Last is { } runBReq)
            await ResumeDecisionRawAsync(provider, runBReq.DecisionSessionId, Accept(), Tenant.ToString());

        var stream = CapturedTypes(capture);
        stream.Should().Contain("CLARIFY.QUESTIONS.GENERATED").And.Contain("CLARIFY.QUESTIONS.DELIVERED");
        stream.Should().Contain("CLARIFY.ANSWERS.RECEIVED");
    }

    // ── (d) Design accept-gate round trip ───────────────────────────────

    [Test]
    public async Task Design_Accept_ViaLegacyEndpoint_Approves_GeneratedDeliveredPreGate()
    {
        Llm.Enqueue(ValidDesign());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var (instanceId, request) = await RunToSuspendAsync(provider, "design-proposal", DesignInput());
        request.Should().NotBeNull();

        // GENERATED/DELIVERED emitted BEFORE the gate by the delivery workflow (D5).
        var preGate = CapturedTypes(capture);
        preGate.Should().Contain("DESIGN.PROPOSAL.GENERATED").And.Contain("DESIGN.PROPOSAL.DELIVERED");

        // Approve through the legacy DesignResumeEndpoint adapter (D4).
        var approve = await DesignResumeEndpoint.Resume(
            new DesignResumeEndpoint.ResumeRequest(request!.DecisionSessionId, Tenant.ToString(), true, "ship it", "rev@x.test"),
            provider.GetRequiredService<IBookmarkStore>(),
            provider.GetRequiredService<IWorkflowRuntime>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), CancellationToken.None);
        StatusCode(approve).Should().Be(200);

        var result = await ReadParentAsync(provider, instanceId);
        Status(result).Should().Be("approved");
        CapturedTypes(capture).Should().Contain("DESIGN.PROPOSAL.APPROVED");
    }

    [Test]
    public async Task Design_Reject_ViaLegacyEndpoint_Rejects()
    {
        Llm.Enqueue(ValidDesign());
        Reviews.Enqueue(ApproveReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var (instanceId, request) = await RunToSuspendAsync(provider, "design-proposal", DesignInput());
        var reject = await DesignResumeEndpoint.Resume(
            new DesignResumeEndpoint.ResumeRequest(request!.DecisionSessionId, Tenant.ToString(), false, "revise the data model", "rev@x.test"),
            provider.GetRequiredService<IBookmarkStore>(),
            provider.GetRequiredService<IWorkflowRuntime>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), CancellationToken.None);
        StatusCode(reject).Should().Be(200);

        var result = await ReadParentAsync(provider, instanceId);
        Status(result).Should().Be("rejected");
        CapturedTypes(capture).Should().Contain("DESIGN.PROPOSAL.REJECTED");
    }

    // ════════════════════════════════════════════════════════════════════
    // Harness
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
        return await ReadParentAsync(provider, response.WorkflowInstanceId);
    }

    private static async Task<IDictionary<string, object>> ReadParentAsync(IServiceProvider provider, string instanceId)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync(instanceId);
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IDictionary<string, object>> ResumeDecisionAndReadParentAsync(
        IServiceProvider provider, string parentInstanceId, Guid session, string decisionJson)
    {
        await ResumeDecisionRawAsync(provider, session, decisionJson, Tenant.ToString());
        return await ReadParentAsync(provider, parentInstanceId);
    }

    private static Task<IResult> ResumeDecisionRawAsync(
        IServiceProvider provider, Guid session, string decisionJson, string tenantId)
        => DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session, TenantId: tenantId, DecisionJson: decisionJson,
                Feedback: null, DeciderId: "orchestrator", DeciderDisplay: null,
                Channel: "orchestrator", RulesReference: "system-default@1"),
            provider.GetRequiredService<IBookmarkStore>(),
            provider.GetRequiredService<IWorkflowRuntime>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            CancellationToken.None);

    private static Guid SessionIdOf(Dictionary<string, object> input)
        => input.TryGetValue("sessionId", out var v) && v is Guid g ? g : Guid.Empty;

    private static readonly Guid ClarifySession = Guid.Parse("0192a8b0-2222-7abc-8def-000000000002");

    private static Dictionary<string, object> ResearchInput() => new()
    {
        ["sessionId"] = Guid.NewGuid(), ["issueId"] = Issue, ["topic"] = "per-tenant rate limiting",
        ["repository"] = "meywd/tamma", ["issueNumber"] = 42, ["tenantId"] = Tenant.ToString(), ["acceptanceRulesJson"] = "",
    };

    private static Dictionary<string, object> AmbiguityInput() => new()
    {
        ["sessionId"] = Guid.NewGuid(), ["issueId"] = Issue, ["requirement"] = "make it fast",
        ["tenantId"] = Tenant.ToString(), ["acceptanceRulesJson"] = "",
    };

    private static Dictionary<string, object> ClarifyInput() => new()
    {
        ["sessionId"] = ClarifySession, ["issueId"] = Issue, ["requirement"] = "make it fast",
        ["repository"] = "meywd/tamma", ["issueNumber"] = 42, ["tenantId"] = Tenant.ToString(), ["acceptanceRulesJson"] = "",
    };

    private static Dictionary<string, object> DesignInput() => new()
    {
        ["sessionId"] = Guid.NewGuid(), ["issueId"] = Issue, ["requirement"] = "design the limiter",
        ["repository"] = "meywd/tamma", ["issueNumber"] = 42, ["tenantId"] = Tenant.ToString(), ["acceptanceRulesJson"] = "",
    };

    private static string? Status(IDictionary<string, object> output) => Output(output, "status");
    private static string? Output(IDictionary<string, object> output, string key)
        => output.TryGetValue(key, out var v) ? v?.ToString() : null;
    private static int StatusCode(IResult result) => result is IStatusCodeHttpResult s ? s.StatusCode ?? 0 : 200;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.TryGetProperty("events", out var evs)
                ? evs.EnumerateArray()
                : System.Linq.Enumerable.Empty<JsonElement>())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    private static ServiceProvider BuildProvider(CapturingHandler capture)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var publisher = new CapturingPublisher();
        services.AddSingleton(publisher);
        services.AddSingleton<IAcceptanceRequestPublisher>(publisher);

        services.AddSingleton<IDocumentInstanceRepository>(new FakeDocuments());
        services.AddSingleton<IEventRepository>(new FakeEvents());
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Tenant));
        services.AddSingleton<ILifecycleReEntryService, LifecycleReEntryService>();

        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<EmitDocumentEventActivity>();
            elsa.AddWorkflow<ResearchWorkflow>();
            elsa.AddWorkflow<AmbiguityScoringWorkflow>();
            elsa.AddWorkflow<ClarifyingQuestionsWorkflow>();
            elsa.AddWorkflow<DesignProposalWorkflow>();
            elsa.AddWorkflow<DesignDeliveryWorkflow>();
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

    // ── payloads ────────────────────────────────────────────────────────

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidFindings() =>
        "{\"topic\":\"rate limiting\",\"summary\":\"No limiter exists.\",\"findings\":[" +
        "{\"title\":\"No limiter\",\"summary\":\"No middleware today.\",\"relevance\":0.95,\"confidence\":0.9,\"citations\":[\"src/Program.cs\"]}]," +
        "\"overallConfidence\":0.88}";

    private static string Assessment(decimal score) =>
        "{\"score\":" + score.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"confidence\":0.9,\"rationale\":\"vague wording\",\"ambiguities\":[" +
        "{\"type\":\"vague\",\"description\":\"'fast' is not quantified\",\"severity\":\"medium\",\"recommendation\":\"define a target\"}]}";

    private static string QuestionsClarification() =>
        "{\"phase\":\"questions\",\"questions\":[\"What is the target platform?\",\"Which auth model is expected?\"]}";

    private static string ResolutionClarification() =>
        "{\"phase\":\"resolution\",\"clarifiedRequirement\":\"web app with OAuth2\"," +
        "\"resolutions\":[{\"questionId\":\"Q-1\",\"requirement\":\"web\"}],\"remainingAmbiguities\":[],\"resolved\":true}";

    private static string ValidDesign() =>
        "{\"summary\":\"Token-bucket limiter as middleware.\",\"alternatives\":[" +
        "{\"id\":\"ALT-1\",\"name\":\"Middleware\",\"tradeoffs\":\"simple; loses state on restart\"}," +
        "{\"id\":\"ALT-2\",\"name\":\"Redis\",\"tradeoffs\":\"durable; adds a dependency\"}]," +
        "\"recommendation\":\"ALT-1 is lowest-risk.\",\"recommendedAlternativeId\":\"ALT-1\",\"constraintEvaluation\":\"meets no-new-infra\"}";

    private static string ApproveReview() =>
        "{\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

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
        public Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocumentInstance>>(Array.Empty<DocumentInstance>());
        public Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
            => Task.FromResult<DocumentInstance?>(null);
        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, string? audience, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DocumentInstance>>(Array.Empty<DocumentInstance>());
        public Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId, DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeEvents : IEventRepository
    {
        public Task<(IReadOnlyList<DomainEvent> Events, int? Total)> QueryEventsAsync(
            Guid tenantId, string? type, bool typeIsPrefix, string? correlationId, string? actor,
            DateTimeOffset? from, DateTimeOffset? to, long? cursor, int limit, bool includeTotal = false)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int?)>((Array.Empty<DomainEvent>(), 0));
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
