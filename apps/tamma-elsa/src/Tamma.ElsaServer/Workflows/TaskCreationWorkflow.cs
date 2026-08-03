using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-15 — Task Creation, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>),
/// producing a typed task-breakdown <see cref="Tamma.Core.Documents.Types.Plan"/> (39-4's
/// mapping of <c>create-tasks</c> → documentType <c>plan</c>, D2). The public surface is
/// byte-stable (D1): same <c>DefinitionId = "task-creation"</c>, same
/// <c>repository</c>/<c>issueNumber</c>/<c>planJson</c>/<c>contextIds</c>/<c>workItemJson</c>/
/// <c>tenantId</c> inputs (plus additive <c>issueId?</c> / <c>acceptanceRulesJson?</c>), same
/// <c>tasksJson</c>/<c>error</c> outputs plus additive <c>status</c>/<c>outcome</c>/
/// <c>documentId</c>/<c>parentDocumentId</c>. The SingleIssueCycle dispatch site (by definition
/// id) and its bare-array tasks-gate are untouched — <c>tasksJson</c> is the accepted Plan's
/// <c>tasks</c> array raw text (<c>"[]"</c> on non-accept).
///
/// <para><b>What changed (the epic's charter).</b> The bespoke validate-retry loop — the
/// <c>ValidationErrors</c> variable, the <c>maxRetries</c> counter, the inline JSON extract, the
/// <c>OutErr</c> terminal, every <see cref="Finish"/> — is DELETED. Validation failure now flows
/// through the generic validate → repair/revise → review → accept rings and, at worst, exits as a
/// typed escalation with full lineage. Consumed content (the architect's <c>planJson</c>) is
/// folded into the DECLARED <c>contextFindings</c> carrier — never a new undeclared key (the
/// render-drop lesson) — and repair/revise notes land in that SAME carrier via
/// <c>feedbackVariableName = "contextFindings"</c>.</para>
///
/// <para><b>Two-plans disambiguation (D2).</b> Both <c>plan-generation</c> and this binding
/// produce documentType <c>plan</c> per issue; the 39-11 read scopes by
/// <c>(issueId, documentType)</c> with NO producer filter (FILED to 39-11), so the task-creation
/// lifecycle is keyed on a producer-scoped issue id (<c>{issueId}#task-creation</c>) — isolating
/// its re-entry / accepted-doc slice from the accepted system plan WITHOUT forking the type. The
/// <c>planJson</c> input remains the runtime carrier for the consumed plan content.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class TaskCreationWorkflow : WorkflowBase
{
    private const string PlanDocumentType = "plan";
    private const string ProducerScope = "task-creation";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Task Creation";
        builder.DefinitionId = "task-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Break the approved plan into detailed implementation tasks via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Inputs (compat set + additive) ─────────────────────────────
        var repository      = builder.WithVariable<string>("Repository", "");
        var issueNumber     = builder.WithVariable<int>("IssueNumber", 0);
        var planJson        = builder.WithVariable<string>("PlanJson", "");
        var contextIds      = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson    = builder.WithVariable<string>("WorkItemJson", "");
        var tenantId        = builder.WithVariable<string>("TenantId", "");
        var issueId         = builder.WithVariable<string>("IssueId", "");
        var scopedIssueId   = builder.WithVariable<string>("ScopedIssueId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Consumed system plan (D2 lineage anchor) ───────────────────
        var consumedPlanFound = builder.WithVariable<bool>();
        var consumedPlanDocId = builder.WithVariable<string>("ConsumedPlanDocId", "");
        var consumedPlanJson  = builder.WithVariable<string>("ConsumedPlanJson", "");
        var consumedPlanLineage = builder.WithVariable<string>();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position (D8) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "");
        var tasksJson       = builder.WithVariable<string>("TasksJson", "[]");
        var outputStatus    = builder.WithVariable<string>();
        var outputError     = builder.WithVariable<string>("OutputError", "");

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                planJson.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                var baseIssueId = string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId;
                issueId.Set(ctx, baseIssueId);
                // D2 — producer-scoped resume anchor so re-entry does not collide with the system plan.
                scopedIssueId.Set(ctx, CreationBindingHelper.ScopeIssueId(baseIssueId, ProducerScope));
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D8) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => scopedIssueId.Get(ctx)),
            DocumentType = new(PlanDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => scopedIssueId.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        var readPositionStage = new SetVariable
        {
            Id = "ReadPositionStage", Name = "Read Position Stage",
            Variable = positionStage,
            Value = new(ctx =>
            {
                var position = DocumentLifecycleHelper.DeserializeReEntryPosition(reEntryPositionJson.Get(ctx));
                return position?.ResumeAt switch
                {
                    LifecycleResumeStage.Complete => "complete",
                    LifecycleResumeStage.Accept => "accept",
                    LifecycleResumeStage.Review => "review",
                    _ => "produce",
                };
            })
        };
        readPositionStage.SetDisplayText("Read Position Stage");

        // ── Step 3: FreshRun gate — fetch the consumed system plan only on a fresh run (D2/D8) ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        // The consumed plan is read on the BASE issue id (the system plan's scope).
        var fetchConsumedPlan = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchConsumedPlan", Name = "Fetch Accepted System Plan",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(PlanDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(consumedPlanFound),
            DocumentId = new(consumedPlanDocId),
            DocumentJson = new(consumedPlanJson),
            LineageJson = new(consumedPlanLineage),
        };
        fetchConsumedPlan.SetDisplayText("Fetch Accepted System Plan");

        // ── Step 3b (39-25 leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Read on the BASE issue id (the run's identity — the anchor the assessment is
        // persisted under), NOT the producer-scoped id the plan lifecycle keys on.
        // Fail-closed: no accepted assessment ⇒ the ambiguityScore key is OMITTED.
        var fetchAmbiguityAssessment = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchAmbiguityAssessment", Name = "Fetch Accepted Ambiguity Assessment",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentTypeKey = new(AmbiguityAssessmentDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(assessmentFound),
            DocumentJson = new(assessmentJson),
        };
        fetchAmbiguityAssessment.SetDisplayText("Fetch Accepted Ambiguity Assessment");

        // ── Step 4: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"]          = PlanDocumentType,
                    ["producerRole"]          = AgentRole.SeniorDeveloper.ToWire(),
                    ["producerAction"]        = AgentAction.CreateTasks.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["workItemJson"] = workItemJson.Get(ctx) ?? "",
                        // D2 — the consumed plan is the runtime carrier; fold it into the DECLARED
                        // contextFindings variable (create-tasks.md declares {{contextFindings}}),
                        // NOT a new (render-dropped) key.
                        ["contextFindings"] = planJson.Get(ctx) ?? "",
                        ["planJson"]     = planJson.Get(ctx) ?? "",
                        ["contextIds"]   = contextIds.Get(ctx) ?? "[]",
                        ["repository"]   = repository.Get(ctx) ?? "",
                    }),
                    // 39-6 D11 — repair/revise notes land in the DECLARED carrier.
                    ["feedbackVariableName"] = "contextFindings",
                    // D2 — producer-scoped issue id isolates this lifecycle's slice from the system plan.
                    ["issueId"]             = scopedIssueId.Get(ctx) ?? "",
                    ["correlationId"]       = scopedIssueId.Get(ctx) ?? "",
                    ["tenantId"]            = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
                };
                // 39-25 — thread the accepted assessment's score; ABSENT when none (null stays null).
                if (LifecycleBindingHelper.TryReadAssessmentScore(
                        assessmentFound.Get(ctx), assessmentJson.Get(ctx)) is double ambiguityScore)
                    input["ambiguityScore"] = ambiguityScore;
                return input;
            }),
            WaitForCompletion = new(true),
            Result = new(lifecycleResult),
        };
        dispatchLifecycle.SetDisplayText("Dispatch Document Lifecycle");

        // ── Step 5: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = tasksJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                outputError.Set(ctx, accepted ? "" : CreationBindingHelper.BuildFailureDetail(exit));
                // tasksJson = the accepted Plan's tasks array raw text; "[]" on non-accept so the
                // parent's tasks-gate + empty-tasks failure edge fire unchanged (D2).
                return accepted
                    ? CreationBindingHelper.ProjectTasksArray(exit.DocumentJson)
                    : "[]";
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 6: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputTasks", Name = "Output Tasks", OutputName = new("tasksJson"), OutputValue = new(ctx => (object)(tasksJson.Get(ctx) ?? "[]")) }, "Output Tasks"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(outputError.Get(ctx) ?? "")) }, "Output Error"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
                WithLabel(new SetOutput { Id = "OutputParentDocumentId", Name = "Output Parent Document Id", OutputName = new("parentDocumentId"), OutputValue = new(ctx => (object)(consumedPlanDocId.Get(ctx) ?? "")) }, "Output Parent Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "TaskCreationFlowchart",
            Name = "Task Creation Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                fetchConsumedPlan, fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit, exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(fetchConsumedPlan)),
                // 39-25 — the ambiguity fetch is the SINGLE predecessor of the dispatch,
                // so it runs on every path that actually dispatches (fresh + re-entry).
                new(fetchConsumedPlan, fetchAmbiguityAssessment),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, exposeOutput),
            }
        };
    }
}
