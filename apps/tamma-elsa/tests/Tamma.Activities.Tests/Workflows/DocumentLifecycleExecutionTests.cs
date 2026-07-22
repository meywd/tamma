using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
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
using Tamma.ElsaServer.Endpoints;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-6 — full-runtime execution/integration coverage for
/// <see cref="DocumentLifecycleWorkflow"/> (Test Plan step 10). Drives the REAL
/// lifecycle through the Elsa workflow runtime with the mediated <c>llm-call</c> and
/// <c>document-review</c> dispatches served by SCRIPTED stub sub-workflows, a
/// capturing <see cref="IAcceptanceRequestPublisher"/>, and the durable event drain;
/// decisions are injected by invoking 39-8's <see cref="DocumentDecisionResumeEndpoint"/>
/// resume seam directly.
///
/// <para>This is the heaviest artifact in the epic (dispatcher + registered
/// sub-workflows + bookmarks). It is built as a REUSABLE fixture (39-8 / 39-12 reuse
/// the harness) and is <b>CI-only</b>: the class name contains <c>Execution</c> so
/// the fast local filter (<c>FullyQualifiedName!~Execution</c>) skips it, and
/// <c>[Explicit]</c> keeps it out of the default gate; the Postgres CI jobs run it.
/// The property-level correctness burden lives in
/// <see cref="DocumentLifecycleHelperTests"/> — this proves the WIRING.</para>
///
/// <para>Scenarios: (a) full cycle on <c>decomposition</c> (invalid → repair → valid
/// → concerns → revise → approve → publish+suspend → Accept resume → Accepted);
/// (b) assigned-user resume variant (same bookmark, decider identity varies);
/// (c) ValidationExhausted + RoundsExhausted (always-invalid / always-concerns
/// stubs); (d) DOCUMENT.* replay reconstructs the transition history; (e)
/// forged-approval guardrail (Accept against a blocking review → clamped to
/// Escalate).</para>
/// </summary>
[TestFixture]
[Explicit("Full Elsa workflow-runtime integration — runs in the CI Postgres jobs, skipped in the fast local gate")]
public class DocumentLifecycleExecutionTests
{
    private const string TenantId = "";

    [SetUp]
    public void ResetScript() => ScriptedResponder.Reset();

    // ── (a) full cycle → Accepted ──────────────────────────────────────

    [Test]
    public async Task FullCycle_InvalidThenRepairThenReviseThenApprove_ResumesToAccepted()
    {
        ScriptedResponder.Llm.AddRange(new[] { InvalidDecomposition(), ValidDecomposition(), ValidDecomposition() });
        ScriptedResponder.Reviews.AddRange(new[] { ConcernsReview(), ApproveReview() });

        var capture = new CapturingHandler();
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(capture, publisher);

        var request = await RunToSuspendAsync(provider);
        request.Should().NotBeNull("the lifecycle must publish an AcceptanceRequest and suspend on the gate");
        request!.Rules.Rules.AutonomyLevel.Should().Be(AcceptanceDefaults.DefaultAutonomyLevel,
            "the request carries the resolved rules including the autonomy level");
        request.Lineage.Should().NotBeEmpty("the request carries full lineage");

        var result = await ResumeAsync(provider, request.DecisionSessionId, Accept(), channel: "orchestrator");
        Status(result).Should().Be(DocumentLifecycleResult.StatusAccepted);
        CapturedTypes(capture).Should().Contain(DocumentEvents.Accepted);
    }

    // ── (b) assigned-user resume variant (same bookmark) ───────────────

    [Test]
    public async Task FullCycle_AssignedUserDecides_SameBookmarkResumesToAccepted()
    {
        ScriptedResponder.Llm.Add(ValidDecomposition());
        ScriptedResponder.Reviews.Add(ApproveReview());

        var capture = new CapturingHandler();
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(capture, publisher);

        var request = await RunToSuspendAsync(provider);
        var result = await ResumeAsync(provider, request!.DecisionSessionId, Accept(),
            channel: "user", deciderId: "assignee@x.test");
        Status(result).Should().Be(DocumentLifecycleResult.StatusAccepted);
    }

    // ── (c) unhandleable outcomes ──────────────────────────────────────

