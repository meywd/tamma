using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-14 (Design Decision D1/D2) — Plan Review, reduced to a deterministic READ-THROUGH
/// SHIM over the document store. Plan review no longer exists as an independent produce-verdict
/// pipeline: it runs INSIDE the Plan lifecycle's review stage (39-7 panel producers, driven by
/// <see cref="PlanGenerationWorkflow"/>), emitting typed <see cref="Tamma.Core.Documents.Types.Review"/>
/// documents to the store. This shim keeps the <c>DefinitionId = "plan-review"</c> call site
/// (SingleIssueCycle) live and maps the store's latest accepted <c>plan</c> + its round lineage
/// onto the legacy output shape.
///
/// <para><b>Zero LLM, zero dispatch.</b> The 3-phase debate — 7 role reviews, 7 rebuttals, the
/// PO-decision phase, the anonymization, the <c>concerns</c>-laundering verdict parse — is DELETED.
/// The shim has NO <see cref="DispatchWorkflow"/> node and NO <see cref="Finish"/>: it is a pure
/// store read, so a re-run is an idempotent read (declared <c>[ResumeBehavior(LatestStateReEntry)]</c>).</para>
///
/// <para><b>Legacy output mapping (D1).</b> <c>decision</c>: an accepted plan present →
/// <c>"approved"</c>; none → <c>"needsHuman"</c> (the review already happened inside the lifecycle,
/// so <c>needsModification</c> is unreachable here). <c>planJson</c>: the accepted body, else the
/// input passthrough. <c>deferred</c>/<c>split</c>: always <c>"[]"</c> — defer/split retire from
/// the review surface (D2; scope routing is the orchestrator's job). <c>discussionLog</c>: a round
/// projection from the plan's lineage. <c>suggestionsJson</c>: <c>"[]"</c>.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class PlanReviewWorkflow : WorkflowBase
{
    private const string PlanDocumentType = "plan";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Review";
        builder.DefinitionId = "plan-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Read-through shim over the document store: maps the latest accepted plan + review lineage to the legacy review output (39-14 D1)";

        // ── Inputs (compat set) ────────────────────────────────────────
        var repository   = builder.WithVariable<string>("Repository", "");
        var issueNumber  = builder.WithVariable<int>("IssueNumber", 0);
        var planJsonIn   = builder.WithVariable<string>("PlanJson", "");
        var contextIds   = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var tenantId     = builder.WithVariable<string>("TenantId", "");
        var issueId      = builder.WithVariable<string>("IssueId", "");

        // ── 39-10 re-entry position (trivial pure read) ────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();

        // ── Store read result ──────────────────────────────────────────
        var planFound    = builder.WithVariable<bool>();
        var acceptedDocId = builder.WithVariable<string>();
        var acceptedPlanJson = builder.WithVariable<string>("AcceptedPlanJson", "{}");
        var lineageJson  = builder.WithVariable<string>("LineageJson", "{}");

        // ── Mapped legacy outputs ──────────────────────────────────────
        var decision     = builder.WithVariable<string>("Decision", "needsHuman");
        var planJsonOut  = builder.WithVariable<string>("PlanJsonOut", "");
        var reviewNotes  = builder.WithVariable<string>("ReviewNotes", "");
        var discussionLog = builder.WithVariable<string>("DiscussionLog", "[]");

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                planJsonIn.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                issueId.Set(ctx, string.IsNullOrWhiteSpace(explicitIssueId)
                    ? PlanBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (clause c — a harmless idempotent read) ──
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(PlanDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        // ── Step 3: Fetch the latest accepted plan + lineage ───────────
        var fetchAcceptedPlan = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchLatestAcceptedPlan", Name = "Fetch Latest Accepted Plan",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(PlanDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(planFound),
            DocumentId = new(acceptedDocId),
            DocumentJson = new(acceptedPlanJson),
            LineageJson = new(lineageJson),
        };
        fetchAcceptedPlan.SetDisplayText("Fetch Latest Accepted Plan");

        // ── Step 4: Map to legacy outputs ──────────────────────────────
        var mapOutputs = new SetVariable
        {
            Id = "MapToLegacyOutputs", Name = "Map To Legacy Outputs",
            Variable = decision,
            Value = new(ctx =>
            {
                var found = planFound.Get(ctx);
                planJsonOut.Set(ctx, found ? acceptedPlanJson.Get(ctx) : planJsonIn.Get(ctx));
                discussionLog.Set(ctx, PlanBindingHelper.BuildDiscussionLogProjection(lineageJson.Get(ctx)));
                reviewNotes.Set(ctx, found
                    ? "Plan accepted through the document lifecycle (unified review)."
                    : "No accepted plan for this issue — escalating to human.");
                return (object)PlanBindingHelper.MapDecisionForLegacyOutput(found);
            })
        };
        mapOutputs.SetDisplayText("Map To Legacy Outputs");

        // ── Step 5: Expose the legacy output shape ─────────────────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutDecision", Name = "Output Decision", OutputName = new("decision"), OutputValue = new(ctx => (object)(decision.Get(ctx) ?? "needsHuman")) }, "Output Decision"),
                WithLabel(new SetOutput { Id = "OutPlanJson", Name = "Output Plan Json", OutputName = new("planJson"), OutputValue = new(ctx => (object)(planJsonOut.Get(ctx) ?? "")) }, "Output Plan Json"),
                WithLabel(new SetOutput { Id = "OutReviewNotes", Name = "Output Review Notes", OutputName = new("reviewNotes"), OutputValue = new(ctx => (object)(reviewNotes.Get(ctx) ?? "")) }, "Output Review Notes"),
                WithLabel(new SetOutput { Id = "OutDeferred", Name = "Output Deferred", OutputName = new("deferred"), OutputValue = new(_ => (object)"[]") }, "Output Deferred"),
                WithLabel(new SetOutput { Id = "OutSplit", Name = "Output Split", OutputName = new("split"), OutputValue = new(_ => (object)"[]") }, "Output Split"),
                WithLabel(new SetOutput { Id = "OutDiscussionLog", Name = "Output Discussion Log", OutputName = new("discussionLog"), OutputValue = new(ctx => (object)(discussionLog.Get(ctx) ?? "[]")) }, "Output Discussion Log"),
                WithLabel(new SetOutput { Id = "OutSuggestionsJson", Name = "Output Suggestions Json", OutputName = new("suggestionsJson"), OutputValue = new(_ => (object)"[]") }, "Output Suggestions Json"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "PlanReviewFlowchart",
            Name = "Plan Review Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, fetchAcceptedPlan, mapOutputs, exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, fetchAcceptedPlan),
                new(fetchAcceptedPlan, mapOutputs),
                new(mapOutputs, exposeOutput),
            }
        };
    }
}
