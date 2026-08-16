using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Clarify;
using Tamma.Activities.Clarify.Models;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-13 — Clarifying Questions, re-implemented as a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> that runs the lifecycle TWICE (D2), producing one
/// <see cref="Tamma.Core.Documents.Types.Clarification"/> document across its two phases:
/// Run A produces the <c>questions</c> phase via <c>(product_owner, clarify-requirements)</c>;
/// the binding delivers the accepted questions and SUSPENDS on the generic
/// <see cref="WaitForDocumentInputActivity"/> input gate (D3); on resume Run B produces the
/// <c>resolution</c> phase via <c>(product_owner, incorporate-answers)</c>.
///
/// <para>The legacy bespoke pipeline (<c>llm-call</c> → <c>ClarifyParsing</c> →
/// <c>WaitForClarifyingAnswersActivity</c> → <c>LlmCallError</c> Finish) is DELETED: NO parse,
/// NO success-flag gate, ZERO <see cref="Finish"/>. The wait-for-answers ride the generic
/// input gate; <c>ClarifyResumeEndpoint</c> is preserved as a thin adapter onto the generic
/// input-resume surface. The public surface is byte-stable (D1): same
/// <c>DefinitionId = "clarifying-questions"</c>, same outputs (<c>sessionId</c>/<c>status</c>/
/// <c>clarifiedRequirement</c>/<c>resolved</c>) plus additive <c>outcome</c>/<c>documentId</c>.</para>
///
/// <para>Declared <c>[ResumeBehavior(Both)]</c> — it owns the input-gate bookmark AND
/// re-enters from the latest accepted clarification state after a crash (D10).</para>
/// </summary>
[ResumeBehavior(ResumeMode.Both, SuspendActivities = new[] { typeof(WaitForDocumentInputActivity) })]
public class ClarifyingQuestionsWorkflow : WorkflowBase
{
    private const string ClarificationDocumentType = "clarification";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ClarifyingQuestions";
        builder.DefinitionId = "clarifying-questions";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Resolve requirement ambiguity via two clarification-lifecycle runs (questions → suspend → resolution) over the generic document lifecycle";