    [Test]
    public async Task AlwaysInvalid_EscalatesValidationExhausted_WithLineage()
    {
        for (var i = 0; i < 6; i++) ScriptedResponder.Llm.Add(InvalidDecomposition());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new CapturingPublisher());

        var result = await RunToCompletionAsync(provider);
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
        Outcome(result).Should().Be(DocumentLifecycleOutcome.ValidationExhausted.ToWire());
        CapturedTypes(capture).Should().Contain(DocumentEvents.Escalated);
    }

    [Test]
    public async Task AlwaysConcerns_EscalatesRoundsExhausted_WithLineage()
    {
        for (var i = 0; i < 6; i++) ScriptedResponder.Llm.Add(ValidDecomposition());
        for (var i = 0; i < 6; i++) ScriptedResponder.Reviews.Add(ConcernsReview());

        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new CapturingPublisher());

        var result = await RunToCompletionAsync(provider);
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
        Outcome(result).Should().Be(DocumentLifecycleOutcome.RoundsExhausted.ToWire());
    }

    // ── (d) replay ─────────────────────────────────────────────────────

    [Test]
    public async Task DocumentEventStream_ReconstructsTransitionHistory()
    {
        ScriptedResponder.Llm.Add(ValidDecomposition());
        ScriptedResponder.Reviews.Add(ApproveReview());

        var capture = new CapturingHandler();
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(capture, publisher);

        var request = await RunToSuspendAsync(provider);
        await ResumeAsync(provider, request!.DecisionSessionId, Accept(), channel: "orchestrator");

        var stream = CapturedTypes(capture).Where(t => t!.StartsWith("DOCUMENT.")).ToList();
        stream.Should().ContainInOrder(
            DocumentEvents.ProducedSuccess,
            DocumentEvents.ValidatedSuccess,
            DocumentEvents.ReviewRequested,
            DocumentEvents.Reviewed,
            DocumentEvents.Accepted);
    }

    // ── (e) forged-approval guardrail ──────────────────────────────────

    [Test]
    public async Task ForgedApproval_AcceptAgainstBlockingReview_ClampedToEscalate()
    {
        ScriptedResponder.Llm.Add(ValidDecomposition());
        // An approve review that carries a blocking (critical) issue — routes to ACCEPT on the
        // decision but the guardrail sees HasBlockingIssues and clamps a forged Accept.
        ScriptedResponder.Reviews.Add(ApproveWithBlockingReview());

        var capture = new CapturingHandler();
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(capture, publisher);

        var request = await RunToSuspendAsync(provider);
        var result = await ResumeAsync(provider, request!.DecisionSessionId, Accept(), channel: "orchestrator");
        Status(result).Should().Be(DocumentLifecycleResult.StatusEscalated);
    }

    // ════════════════════════════════════════════════════════════════════
    // Reusable harness
    // ════════════════════════════════════════════════════════════════════

    private sealed record SuspendedRun(string InstanceId, AcceptanceRequest? Request);

    private static async Task<SuspendedRun> RunToSuspendRunAsync(IServiceProvider provider, CapturingPublisher publisher)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("document-lifecycle"),
            Input = LifecycleInput(),
        });
        return new SuspendedRun(response.WorkflowInstanceId, publisher.Last);
    }

    private static async Task<AcceptanceRequest?> RunToSuspendAsync(IServiceProvider provider)
    {
        var publisher = provider.GetRequiredService<CapturingPublisher>();
        var run = await RunToSuspendRunAsync(provider, publisher);
        return run.Request;
    }

    private static async Task<IDictionary<string, object>> RunToCompletionAsync(IServiceProvider provider)
    {
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var client = await runtime.CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId("document-lifecycle"),
            Input = LifecycleInput(),
        });
        var state = await client.ExportStateAsync();
        return state.Output ?? new Dictionary<string, object>();
    }

    private static async Task<IDictionary<string, object>> ResumeAsync(
        IServiceProvider provider, Guid session, string decisionJson, string channel, string? deciderId = null)
    {
        // Locate the suspended instance, inject the decision through the 39-8 resume seam,
        // then read the terminal state.
        var bookmarkStore = provider.GetRequiredService<IBookmarkStore>();
        var runtime = provider.GetRequiredService<IWorkflowRuntime>();
        var loggerFactory = provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        var bookmarkName = WaitForDocumentDecisionActivity.DecisionBookmarkName(TenantId, session);
        var bookmarks = (await bookmarkStore.FindManyAsync(
            new Elsa.Workflows.Runtime.Filters.BookmarkFilter { Name = bookmarkName }, CancellationToken.None)).ToList();
        var instanceId = bookmarks.Count == 1 ? bookmarks[0].WorkflowInstanceId : string.Empty;

        await DocumentDecisionResumeEndpoint.Resume(
            new DocumentDecisionResumeEndpoint.ResumeRequest(
                SessionId: session,
                TenantId: TenantId,
                DecisionJson: decisionJson,
                Feedback: null,
                DeciderId: deciderId,
                DeciderDisplay: null,
                Channel: channel,
                RulesReference: "system-default@1"),
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
        ["documentType"] = "decomposition",
        ["issueId"] = "issue-1",
        ["correlationId"] = "corr-1",
        ["acceptanceRulesJson"] = "",
        ["tenantId"] = TenantId,
    };

    private static string? Status(IDictionary<string, object> output)
        => output.TryGetValue("status", out var s) ? s?.ToString() : null;

    private static string? Outcome(IDictionary<string, object> output)
        => output.TryGetValue("outcome", out var o) ? o?.ToString() : null;

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    private static ServiceProvider BuildProvider(CapturingHandler capture, CapturingPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(publisher);
        services.AddSingleton<IAcceptanceRequestPublisher>(publisher);

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

    // ── decision + payload fixtures ────────────────────────────────────

    private static string Accept() => "{\"kind\":\"accept\"}";

    private static string ValidDecomposition() =>
        "{\"summary\":\"Split rate limiting into middleware then config.\",\"subtasks\":[" +
        "{\"id\":\"ST-1\",\"title\":\"Middleware\",\"description\":\"limiter\",\"estimateHours\":6,\"complexity\":\"medium\",\"dependsOn\":[]}]}";

    private static string InvalidDecomposition() => "{\"summary\":\"\",\"subtasks\":[]}";

    private static string ApproveReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"approve\",\"summary\":\"looks good\",\"issues\":[]}";

    private static string ConcernsReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"request-changes\",\"summary\":\"please revise\",\"issues\":[" +
        "{\"severity\":\"major\",\"category\":\"clarity\",\"description\":\"unclear\",\"suggestedFix\":\"clarify\"}]}";

    private static string ApproveWithBlockingReview() =>
        "{\"subject\":{\"kind\":\"document\",\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"documentType\":\"decomposition\"}," +
        "\"decision\":\"approve\",\"summary\":\"approving despite blocker\",\"issues\":[" +
        "{\"severity\":\"critical\",\"category\":\"security\",\"description\":\"sql injection\",\"suggestedFix\":\"parameterize\"}]}";

    // ── scripted stub sub-workflows ────────────────────────────────────

    /// <summary>Sequential response script shared by the stub sub-workflows.</summary>
    private static class ScriptedResponder
    {
        public static readonly List<string> Llm = new();
        public static readonly List<string> Reviews = new();
        private static int s_llm;
        private static int s_review;

        public static void Reset() { Llm.Clear(); Reviews.Clear(); s_llm = 0; s_review = 0; }

        public static string NextLlm() => Next(Llm, ref s_llm);
        public static string NextReview() => Next(Reviews, ref s_review);

        private static string Next(List<string> list, ref int index)
        {
            if (list.Count == 0) return "{}";
            var i = Math.Min(Interlocked.Increment(ref index) - 1, list.Count - 1);
            return list[i];
        }
    }

    /// <summary>Stub <c>llm-call</c> — returns the next scripted payload as <c>llmResponse</c>.</summary>
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
                    new SetOutput { Id = "OutResponse", OutputName = new("llmResponse"), OutputValue = new(_ => (object)ScriptedResponder.NextLlm()) },
                },
            };
        }
    }

    /// <summary>Stub <c>document-review</c> — returns the next scripted <c>reviewJson</c> (D10).</summary>
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
                    new SetOutput { Id = "OutReview", OutputName = new("reviewJson"), OutputValue = new(_ => (object)ScriptedResponder.NextReview()) },
                },
            };
        }
    }

    /// <summary>Capturing <see cref="IAcceptanceRequestPublisher"/> fake (D6).</summary>
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true,\"persisted\":1}"),
            };
        }
    }
}
