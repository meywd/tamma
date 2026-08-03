using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text;
using System.Text.Json;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-15 (D4) — Debug diagnosis, implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>),
/// producing a typed <see cref="Tamma.Core.Documents.Types.Diagnosis"/> through the shared
/// produce → validate → review → revise → accept loop. Replaces the retired
/// <c>AIDiagnosisActivity</c>'s hand-built prompt + direct mediated call: production is now
/// on the registry cell <c>(senior_developer, debug-rootcause)</c>, restoring the
/// llm-call-mediation invariant.
///
/// <para><b>Consumed by <c>DebuggingWorkflow</c>'s untouched fix/retry loop.</b> The binding
/// surfaces the accepted diagnosis both as its typed store id (<c>diagnosisDocumentId</c>) and,
/// via <see cref="DiagnosisBindingHelper.ToLegacyHypothesesJson"/>, as the bare
/// <c>hypothesesJson</c> the loop's <c>SelectHypothesisActivity</c> slices — so the loop is
/// byte-stable (AC4).</para>
///
/// <para><b>Resumable per the standard (D8).</b> Declared <c>[ResumeBehavior(LatestStateReEntry)]</c>
/// with the generic <see cref="ComputeReEntryPositionActivity"/> gate — the accept-gate suspend
/// happens inside the dispatched child lifecycle, which the parent awaits via
/// <c>WaitForCompletion</c>.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class DebugDiagnosisWorkflow : WorkflowBase
{
    public const string DebugDiagnosisDefinitionId = "debug-diagnosis";
    private const string DiagnosisDocumentType = "diagnosis";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Debug Diagnosis";
        builder.DefinitionId = DebugDiagnosisDefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Produce a typed root-cause Diagnosis via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Inputs (debug context carried into the producer cell) ──────
        var sessionId       = builder.WithVariable<string>("SessionId", "");
        var issueId         = builder.WithVariable<string>("IssueId", "");
        var mode            = builder.WithVariable<string>("Mode", "RuntimeError");
        var errorContext    = builder.WithVariable<string>("ErrorContext", "");
        var codeContext     = builder.WithVariable<string>("CodeContext", "");
        var gitContext      = builder.WithVariable<string>("GitContext", "");
        var testContext     = builder.WithVariable<string>("TestContext", "");
        var reproContext    = builder.WithVariable<string>("ReproductionContext", "");
        var previousContext = builder.WithVariable<string>("PreviousContext", "");
        var supersedesDocId = builder.WithVariable<string>("SupersedesDocumentId", "");
        var tenantId        = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position (D8) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "");
        var hypothesesJson  = builder.WithVariable<string>("HypothesesJson", "[]");
        var failureReason   = builder.WithVariable<string>("FailureReason", "");
        var outputStatus    = builder.WithVariable<string>("OutputStatus", "");

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = issueId,
            Value = new(ctx =>
            {
                sessionId.Set(ctx, ctx.GetInput<string>("sessionId") ?? "");
                mode.Set(ctx, ctx.GetInput<string>("mode") ?? "RuntimeError");
                errorContext.Set(ctx, ctx.GetInput<string>("errorContext") ?? "");
                codeContext.Set(ctx, ctx.GetInput<string>("codeContext") ?? "");
                gitContext.Set(ctx, ctx.GetInput<string>("gitContext") ?? "");
                testContext.Set(ctx, ctx.GetInput<string>("testContext") ?? "");
                reproContext.Set(ctx, ctx.GetInput<string>("reproductionContext") ?? "");
                previousContext.Set(ctx, ctx.GetInput<string>("previousContext") ?? "");
                supersedesDocId.Set(ctx, ctx.GetInput<string>("supersedesDocumentId") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                var explicitIssueId = ctx.GetInput<string>("issueId") ?? "";
                return (object)(string.IsNullOrWhiteSpace(explicitIssueId)
                    ? DeriveIssueId(ctx.GetInput<string>("sessionId"), ctx.GetInput<string>("storyId"))
                    : explicitIssueId);
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D8) ───────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(DiagnosisDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => issueId.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

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
                    ["documentType"]          = DiagnosisDocumentType,
                    ["producerRole"]          = AgentRole.SeniorDeveloper.ToWire(),
                    ["producerAction"]        = AgentAction.DebugRootcause.ToWire(),
                    // Fold the debug context into the debug-rootcause cell's DECLARED variables
                    // (errorContext / stackTrace / relevantCode / recentChanges / conventions) —
                    // an undeclared key is silently dropped at render (the render-drop lesson).
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["errorContext"]  = errorContext.Get(ctx) ?? "",
                        ["stackTrace"]    = FoldStackTrace(testContext.Get(ctx), reproContext.Get(ctx)),
                        ["relevantCode"]  = codeContext.Get(ctx) ?? "",
                        ["recentChanges"] = FoldRecentChanges(gitContext.Get(ctx), previousContext.Get(ctx), supersedesDocId.Get(ctx)),
                        ["conventions"]   = "",
                    }),
                    // Repair/revise notes land in the DECLARED errorContext carrier (39-6 D11).
                    ["feedbackVariableName"] = "errorContext",
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
            Variable = hypothesesJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);
                var legacy = accepted ? DiagnosisBindingHelper.ToLegacyHypothesesJson(exit.DocumentJson) : "[]";

                lifecycleAccepted.Set(ctx, accepted && DiagnosisBindingHelper.HasUsableHypotheses(legacy));
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                failureReason.Set(ctx, accepted ? "" : DiagnosisBindingHelper.BuildFailureReason(exit));
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                return (object)legacy;
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 5: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputHypotheses", Name = "Output Hypotheses", OutputName = new("hypothesesJson"), OutputValue = new(ctx => (object)(hypothesesJson.Get(ctx) ?? "[]")) }, "Output Hypotheses"),
                WithLabel(new SetOutput { Id = "OutputAccepted", Name = "Output Accepted", OutputName = new("accepted"), OutputValue = new(ctx => (object)lifecycleAccepted.Get(ctx)) }, "Output Accepted"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDiagnosisDocumentId", Name = "Output Diagnosis Document Id", OutputName = new("diagnosisDocumentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Diagnosis Document Id"),
                WithLabel(new SetOutput { Id = "OutputFailureReason", Name = "Output Failure Reason", OutputName = new("failureReason"), OutputValue = new(ctx => (object)(failureReason.Get(ctx) ?? "")) }, "Output Failure Reason"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "DebugDiagnosisFlowchart",
            Name = "Debug Diagnosis Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, fetchAmbiguityAssessment, dispatchLifecycle, readLifecycleExit, exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                // 39-25 — the ambiguity fetch is the single predecessor of the dispatch.
                new(computeReEntry, fetchAmbiguityAssessment),
                new(fetchAmbiguityAssessment, dispatchLifecycle),
                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, exposeOutput),
            }
        };
    }

    /// <summary>Issue-identity anchor for the diagnosis lifecycle: explicit issue id, else
    /// a stable <c>debug#{session}</c> / <c>debug#{story}</c> derivation.</summary>
    internal static string DeriveIssueId(string? sessionId, string? storyId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId)) return $"debug#{sessionId}";
        if (!string.IsNullOrWhiteSpace(storyId)) return $"debug#{storyId}";
        return "debug#unknown";
    }

    /// <summary>Fold the test-results + reproduction context into the declared
    /// <c>stackTrace</c> carrier (no undeclared key is rendered).</summary>
    internal static string FoldStackTrace(string? testContext, string? reproContext)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(testContext)) sb.Append(testContext);
        if (!string.IsNullOrWhiteSpace(reproContext))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine("## Reproduction");
            sb.Append(reproContext);
        }
        return sb.ToString();
    }

    /// <summary>Fold the git history + previous failed attempts (+ any superseded-diagnosis
    /// pointer) into the declared <c>recentChanges</c> carrier.</summary>
    internal static string FoldRecentChanges(string? gitContext, string? previousContext, string? supersedesDocId)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(gitContext)) sb.Append(gitContext);
        if (!string.IsNullOrWhiteSpace(previousContext))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine("## Previous Failed Attempts (do NOT repeat)");
            sb.Append(previousContext);
        }
        if (!string.IsNullOrWhiteSpace(supersedesDocId))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("## Supersedes prior diagnosis ").Append(supersedesDocId);
        }
        return sb.ToString();
    }
}
