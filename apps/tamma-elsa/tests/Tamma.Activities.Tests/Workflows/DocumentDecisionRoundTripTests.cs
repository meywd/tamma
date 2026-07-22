using System.Globalization;
using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.Documents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-8 (AC7 a/b/c, AC4/AC5 runtime halves). A GENUINE suspend/resume round-trip of the
/// ONE generic document-decision gate through the REAL Elsa runtime (the same harness intent as
/// the plan's Testcontainers round-trip, run here against Elsa's in-process persistence — no
/// Docker needed, and the plan's step-8 <c>GateHarnessWorkflow</c> fallback since 39-6 is not
/// landed). Drives publish→gate→terminal:
/// <list type="bullet">
///   <item>(a) supervised: the lifecycle SUSPENDS on the tenant-folded bookmark and emits
///     <c>APPROVAL.REQUESTED</c>; resuming with an Accept reaches the <c>Accepted</c> terminal
///     and emits <c>APPROVAL.PROVIDED</c> carrying the decider + a positive <c>durationMs</c>
///     across the genuine suspend/resume (D4 proven against real persistence).</item>
///   <item>(b) rejection: resuming with a <c>reject</c> + feedback reaches the <c>Rejected</c>
///     terminal with the feedback on the trail.</item>
///   <item>(c) escalation: an <c>escalate</c> resume reaches the <c>Escalated</c> terminal and
///     the escalated exit emits <c>ESCALATION.TRIGGERED</c> with the FULL lineage embedded (the
///     disposition half — <c>ESCALATION.RESOLVED</c> — is covered by
///     <c>Tamma.Api.Tests.Documents.EscalationDispositionTests</c>).</item>
/// </list>
///
/// <para>Excluded from the fast local test filter by the <c>RoundTrip</c> name; runs in CI.</para>
/// </summary>
[TestFixture]
public class DocumentDecisionRoundTripTests
{
    private const string LineageJson =
        "{\"drafts\":[{\"id\":\"d1\",\"state\":\"reviewed\"}],\"roundsUsed\":3,\"outcome\":\"rounds-exhausted\"}";

    [Test]
    public async Task Supervised_Suspends_EmitsRequested_ThenAcceptResumesToAccepted_WithDurationMs()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();

        var (state, workflow) = await RunToSuspendAsync(provider, session, tenant);

        // Suspended on the canonical tenant-folded bookmark, and APPROVAL.REQUESTED is on the stream.
        state.SubStatus.Should().Be(WorkflowSubStatus.Suspended, "the gate must suspend the lifecycle");
        var expectedBookmark = WaitForDocumentDecisionActivity.DecisionBookmarkName(tenant, session);
        var bookmark = state.Bookmarks.SingleOrDefault(b => b.Name == expectedBookmark);
        bookmark.Should().NotBeNull("the gate must register the canonical decision bookmark");
        CapturedTypes(capture).Should().Contain(ApprovalEvents.Requested);

        // Resume with Accept.
        var result = await ResumeAsync(provider, workflow, state, bookmark!.Id, new Dictionary<string, object>
        {
            ["DecisionJson"] = "{\"kind\":\"accept\"}",
            ["Feedback"] = "ship it",
            ["DeciderId"] = "alice@x.test",
            ["DeciderDisplay"] = "Alice",
            ["Channel"] = "user",
            ["RulesReference"] = "system-default@1",
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        Terminal(result).Should().Be("Accepted");

        var provided = CapturedEvents(capture).Single(e => Type(e) == ApprovalEvents.Provided);
        Data(provided, "deciderId").Should().Be("alice@x.test");
        Data(provided, "channel").Should().Be("user");
        provided.GetProperty("data").GetProperty("durationMs").GetInt64().Should().BeGreaterThan(0,
            "durationMs must be positive across a genuine suspend/resume (D4)");
    }

    [Test]
    public async Task Rejection_ResumesToRejected_WithFeedbackOnTheTrail()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var (state, workflow) = await RunToSuspendAsync(provider, session, tenant);
        var bookmark = state.Bookmarks.Single();

        var result = await ResumeAsync(provider, workflow, state, bookmark.Id, new Dictionary<string, object>
        {
            ["DecisionJson"] = "{\"kind\":\"reject\",\"reason\":\"wrong approach\"}",
            ["Feedback"] = "revise the data model",
            ["DeciderId"] = "bob@x.test",
            ["Channel"] = "user",
        });

        Terminal(result).Should().Be("Rejected");
        var provided = CapturedEvents(capture).Single(e => Type(e) == ApprovalEvents.Provided);
        Data(provided, "decisionKind").Should().Be("reject");
        Data(provided, "feedback").Should().Be("revise the data model");
    }

