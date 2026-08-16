using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Create Issues — Story 40-8. Creates one platform issue per draft in a JSON array
/// through the mediated engine route (<c>POST /api/engine/create-issue</c> via
/// <see cref="CreateIssuesActivity"/>).
///
/// <para>This is the workflow the two previously-DEAD dispatch sites already target:
/// <c>SingleIssueCycleWorkflow</c>'s Defer (<c>CreateDeferredIssues</c>) and Split
/// (<c>CreateSplitIssues</c>) triage outcomes dispatch
/// <c>WorkflowDefinitionId = "create-issues"</c> with <c>WaitForCompletion = true</c>;
/// until this workflow existed both branches suspended FOREVER on a completion that
/// could never arrive
/// (<c>.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md</c>).
/// The id matches the existing call sites, so the cycle needs no wiring edit;
/// registration is automatic via the Program.cs assembly scan.</para>
///
/// <para><b>Always completes</b> (AC1): the parent has no failure edge from its
/// dispatch and ignores this child's outputs, so BOTH activity outcomes route to the
/// output surface → <c>Finish</c>. Empty/malformed <c>issuesJson</c> completes with 0
/// created and a recorded warning — never a fault, never a hang.</para>
///
/// <para><b>Resumable by design</b> (AC3, 40-8 D7): a crash/re-run never
/// double-creates — <see cref="CreateIssuesActivity"/> dedupes against the PLATFORM
/// (lists the repo's issues and skips exact-title matches), so the durable record of
/// what was created survives instance loss. The <c>[ResumeBehavior]</c> declaration
/// itself is deferred: clause (c) of the shipped resumable-standard gate requires the
/// document-coupled <c>ComputeReEntryPositionActivity</c> for any
/// <c>LatestStateReEntry</c> declaration, which this non-document side-effect leaf
/// cannot honestly wire until 40-4's <c>CanonicalReEntryActivities</c> registry seam
/// lands — so it sits on <c>LegacyResumeAllowlist</c> with that burn-down recorded.</para>
///
/// Flow:
///   ReadInputs → CreateIssues
///     ├─ Success → Success Outputs → Finish
///     └─ Failure → Failure Outputs (success=false) → Finish
///
/// Inputs: repository, issuesJson, tenantId? (threaded into the <c>TenantId</c>
/// variable so the DCB drain tenant-tags the <c>ISSUES.CREATE*</c> events — the
/// <c>EventPersistenceMiddleware.ResolveTenantId</c> contract).
/// Outputs: success, createdCount, failedCount, skippedCount, issueNumbersJson.
/// </summary>
public class CreateIssuesWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Create Issues";
        builder.DefinitionId = "create-issues";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Create one platform issue per draft in a JSON array via the mediated engine route (defer/split triage outcomes)";

        // ================================================================
        // Variables
        // ================================================================
        var repositoryVar = builder.WithVariable<string>("Repository", "").Persisted();
        var issuesJsonVar = builder.WithVariable<string>("IssuesJson", "[]").Persisted();
        // MUST be literally "TenantId" — EventPersistenceMiddleware.ResolveTenantId
        // reads this exact variable name to tenant-tag the drained events (AC5).
        var tenantIdVar = builder.WithVariable<string>("TenantId", "").Persisted();

        var createdCountVar = builder.WithVariable<int>("CreatedCount", 0).Persisted();
        var failedCountVar = builder.WithVariable<int>("FailedCount", 0).Persisted();
        var skippedCountVar = builder.WithVariable<int>("SkippedCount", 0).Persisted();
        var issueNumbersJsonVar = builder.WithVariable<string>("IssueNumbersJson", "[]").Persisted();

        // ================================================================
        // 1. Read inputs
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repositoryVar,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issuesJsonVar.Set(ctx, ctx.GetInput<string>("issuesJson") ?? "[]");
                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Create the issues (outcome-bearing — Success / Failure; never faults)
        // ================================================================
        var createIssues = new CreateIssuesActivity
        {
            Id = "CreateIssues", Name = "Create Issues",
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            IssuesJson = new Input<string>(ctx => issuesJsonVar.Get(ctx)),
            CreatedCount = new Output<int>(createdCountVar),
            FailedCount = new Output<int>(failedCountVar),
            SkippedCount = new Output<int>(skippedCountVar),
            IssueNumbersJson = new Output<string>(issueNumbersJsonVar),
        };
        createIssues.SetDisplayText("Create Issues");

        // ================================================================
        // 3. Output surfaces — BOTH outcomes reach Finish (the parent ignores
        //    the result; it must always resume — AC1's no-hang half)
        // ================================================================
        var successOutputs = new Sequence
        {
            Id = "SuccessOutputs", Name = "Success Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutCreatedCount", OutputName = new("createdCount"), OutputValue = new(ctx => (object)createdCountVar.Get(ctx)) }, "Output createdCount"),
                WithLabel(new SetOutput { Id = "OutFailedCount", OutputName = new("failedCount"), OutputValue = new(ctx => (object)failedCountVar.Get(ctx)) }, "Output failedCount"),
                WithLabel(new SetOutput { Id = "OutSkippedCount", OutputName = new("skippedCount"), OutputValue = new(ctx => (object)skippedCountVar.Get(ctx)) }, "Output skippedCount"),
                WithLabel(new SetOutput { Id = "OutIssueNumbers", OutputName = new("issueNumbersJson"), OutputValue = new(ctx => (object)issueNumbersJsonVar.Get(ctx)) }, "Output issueNumbersJson"),
            }
        };
        successOutputs.SetDisplayText("Success Outputs");

        var failureOutputs = new Sequence
        {
            Id = "FailureOutputs", Name = "Failure Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutFailSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutFailCreatedCount", OutputName = new("createdCount"), OutputValue = new(ctx => (object)createdCountVar.Get(ctx)) }, "Output createdCount"),
                WithLabel(new SetOutput { Id = "OutFailFailedCount", OutputName = new("failedCount"), OutputValue = new(ctx => (object)failedCountVar.Get(ctx)) }, "Output failedCount"),
                WithLabel(new SetOutput { Id = "OutFailSkippedCount", OutputName = new("skippedCount"), OutputValue = new(ctx => (object)skippedCountVar.Get(ctx)) }, "Output skippedCount"),
                WithLabel(new SetOutput { Id = "OutFailIssueNumbers", OutputName = new("issueNumbersJson"), OutputValue = new(ctx => (object)issueNumbersJsonVar.Get(ctx)) }, "Output issueNumbersJson"),
            }
        };
        failureOutputs.SetDisplayText("Failure Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "CreateIssuesFlowchart",
            Name = "Create Issues Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, createIssues,
                successOutputs, failureOutputs, finish,
            },
            Connections =
            {
                Connect(readInputs, createIssues),

                // Success → success outputs → Finish
                ConnectOutcome(createIssues, "Success", successOutputs),
                Connect(successOutputs, finish),

                // Failure (some items failed — already loudly evented per item)
                // → failure outputs → Finish. NO dead end, NO fault.
                ConnectOutcome(createIssues, "Failure", failureOutputs),
                Connect(failureOutputs, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