        // ── Inputs ─────────────────────────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>().Persisted();
        var issueId         = builder.WithVariable<string>().Persisted();
        var requirement     = builder.WithVariable<string>().Persisted();
        var repository      = builder.WithVariable<string>().Persisted();
        var issueNumber     = builder.WithVariable<int>().Persisted();
        var ambiguityContext = builder.WithVariable<string>().Persisted();
        var tenantId        = builder.WithVariable<string>("TenantId", "").Persisted();
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "").Persisted();

        // ── 39-10 re-entry position ────────────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>().Persisted();
        var reEntryDocJson  = builder.WithVariable<string>().Persisted();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce").Persisted();
        var existingResolved = builder.WithVariable<bool>().Persisted();

        // ── Run A / Run B state ────────────────────────────────────────
        var runAResult      = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var runBResult      = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var runAAccepted    = builder.WithVariable<bool>().Persisted();
        var runBAccepted    = builder.WithVariable<bool>().Persisted();
        var questionsJson   = builder.WithVariable<string>("QuestionsJson", "{}").Persisted();
        var questionCount   = builder.WithVariable<int>().Persisted();
        var clarifiedJson   = builder.WithVariable<string>("ClarifiedJson", "{}").Persisted();
        var resolved        = builder.WithVariable<bool>().Persisted();
        var exitOutcome     = builder.WithVariable<string>("ExitOutcome", "").Persisted();
        var exitDocId       = builder.WithVariable<string>("ExitDocId", "").Persisted();
        var failureDetail   = builder.WithVariable<string>("FailureDetail", "").Persisted();

        // ── Input-gate outputs ─────────────────────────────────────────
        var deliveryResult  = builder.WithVariable<ClarifyDeliveryResult>().Persisted();
        var inputReceived   = builder.WithVariable<bool>().Persisted();
        var inputTimedOut   = builder.WithVariable<bool>().Persisted();
        var answers         = builder.WithVariable<string>("Answers", "").Persisted();

        // ── Outputs ────────────────────────────────────────────────────
        var outputStatus        = builder.WithVariable<string>().Persisted();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = sessionId,
            Value = new(context =>
            {
                var sid = context.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();

                issueId.Set(context, context.GetInput<string>("issueId") ?? string.Empty);
                requirement.Set(context, context.GetInput<string>("requirement") ?? string.Empty);
                repository.Set(context, context.GetInput<string>("repository") ?? string.Empty);
                issueNumber.Set(context, context.GetInput<int>("issueNumber"));
                ambiguityContext.Set(context, context.GetInput<string>("ambiguityContext") ?? string.Empty);
                tenantId.Set(context, context.GetInput<string>("tenantId") ?? string.Empty);
                acceptanceRulesJson.Set(context, context.GetInput<string>("acceptanceRulesJson") ?? string.Empty);
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position ────────────────────
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => issueId.Get(ctx)),
            DocumentType = new(ClarificationDocumentType),
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
                var stage = position?.ResumeAt switch
                {
                    LifecycleResumeStage.Complete => "complete",
                    LifecycleResumeStage.Accept => "accept",
                    LifecycleResumeStage.Review => "review",
                    _ => "produce",
                };
                // When a clarification document is already accepted, distinguish a completed
                // RESOLUTION (short-circuit) from an accepted QUESTIONS phase (between runs —
                // prime the questions and re-arm the input gate without re-delivering).
                var existing = reEntryDocJson.Get(ctx);
                var (qCount, res) = AssessmentBindingHelper.ReadClarification(existing);
                existingResolved.Set(ctx, res);
                if (stage == "complete" && !res)
                {
                    // Accepted questions phase — prime for the wait step.
                    questionsJson.Set(ctx, string.IsNullOrWhiteSpace(existing) ? "{}" : existing);
                    questionCount.Set(ctx, qCount);
                }
                if (stage == "complete" && res)
                    clarifiedJson.Set(ctx, string.IsNullOrWhiteSpace(existing) ? "{}" : existing);
                return stage;
            })
        };
        readPositionStage.SetDisplayText("Read Position Stage");

        // stage == complete → resolution done (short-circuit) OR between-runs (re-arm gate)
        var reEntryCompleteGate = new FlowDecision(ctx => positionStage.Get(ctx) == "complete")
        { Id = "ReEntryComplete", Name = "Clarification Already Accepted?" };
        reEntryCompleteGate.SetDisplayText("Clarification Already Accepted?");

        var resolutionDoneGate = new FlowDecision(ctx => existingResolved.Get(ctx))
        { Id = "ResolutionDone", Name = "Resolution Complete?" };
        resolutionDoneGate.SetDisplayText("Resolution Complete?");

        // ── Run A — produce the questions phase ────────────────────────
        var dispatchRunA = new DispatchWorkflow
        {
            Id = "DispatchRunA", Name = "Dispatch Clarification (Questions)",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["documentType"]          = ClarificationDocumentType,
                ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                ["producerAction"]        = AgentAction.ClarifyRequirements.ToWire(),
                ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = ambiguityContext.Get(ctx) ?? "",
                    ["conventions"]     = "",
                }),
                ["issueId"]             = issueId.Get(ctx) ?? "",
                ["correlationId"]       = issueId.Get(ctx) ?? "",
                ["tenantId"]            = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(runAResult),
        };
        dispatchRunA.SetDisplayText("Dispatch Clarification (Questions)");

        var readRunAExit = new SetVariable
        {
            Id = "ReadRunAExit", Name = "Read Run A Exit",
            Variable = questionsJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(runAResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);
                var (qCount, _) = AssessmentBindingHelper.ReadClarification(exit.DocumentJson);
                runAAccepted.Set(ctx, accepted);
                questionCount.Set(ctx, qCount);
                failureDetail.Set(ctx, AssessmentBindingHelper.BuildFailureDetail(exit));
                return exit.DocumentJson;
            })
        };
        readRunAExit.SetDisplayText("Read Run A Exit");

        var runAAcceptedGate = new FlowDecision(ctx => runAAccepted.Get(ctx))
        { Id = "RunAAccepted", Name = "Questions Accepted?" };
        runAAcceptedGate.SetDisplayText("Questions Accepted?");

        var emitQuestionsGenerated = new EmitClarifyEventActivity
        {
            Id = "EmitQuestionsGenerated", Name = "Emit Questions Generated",
            EventType = new(ClarifyEvents.QuestionsGenerated),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitQuestionsGenerated.SetDisplayText("Emit Questions Generated");

        var deliverQuestions = new DeliverClarifyingQuestionsActivity
        {
            Id = "DeliverClarifyingQuestions", Name = "Deliver Clarifying Questions",
            SessionId = new(ctx => sessionId.Get(ctx)),
            IssueId = new(ctx => issueId.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            IssueNumber = new(ctx => issueNumber.Get(ctx)),
            QuestionsJson = new(ctx => questionsJson.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Result = new(deliveryResult),
        };
        deliverQuestions.SetDisplayText("Deliver Clarifying Questions");

        var emitQuestionsDelivered = new EmitClarifyEventActivity
        {
            Id = "EmitQuestionsDelivered", Name = "Emit Questions Delivered",
            EventType = new(ClarifyEvents.QuestionsDelivered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Channel = new(ctx => deliveryResult.Get(ctx)?.Channel ?? "api"),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitQuestionsDelivered.SetDisplayText("Emit Questions Delivered");

        // ── Suspend on the generic input gate (D3) ─────────────────────
        var waitForInput = new WaitForDocumentInputActivity
        {
            Id = "WaitForDocumentInput", Name = "Wait For Document Input",
            SessionId = new(ctx => sessionId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            TimeoutConfigKey = new("Clarify:AnswerTimeoutMinutes"),
            InputJson = new(answers),
            Received = new(inputReceived),
            TimedOut = new(inputTimedOut),
        };
        waitForInput.SetDisplayText("Wait For Document Input");

        // ── Received path → Run B (resolution) ─────────────────────────
        var emitAnswersReceived = new EmitClarifyEventActivity
        {
            Id = "EmitAnswersReceived", Name = "Emit Answers Received",
            EventType = new(ClarifyEvents.AnswersReceived),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitAnswersReceived.SetDisplayText("Emit Answers Received");

        var dispatchRunB = new DispatchWorkflow
        {
            Id = "DispatchRunB", Name = "Dispatch Clarification (Resolution)",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["documentType"]          = ClarificationDocumentType,
                ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                ["producerAction"]        = AgentAction.IncorporateAnswers.ToWire(),
                ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = BuildIncorporationContext(
                        ambiguityContext.Get(ctx), questionsJson.Get(ctx), answers.Get(ctx)),
                    ["conventions"]     = "",
                }),
                ["issueId"]             = issueId.Get(ctx) ?? "",
                ["correlationId"]       = issueId.Get(ctx) ?? "",
                // NO cross-run supersedes edge, deliberately. `document-lifecycle` owns the
                // supersedes field end-to-end (a REVISE mints the superseding draft) and
                // 39-11's store enforces a strictly linear, write-once chain — a per-run
                // input edge cannot be reconciled with either. Cross-run lineage for the two
                // Clarification runs rides the shared issueId/correlationId, which is what
                // the 39-11 lineage query groups on. See
                // `.dev/findings/assessment-family-policy-gaps.md` #4.
                ["tenantId"]            = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(runBResult),
        };
        dispatchRunB.SetDisplayText("Dispatch Clarification (Resolution)");

        var readRunBExit = new SetVariable
        {
            Id = "ReadRunBExit", Name = "Read Run B Exit",
            Variable = clarifiedJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(runBResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);
                var (_, res) = AssessmentBindingHelper.ReadClarification(exit.DocumentJson);
                runBAccepted.Set(ctx, accepted);
                resolved.Set(ctx, res);
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                failureDetail.Set(ctx, AssessmentBindingHelper.BuildFailureDetail(exit));
                return exit.DocumentJson;
            })
        };
        readRunBExit.SetDisplayText("Read Run B Exit");

        var runBAcceptedGate = new FlowDecision(ctx => runBAccepted.Get(ctx))
        { Id = "RunBAccepted", Name = "Resolution Accepted?" };
        runBAcceptedGate.SetDisplayText("Resolution Accepted?");

        var emitRequirementsClarified = new EmitClarifyEventActivity
        {
            Id = "EmitRequirementsClarified", Name = "Emit Requirements Clarified",
            EventType = new(ClarifyEvents.RequirementsClarified),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitRequirementsClarified.SetDisplayText("Emit Requirements Clarified");

        // ── Failure / timeout emits ────────────────────────────────────
        var emitQuestionsFailed = new EmitClarifyEventActivity
        {
            Id = "EmitQuestionsFailed", Name = "Emit Questions Failed",
            EventType = new(ClarifyEvents.QuestionsFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitQuestionsFailed.SetDisplayText("Emit Questions Failed");

        var emitIncorporationFailed = new EmitClarifyEventActivity
        {
            Id = "EmitIncorporationFailed", Name = "Emit Incorporation Failed",
            EventType = new(ClarifyEvents.IncorporationFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitIncorporationFailed.SetDisplayText("Emit Incorporation Failed");

        var emitTimedOut = new EmitClarifyEventActivity
        {
            Id = "EmitTimedOut", Name = "Emit Timed Out",
            EventType = new(ClarifyEvents.AnswersTimedOut),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Answer SLA expired with no stakeholder response"),
        };
        emitTimedOut.SetDisplayText("Emit Timed Out");

        // ── Status setters feeding the single output region ────────────
        var setClarifiedStatus = new SetVariable
        {
            Id = "SetClarifiedStatus", Name = "Set Clarified Status",
            Variable = outputStatus, Value = new(_ => "clarified")
        };
        setClarifiedStatus.SetDisplayText("Set Clarified Status");

        var setFailedStatus = new SetVariable
        {
            Id = "SetFailedStatus", Name = "Set Failed Status",
            Variable = outputStatus,
            Value = new(ctx => { resolved.Set(ctx, false); return "failed"; })
        };
        setFailedStatus.SetDisplayText("Set Failed Status");

        var setTimeoutStatus = new SetVariable
        {
            Id = "SetTimeoutStatus", Name = "Set Timeout Status",
            Variable = outputStatus,
            Value = new(ctx => { resolved.Set(ctx, false); return "timed_out"; })
        };
        setTimeoutStatus.SetDisplayText("Set Timeout Status");

        // ── Expose output — the single terminal region ─────────────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputClarifiedRequirement", Name = "Output Clarified Requirement", OutputName = new("clarifiedRequirement"), OutputValue = new(ctx => (object)(clarifiedJson.Get(ctx) ?? "{}")) }, "Output Clarified Requirement"),
                WithLabel(new SetOutput { Id = "OutputResolved", Name = "Output Resolved", OutputName = new("resolved"), OutputValue = new(ctx => (object)resolved.Get(ctx)) }, "Output Resolved"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "ClarifyingQuestionsFlowchart",
            Name = "Clarifying Questions Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage,
                reEntryCompleteGate, resolutionDoneGate,
                dispatchRunA, readRunAExit, runAAcceptedGate,
                emitQuestionsGenerated, deliverQuestions, emitQuestionsDelivered,
                waitForInput, emitAnswersReceived, dispatchRunB, readRunBExit, runBAcceptedGate,
                emitRequirementsClarified,
                emitQuestionsFailed, emitIncorporationFailed, emitTimedOut,
                setClarifiedStatus, setFailedStatus, setTimeoutStatus,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, reEntryCompleteGate),

                // Re-entry: already-accepted clarification → resolution done vs between-runs.
                new(new FlowEndpoint(reEntryCompleteGate, "True"),  new FlowEndpoint(resolutionDoneGate)),
                new(new FlowEndpoint(resolutionDoneGate, "True"),   new FlowEndpoint(setClarifiedStatus)),
                new(new FlowEndpoint(resolutionDoneGate, "False"),  new FlowEndpoint(waitForInput)),
                // Fresh / in-progress → Run A.
                new(new FlowEndpoint(reEntryCompleteGate, "False"), new FlowEndpoint(dispatchRunA)),

                new(dispatchRunA, readRunAExit),
                new(readRunAExit, runAAcceptedGate),
                new(new FlowEndpoint(runAAcceptedGate, "True"),  new FlowEndpoint(emitQuestionsGenerated)),
                new(new FlowEndpoint(runAAcceptedGate, "False"), new FlowEndpoint(emitQuestionsFailed)),

                new(emitQuestionsGenerated, deliverQuestions),
                new(deliverQuestions, emitQuestionsDelivered),
                new(emitQuestionsDelivered, waitForInput),

                // Received → Run B.
                new(new FlowEndpoint(waitForInput, "Received"), new FlowEndpoint(emitAnswersReceived)),
                new(emitAnswersReceived, dispatchRunB),
                new(dispatchRunB, readRunBExit),
                new(readRunBExit, runBAcceptedGate),
                new(new FlowEndpoint(runBAcceptedGate, "True"),  new FlowEndpoint(emitRequirementsClarified)),
                new(new FlowEndpoint(runBAcceptedGate, "False"), new FlowEndpoint(emitIncorporationFailed)),
                new(emitRequirementsClarified, setClarifiedStatus),

                // Timeout.
                new(new FlowEndpoint(waitForInput, "Timeout"), new FlowEndpoint(emitTimedOut)),
                new(emitTimedOut, setTimeoutStatus),

                // Failure emits → failed status.
                new(emitQuestionsFailed, setFailedStatus),
                new(emitIncorporationFailed, setFailedStatus),

                // Single output region.
                new(setClarifiedStatus, exposeOutput),
                new(setFailedStatus, exposeOutput),
                new(setTimeoutStatus, exposeOutput),
            }
        };
    }

    /// <summary>
    /// Compose the context findings for the resolution (incorporate-answers) run — the
    /// original ambiguity context plus the asked questions and the stakeholder's answers,
    /// so the model incorporates the answers into a disambiguated requirement. Pure; exposed
    /// for unit testing (kept from the pre-migration workflow).
    /// </summary>
    internal static string BuildIncorporationContext(string? ambiguityContext, string? questionsJson, string? answers)
    {
        return $"{ambiguityContext ?? ""}\n\n## Clarifying Questions Asked\n{questionsJson ?? ""}\n\n## Stakeholder Answers\n{answers ?? ""}";
    }
}