    [Test]
    public async Task Escalation_ResumesToEscalated_EmitsTriggeredWithFullLineage()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture);

        var session = Guid.NewGuid();
        var tenant = Guid.NewGuid().ToString();
        var (state, workflow) = await RunToSuspendAsync(provider, session, tenant);
        var bookmark = state.Bookmarks.Single();

        var result = await ResumeAsync(provider, workflow, state, bookmark.Id, new Dictionary<string, object>
        {
            ["DecisionJson"] = "{\"kind\":\"escalate\",\"reason\":\"rounds-exhausted\",\"detail\":\"rounds ran out\"}",
            ["Channel"] = "orchestrator",
        });

        Terminal(result).Should().Be("Escalated");

        var triggered = CapturedEvents(capture).Single(e => Type(e) == ApprovalEvents.EscalationTriggered);
        var lineage = triggered.GetProperty("data").GetProperty("lineage");
        lineage.ValueKind.Should().Be(JsonValueKind.Object, "the escalation must carry lineage as an object, never a bare string");
        lineage.GetProperty("roundsUsed").GetInt32().Should().Be(3);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<(Elsa.Workflows.State.WorkflowState State, Workflow Workflow)> RunToSuspendAsync(
        IServiceProvider rootProvider, Guid session, string tenant)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var workflow = GateHarnessWorkflow.Build(session, tenant, DateTimeOffset.UtcNow.AddSeconds(-2));
        var result = await runner.RunAsync(workflow, new RunWorkflowOptions(), CancellationToken.None);
        return (result.WorkflowState, workflow);
    }

    private static async Task<Elsa.Workflows.Models.RunWorkflowResult> ResumeAsync(
        IServiceProvider rootProvider, Workflow workflow,
        Elsa.Workflows.State.WorkflowState state, string bookmarkId, IDictionary<string, object> input)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        return await runner.RunAsync(workflow, state,
            new RunWorkflowOptions { BookmarkId = bookmarkId, Input = input }, CancellationToken.None);
    }

    private static string? Terminal(Elsa.Workflows.Models.RunWorkflowResult result)
        => result.WorkflowState.Output.TryGetValue("terminal", out var t) ? t?.ToString() : null;

    private static List<JsonElement> CapturedEvents(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray()
                .Select(e => e.Clone()))
            .ToList();

    private static List<string?> CapturedTypes(CapturingHandler capture) =>
        CapturedEvents(capture).Select(Type).ToList();

    private static string? Type(JsonElement e) => e.GetProperty("eventType").GetString();

    private static string? Data(JsonElement e, string key)
        => e.GetProperty("data").TryGetProperty(key, out var v) ? v.GetString() : null;

    private static ServiceProvider BuildProvider(CapturingHandler capture)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivity<WaitForDocumentDecisionActivity>();
            elsa.AddActivity<EmitEscalationEventActivity>();
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

    /// <summary>
    /// Step-8 fallback — a test-only workflow exercising publish→gate→terminal for the ONE
    /// generic document-decision gate (39-6's real <c>DocumentLifecycleWorkflow</c> is not landed
    /// yet). The gate branches to a <c>terminal</c> output per decision kind; the Escalate branch
    /// additionally emits <c>ESCALATION.TRIGGERED</c> with the full lineage.
    /// </summary>
    private static class GateHarnessWorkflow
    {
        public static Workflow Build(Guid session, string? tenant, DateTimeOffset requestedAt)
        {
            var gate = new WaitForDocumentDecisionActivity
            {
                Id = "DecisionGate",
                SessionId = new Input<Guid>(_ => session),
                TenantId = new Input<string?>(_ => tenant),
                RequestedAtUtc = new Input<string>(_ => requestedAt.ToString("O", CultureInfo.InvariantCulture)),
                RulesReference = new Input<string?>(_ => "system-default@1"),
                IssueId = new Input<string?>(_ => "issue-9"),
                DocumentId = new Input<string?>(_ => "doc-7"),
                DocumentType = new Input<string?>(_ => "design"),
                CorrelationId = new Input<string?>(_ => "corr-5"),
            };

            var setAccepted = Terminal("SetAccepted", "Accepted");
            var setRevision = Terminal("SetRevision", "RevisionRequested");
            var setRejected = Terminal("SetRejected", "Rejected");
            var setEscalated = Terminal("SetEscalated", "Escalated");

            var emitEscalation = new EmitEscalationEventActivity
            {
                Id = "EmitEscalation",
                EventType = new Input<string>(_ => ApprovalEvents.EscalationTriggered),
                EscalationId = new Input<string?>(_ => Guid.NewGuid().ToString()),
                Outcome = new Input<string?>(_ => "rounds-exhausted"),
                LineageJson = new Input<string?>(_ => LineageJson),
                RulesReference = new Input<string?>(_ => "system-default@1"),
                Channel = new Input<string?>(_ => "orchestrator"),
                IssueId = new Input<string?>(_ => "issue-9"),
                DocumentId = new Input<string?>(_ => "doc-7"),
                DocumentType = new Input<string?>(_ => "design"),
                CorrelationId = new Input<string?>(_ => "corr-5"),
                SessionId = new Input<string?>(_ => session.ToString()),
                TenantId = new Input<string?>(_ => tenant),
                Detail = new Input<string?>(_ => "rounds ran out"),
            };

            var finishAccepted = new Finish { Id = "FinishAccepted" };
            var finishRevision = new Finish { Id = "FinishRevision" };
            var finishRejected = new Finish { Id = "FinishRejected" };
            var finishEscalated = new Finish { Id = "FinishEscalated" };

            var flowchart = new Flowchart
            {
                Id = "GateHarness",
                Start = gate,
                Activities =
                {
                    gate,
                    setAccepted, finishAccepted,
                    setRevision, finishRevision,
                    setRejected, finishRejected,
                    emitEscalation, setEscalated, finishEscalated,
                },
                Connections =
                {
                    new FlowConnection(new FlowEndpoint(gate, "Accept"), new FlowEndpoint(setAccepted)),
                    new FlowConnection(new FlowEndpoint(setAccepted), new FlowEndpoint(finishAccepted)),
                    new FlowConnection(new FlowEndpoint(gate, "RequestRevision"), new FlowEndpoint(setRevision)),
                    new FlowConnection(new FlowEndpoint(setRevision), new FlowEndpoint(finishRevision)),
                    new FlowConnection(new FlowEndpoint(gate, "Reject"), new FlowEndpoint(setRejected)),
                    new FlowConnection(new FlowEndpoint(setRejected), new FlowEndpoint(finishRejected)),
                    new FlowConnection(new FlowEndpoint(gate, "Escalate"), new FlowEndpoint(emitEscalation)),
                    new FlowConnection(new FlowEndpoint(emitEscalation), new FlowEndpoint(setEscalated)),
                    new FlowConnection(new FlowEndpoint(setEscalated), new FlowEndpoint(finishEscalated)),
                },
            };

            return new Workflow(flowchart);
        }

        private static SetOutput Terminal(string id, string value) => new()
        {
            Id = id,
            OutputName = new("terminal"),
            OutputValue = new(_ => (object)value),
        };
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
