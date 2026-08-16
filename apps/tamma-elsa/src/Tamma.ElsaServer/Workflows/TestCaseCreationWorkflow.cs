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
/// Story 39-15 — Test Case Creation, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>),
/// producing a typed <see cref="Tamma.Core.Documents.Types.TestSpec"/> that CONSUMES the
/// task-breakdown <see cref="Tamma.Core.Documents.Types.Plan"/> (declared
/// <c>consumes: [plan] / produces: test-spec</c>, D3). The public surface is byte-stable (D1):
/// same <c>DefinitionId = "test-case-creation"</c>, same
/// <c>repository</c>/<c>branchName</c>/<c>tasksJson</c>/<c>contextIds</c> inputs (plus additive
/// <c>issueId?</c> / <c>tenantId?</c> / <c>acceptanceRulesJson?</c>), same <c>testCasesJson</c>/
/// <c>error</c> outputs plus additive <c>status</c>/<c>outcome</c>/<c>documentId</c>.
///
/// <para><b>Cross-document task-ID ring (D3, AC2).</b> A case referencing a task that does not
/// exist in the consumed plan is a VALIDATOR failure flowing through the rings, not a
/// binding-local branch: the binding hands the consumed plan (the runtime <c>tasksJson</c>
/// carrier) to the lifecycle as <c>validationContextJson</c>, which VALIDATE forwards to
/// <c>TestSpecDocumentType.ValidateWithContext</c> → <c>CASE_UNKNOWN_TASK_ID</c>. The
/// <c>ValidationErrors</c>/<c>maxRetries</c>/<c>OutErr</c>/<see cref="Finish"/> plumbing is
/// DELETED; feedback lands in the DECLARED <c>testTarget</c> carrier.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class TestCaseCreationWorkflow : WorkflowBase
{
    private const string TestSpecDocumentType = "test-spec";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Test Case Creation";
        builder.DefinitionId = "test-case-creation";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Generate test cases from the task breakdown via the generic document lifecycle (produce → validate(cross-doc task-ID) → review → revise → accept)";

        // ── Inputs (compat set + additive) ─────────────────────────────
        var repository      = builder.WithVariable<string>("Repository", "").Persisted();
        var branchName      = builder.WithVariable<string>("BranchName", "").Persisted();
        var tasksJson       = builder.WithVariable<string>("TasksJson", "[]").Persisted();
        var contextIds      = builder.WithVariable<string>("ContextIds", "[]").Persisted();
        var tenantId        = builder.WithVariable<string>("TenantId", "").Persisted();
        var issueId         = builder.WithVariable<string>("IssueId", "").Persisted();
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "").Persisted();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>().Persisted();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}").Persisted();

        // ── 39-10 re-entry position (D8) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson  = builder.WithVariable<string>().Persisted();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce").Persisted();

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var lifecycleAccepted = builder.WithVariable<bool>().Persisted();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "").Persisted();
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "").Persisted();
        var testCasesJson   = builder.WithVariable<string>("TestCasesJson", "[]").Persisted();
        var outputStatus    = builder.WithVariable<string>().Persisted();
        var outputError     = builder.WithVariable<string>("OutputError", "").Persisted();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                tasksJson.Set(ctx, ctx.GetInput<string>("tasksJson") ?? "[]");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                issueId.Set(ctx, string.IsNullOrWhiteSpace(explicitIssueId)
                    ? CreationBindingHelper.DeriveIssueId(repo, ctx.GetInput<int>("issueNumber"))
                    : explicitIssueId);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D8) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(TestSpecDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
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

        // ── Story 39-25 (leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this run's anchor ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
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

        // ── Step 3: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"]          = TestSpecDocumentType,
                    ["producerRole"]          = AgentRole.Tester.ToWire(),
                    ["producerAction"]        = AgentAction.WriteTests.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["tasksJson"]  = tasksJson.Get(ctx) ?? "[]",
                        ["contextIds"] = contextIds.Get(ctx) ?? "[]",
                        ["repository"] = repository.Get(ctx) ?? "",
                        ["branchName"] = branchName.Get(ctx) ?? "",
                    }),
                    // 39-6 D11 — repair/revise notes land in the DECLARED testTarget carrier
                    // (write-tests.md declares {{testTarget}}).
                    ["feedbackVariableName"] = "testTarget",
                    // D3 — the consumed task breakdown is the cross-document validation context; VALIDATE
                    // forwards it to TestSpecDocumentType.ValidateWithContext (task-ID binding ring).
                    ["validationContextJson"] = CreationBindingHelper.BuildTaskIdContext(tasksJson.Get(ctx)),
                    ["issueId"]             = issueId.Get(ctx) ?? "",
                    ["correlationId"]       = issueId.Get(ctx) ?? "",
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

        // ── Step 4: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = testCasesJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                outputError.Set(ctx, accepted ? "" : CreationBindingHelper.BuildFailureDetail(exit));
                return accepted
                    ? CreationBindingHelper.ProjectTestCasesArray(exit.DocumentJson)
                    : "[]";
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 5: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputTestCases", Name = "Output Test Cases", OutputName = new("testCasesJson"), OutputValue = new(ctx => (object)(testCasesJson.Get(ctx) ?? "[]")) }, "Output Test Cases"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(outputError.Get(ctx) ?? "")) }, "Output Error"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "TestCaseCreationFlowchart",
            Name = "Test Case Creation Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage,
                fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit, exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch.
                new(readPositionStage, fetchAmbiguityAssessment),
                new(fetchAmbiguityAssessment, dispatchLifecycle),
                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, exposeOutput),
            }
        };
    }
}
