using System.Net;
using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// EXECUTION coverage for the review fix (CRITICAL — "stuck, no terminal" hang). This
/// runs the cycle's apply branch through the REAL Elsa runtime (the same harness pattern
/// as <c>EventPersistencePipelineTests</c>: a real <see cref="IWorkflowRunner"/> with the
/// DCB-event drain installed and a capturing API client), with a FAULTING
/// <see cref="ITriageApplyClient"/>, and asserts the load-bearing guarantee the topology
/// tests cannot prove:
///
/// <list type="number">
///   <item>an apply HTTP failure produces EXACTLY ONE <c>TRIAGE.ISSUE.FAILED</c> cycle
///         terminal (never a stuck instance with no terminal), and</item>
///   <item>NO false <c>TRIAGE.ISSUE.COMPLETED</c> is emitted on the fault path, and</item>
///   <item>the loud leaf <c>TRIAGE.APPLY.RESULT.FAILED</c> still fires.</item>
/// </list>
///
/// <para>The flowchart under test is the EXACT apply-branch wiring of
/// <c>TriageItemCycleWorkflow</c> (real <see cref="ApplyTriageResultActivity"/> with its
/// <c>Success</c>/<c>Failure</c> outcomes → the real <see cref="EmitTriageCycleEventActivity"/>
/// COMPLETED / FAILED terminals). Running the whole cycle end-to-end would require an
/// async <c>IWorkflowDispatcher</c> + three registered, bookmark-resuming sub-workflows
/// (context / panel / PO) — far heavier than the load-bearing seam, which is precisely
/// the apply outcome → cycle terminal routing this proves. The happy path
/// (Success → COMPLETED) is exercised symmetrically by
/// <see cref="ApplySuccess_EmitsExactlyOneCompletedTerminal_AndNoFailed"/>.</para>
///
/// <para>Mirrors <c>MergeApprovalWorkflowTests.Workflow_UsesContinueWithIncidentsStrategy_…</c>
/// in intent (a fault must not halt silently) but proves it by EXECUTION, not topology.</para>
/// </summary>
[TestFixture]
public class TriageItemCycleApplyFaultExecutionTests
{
    private const string Repo = "owner/repo";
    private const string ItemJson = """{"type":"issue","number":7,"title":"t","body":"b","source":"issue"}""";
    private const string OkDecision = """{"status":"ok","priority":"high","type":"bug","automation":"tamma-auto","labels":["bug"],"comment":"c"}""";

    [Test]
    public async Task ApplyHttpFailure_EmitsExactlyOneFailedTerminal_NeverFalseCompleted()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new FaultingApplyClient(statusCode: 500));

        await RunApplyBranchAsync(provider);

        var cycleTypes = CapturedEventTypes(capture).Where(t => t!.StartsWith("TRIAGE.ISSUE.")).ToList();

        cycleTypes.Should().ContainSingle(
            "an apply HTTP failure must yield EXACTLY ONE cycle terminal (no stuck/no-terminal hang)");
        cycleTypes.Single().Should().Be(TriageCycleEvents.Failed,
            "the single cycle terminal on an apply HTTP failure must be the loud TRIAGE.ISSUE.FAILED");
        cycleTypes.Should().NotContain(TriageCycleEvents.Completed,
            "a failed apply must NEVER surface as TRIAGE.ISSUE.COMPLETED (no false success)");
    }

    [Test]
    public async Task ApplyHttpFailure_EmitsTheLoudLeafApplyFailedEvent()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new FaultingApplyClient(statusCode: 403));

        await RunApplyBranchAsync(provider);

        var allTypes = CapturedEventTypes(capture).ToList();
        allTypes.Should().Contain("TRIAGE.APPLY.RESULT.FAILED",
            "the apply step must still emit its loud leaf FAILED event (#8) on a non-success POST");
        allTypes.Should().NotContain("TRIAGE.APPLY.RESULT.COMPLETED",
            "a failed apply must not emit a false leaf .COMPLETED");
    }

    [Test]
    public async Task ApplyFailedTerminal_OutputsFailedItemResult()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new FaultingApplyClient(statusCode: 500));

        var output = await RunApplyBranchAsync(provider);

        output.Should().ContainKey("itemResult");
        using var doc = JsonDocument.Parse(output["itemResult"]!.ToString()!);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be(TriageCycleEvents.OutcomeFailed,
            "the per-item result on the apply-fault path must report a failed outcome for the parent");
    }

    [Test]
    public async Task ApplySuccess_EmitsExactlyOneCompletedTerminal_AndNoFailed()
    {
        var capture = new CapturingHandler();
        await using var provider = BuildProvider(capture, new AllOkApplyClient());

        await RunApplyBranchAsync(provider);

        var cycleTypes = CapturedEventTypes(capture).Where(t => t!.StartsWith("TRIAGE.ISSUE.")).ToList();

        cycleTypes.Should().ContainSingle("a successful apply must yield exactly one cycle terminal");
        cycleTypes.Single().Should().Be(TriageCycleEvents.Completed);
        cycleTypes.Should().NotContain(TriageCycleEvents.Failed);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the apply-branch flowchart (real apply activity + real cycle-event terminals,
    /// wired exactly as <c>TriageItemCycleWorkflow</c>) and returns the workflow output.
    /// </summary>
    private static async Task<IDictionary<string, object>> RunApplyBranchAsync(IServiceProvider rootProvider)
    {
        using var scope = rootProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var result = await runner.RunAsync(BuildApplyBranch());
        return result.WorkflowState.Output ?? new Dictionary<string, object>();
    }

    /// <summary>
    /// The apply branch of <c>TriageItemCycleWorkflow</c>, isolated: Init (seed item key +
    /// decision fields) → Seed fail-closed itemResult → Apply → { Success → COMPLETED,
    /// Failure → FAILED }. Uses the SAME node types and the SAME Success/Failure outcome
    /// routing as the production workflow.
    /// </summary>
    private static Flowchart BuildApplyBranch()
    {
        var itemKey = new Variable<string>("ItemKey", "owner/repo#7");
        var skipReason = new Variable<string>("SkipReason", "");

        var apply = new ApplyTriageResultActivity
        {
            Id = "ApplyLabels",
            Name = "Apply Labels & Comment",
            Repository = new Input<string>(_ => Repo),
            ItemJson = new Input<string>(_ => ItemJson),
            DecisionJson = new Input<string>(_ => OkDecision),
        };

        var seedFailedResult = new SetOutput
        {
            Id = "SeedFailedResult",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)Tamma.ElsaServer.Workflows.Helpers.TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx), TriageCycleEvents.OutcomeFailed, "ok", "applyIncomplete")),
        };

        var emitCompleted = CycleEvent("EmitCycleCompleted", TriageCycleEvents.Completed, itemKey, _ => "");
        var outCompleted = new SetOutput
        {
            Id = "OutCompletedResult",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)Tamma.ElsaServer.Workflows.Helpers.TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx), TriageCycleEvents.OutcomeTriaged, "ok", null)),
        };

        var setApplyFailedReason = new SetVariable
        {
            Id = "SetApplyFailedReason", Variable = skipReason,
            Value = new Input<object?>(_ => (object)"applyFailed"),
        };
        var emitApplyFailed = CycleEvent("EmitCycleApplyFailed", TriageCycleEvents.Failed,
            itemKey, ctx => skipReason.Get(ctx));
        var outApplyFailed = new SetOutput
        {
            Id = "OutApplyFailedResult",
            OutputName = new("itemResult"),
            OutputValue = new(ctx => (object)Tamma.ElsaServer.Workflows.Helpers.TriageItemCycleHelper.BuildItemResult(
                itemKey.Get(ctx), TriageCycleEvents.OutcomeFailed, "ok", skipReason.Get(ctx))),
        };

        var finish = new Finish { Id = "Finish" };

        return new Flowchart
        {
            Id = "ApplyBranchFlowchart",
            Variables = { itemKey, skipReason },
            Start = seedFailedResult,
            Activities =
            {
                seedFailedResult, apply,
                emitCompleted, outCompleted,
                setApplyFailedReason, emitApplyFailed, outApplyFailed,
                finish,
            },
            Connections =
            {
                Connect(seedFailedResult, apply),
                // Exactly the cycle's apply-outcome routing.
                ConnectOutcome(apply, "Success", emitCompleted),
                Connect(emitCompleted, outCompleted),
                Connect(outCompleted, finish),
                ConnectOutcome(apply, "Failure", setApplyFailedReason),
                Connect(setApplyFailedReason, emitApplyFailed),
                Connect(emitApplyFailed, outApplyFailed),
                Connect(outApplyFailed, finish),
            },
        };
    }

    private static EmitTriageCycleEventActivity CycleEvent(
        string id, string eventType, Variable<string> itemKey,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> reason) => new()
    {
        Id = id,
        EventType = new Input<string>(_ => eventType),
        Repository = new Input<string>(_ => Repo),
        ItemKey = new Input<string?>(ctx => itemKey.Get(ctx)),
        ItemNumber = new Input<int>(_ => 7),
        TenantId = new Input<string?>(_ => ""),
        ItemSource = new Input<string?>(_ => "issue"),
        Type = new Input<string?>(_ => ""),
        Priority = new Input<string?>(_ => ""),
        Automation = new Input<string?>(_ => ""),
        DecisionStatus = new Input<string?>(_ => "ok"),
        Reason = new Input<string?>(ctx => reason(ctx)),
    };

    private static List<string?> CapturedEventTypes(CapturingHandler capture) =>
        capture.Bodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray())
            .Select(e => e.GetProperty("eventType").GetString())
            .ToList();

    private static ServiceProvider BuildProvider(CapturingHandler capture, ITriageApplyClient applyClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElsa(elsa =>
        {
            elsa.AddActivity<ApplyTriageResultActivity>();
            elsa.AddActivity<EmitTriageCycleEventActivity>();
            elsa.UseWorkflows(w => w.UseTammaEventPersistence());
        });

        // Engine:CallbackUrl present so the apply activity takes the real-client path; the
        // injected ITriageApplyClient (the test seam) decides success/failure.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiUrl"] = "http://tamma.test",
                ["Engine:CallbackUrl"] = "http://engine.test",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(applyClient);

        services.AddSingleton(_ => new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(capture) { BaseAddress = null },
            NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            config));

        return services.BuildServiceProvider();
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    // ── Test apply clients ──────────────────────────────────────────────────

    private sealed class FaultingApplyClient(int statusCode) : ITriageApplyClient
    {
        public Task<TriageApplyResult> SetLabelsAsync(string repository, int issueNumber, IReadOnlyList<string> labels, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Fail(statusCode));
        public Task<TriageApplyResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Fail(statusCode));
        public Task<TriageApplyResult> CreateIssueAsync(string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Fail(statusCode));
    }

    private sealed class AllOkApplyClient : ITriageApplyClient
    {
        public Task<TriageApplyResult> SetLabelsAsync(string repository, int issueNumber, IReadOnlyList<string> labels, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Ok());
        public Task<TriageApplyResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Ok());
        public Task<TriageApplyResult> CreateIssueAsync(string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
            => Task.FromResult(TriageApplyResult.Ok());
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
