using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities;
using Tamma.Activities.Clarify;
using Tamma.Activities.Clarify.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 3.5 — Clarifying Questions sub-workflow. Given an ambiguous issue /
/// requirement it uses the LLM (via the MEDIATED <c>llm-call</c> path — the engine
/// holds no LLM credential, TAMMA001) to generate clarifying questions, DELIVERS them
/// to the issue, SUSPENDS on a bookmark awaiting the human answers, then RESUMES (via
/// the secure <c>ClarifyResumeEndpoint</c>) and incorporates the answers into a
/// disambiguated requirement.
///
/// Flow:
///   1. Read inputs (issue/requirement + tenantId; mint a session id if none)
///   2. Generate clarifying questions via DispatchWorkflow("llm-call")
///      role=product_owner / action=clarify-requirements
///   3. Deliver questions to the issue (mediated git seam) — emit CLARIFY.QUESTIONS.DELIVERED
///   4. Wait for answers (bookmark, durable SLA timeout)
///   5a. On answer: incorporate via DispatchWorkflow("llm-call"), emit
///       CLARIFY.REQUIREMENTS.CLARIFIED, set outputs
///   5b. On timeout: emit CLARIFY.ANSWERS.TIMED_OUT (LOUD), set outputs
///
/// Reuses the <see cref="AssessmentWorkflow"/> skeleton (llm-call → deliver →
/// bookmark-wait → analyze/resume, fail-closed gates + error terminal).
///
/// Fail-closed: if either <c>llm-call</c> returns success=false, or the JSON response
/// cannot be parsed into the expected shape, the workflow emits a LOUD
/// <c>CLARIFY.*.FAILED</c> event and routes to the LlmCallError terminal — it NEVER
/// proceeds with fabricated questions or a fabricated clarification. Prompt resolution
/// is tenant→system→error (the <c>llm-call</c> registry never falls back to an
/// empty/plain prompt).
///
/// CLARIFY.* DCB events (AGGREGATE.ACTION.STATUS) are emitted at every transition so
/// the clarification is fully auditable and feeds the Epic-32 learning loop.
/// </summary>
public class ClarifyingQuestionsWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ClarifyingQuestions";
        builder.DefinitionId = "clarifying-questions";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Resolve requirement ambiguity via LLM-generated clarifying questions + human answers";

        // ── Workflow variables ──────────────────────────────────────────
        var sessionId       = builder.WithVariable<Guid>();
        var issueId         = builder.WithVariable<string>();
        var requirement     = builder.WithVariable<string>();
        var repository      = builder.WithVariable<string>();
        var issueNumber     = builder.WithVariable<int>();
        var ambiguityContext = builder.WithVariable<string>();
        var tenantId        = builder.WithVariable<string>("TenantId", "");

        var questionsJson   = builder.WithVariable<string>();
        var questionCount   = builder.WithVariable<int>();
        var answers         = builder.WithVariable<string>();
        var clarifiedJson   = builder.WithVariable<string>();

        // llm-call result containers
        var questionLlm     = builder.WithVariable<IDictionary<string, object>?>();
        var incorporateLlm  = builder.WithVariable<IDictionary<string, object>?>();

        // Success flags (fail-closed guards)
        var questionsLlmOk      = builder.WithVariable<bool>();
        var incorporationLlmOk  = builder.WithVariable<bool>();

        // Activity output capture
        var deliveryResult      = builder.WithVariable<ClarifyDeliveryResult>();
        var waitAnswers         = builder.WithVariable<string>();
        var waitAnswered        = builder.WithVariable<bool>();
        var waitTimedOut        = builder.WithVariable<bool>();
        var clarificationOutput = builder.WithVariable<ClarificationResult>();

        // Output variables (readable by a parent workflow)
        var outputStatus        = builder.WithVariable<string>();
        var outputClarifiedJson = builder.WithVariable<string>();
        var outputResolved      = builder.WithVariable<bool>();

        // ── Step 1: Read inputs ────────────────────────────────────────
        var readInputs = new SetVariable
        {
            Id = "ReadInputs",
            Name = "Read Inputs",
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
                return sid;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Generate clarifying questions via llm-call ─────────
        var generateQuestionsLlm = new DispatchWorkflow
        {
            Id = "GenerateQuestionsLlm",
            Name = "Generate Clarifying Questions (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"]     = AgentRole.ProductOwner.ToWire(),
                ["action"]   = AgentAction.ClarifyRequirements.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = ambiguityContext.Get(ctx) ?? "",
                    ["conventions"]     = "",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(questionLlm),
        };
        generateQuestionsLlm.SetDisplayText("Generate Clarifying Questions (LLM)");

        // Parse llm-call response into a question set; set questionsLlmOk (fail-closed).
        var parseQuestions = new SetVariable
        {
            Id = "ParseQuestions",
            Name = "Parse Questions",
            Variable = questionsJson,
            Value = new(ctx =>
            {
                var result = questionLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    questionsLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var qs = ClarifyParsing.ParseQuestions(text);
                if (qs.Count == 0)
                {
                    // Fail-closed — no fabricated / empty question set.
                    questionsLlmOk.Set(ctx, false);
                    return "{}";
                }

                questionsLlmOk.Set(ctx, true);
                questionCount.Set(ctx, qs.Count);
                return JsonSerializer.Serialize(new ClarifyQuestionSet
                {
                    Questions = qs,
                    ContextSummary = ambiguityContext.Get(ctx),
                });
            })
        };
        parseQuestions.SetDisplayText("Parse Questions");

        var questionsSuccessCheck = new FlowDecision(ctx => questionsLlmOk.Get(ctx))
        { Id = "QuestionsLlmOk", Name = "Questions LLM OK?" };
        questionsSuccessCheck.SetDisplayText("Questions LLM OK?");

        // ── Step 3: Emit GENERATED + deliver + emit DELIVERED ──────────
        var emitQuestionsGenerated = new EmitClarifyEventActivity
        {
            Id = "EmitQuestionsGenerated",
            Name = "Emit Questions Generated",
            EventType = new(ClarifyEvents.QuestionsGenerated),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitQuestionsGenerated.SetDisplayText("Emit Questions Generated");

        var deliverQuestions = new DeliverClarifyingQuestionsActivity
        {
            Id = "DeliverClarifyingQuestions",
            Name = "Deliver Clarifying Questions",
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
            Id = "EmitQuestionsDelivered",
            Name = "Emit Questions Delivered",
            EventType = new(ClarifyEvents.QuestionsDelivered),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Channel = new(ctx => deliveryResult.Get(ctx)?.Channel ?? "api"),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitQuestionsDelivered.SetDisplayText("Emit Questions Delivered");

        // ── Step 4: Wait for answers (bookmark + durable SLA) ──────────
        var waitForAnswers = new WaitForClarifyingAnswersActivity
        {
            Id = "WaitForAnswers",
            Name = "Wait For Answers",
            SessionId = new(ctx => sessionId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Answers = new(waitAnswers),
            Answered = new(waitAnswered),
            TimedOut = new(waitTimedOut),
        };
        waitForAnswers.SetDisplayText("Wait For Answers");

        // ── Step 5a: Answer path ───────────────────────────────────────
        var storeAnswers = new SetVariable
        {
            Id = "StoreAnswers",
            Name = "Store Answers",
            Variable = answers,
            Value = new(ctx => waitAnswers.Get(ctx) ?? string.Empty)
        };
        storeAnswers.SetDisplayText("Store Answers");

        var emitAnswersReceived = new EmitClarifyEventActivity
        {
            Id = "EmitAnswersReceived",
            Name = "Emit Answers Received",
            EventType = new(ClarifyEvents.AnswersReceived),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitAnswersReceived.SetDisplayText("Emit Answers Received");

        var incorporateAnswersLlm = new DispatchWorkflow
        {
            Id = "IncorporateAnswersLlm",
            Name = "Incorporate Answers (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"]     = AgentRole.ProductOwner.ToWire(),
                ["action"]   = AgentAction.ClarifyRequirements.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"]    = requirement.Get(ctx) ?? "",
                    ["contextFindings"] = BuildIncorporationContext(
                        ambiguityContext.Get(ctx), questionsJson.Get(ctx), answers.Get(ctx)),
                    ["conventions"]     = "",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(incorporateLlm),
        };
        incorporateAnswersLlm.SetDisplayText("Incorporate Answers (LLM)");

        var parseIncorporation = new SetVariable
        {
            Id = "ParseIncorporation",
            Name = "Parse Incorporation",
            Variable = clarifiedJson,
            Value = new(ctx =>
            {
                var result = incorporateLlm.Get(ctx);
                if (!ReadSuccessFlag(result))
                {
                    incorporationLlmOk.Set(ctx, false);
                    return "{}";
                }

                var text = result!.TryGetValue("llmResponse", out var r) ? r?.ToString() ?? "" : "";
                var parsed = ClarifyParsing.ParseClarification(text);
                if (parsed is null)
                {
                    // Fail-closed — no fabricated clarification.
                    incorporationLlmOk.Set(ctx, false);
                    return "{}";
                }

                incorporationLlmOk.Set(ctx, true);
                clarificationOutput.Set(ctx, parsed);
                return JsonSerializer.Serialize(parsed);
            })
        };
        parseIncorporation.SetDisplayText("Parse Incorporation");

        var incorporationSuccessCheck = new FlowDecision(ctx => incorporationLlmOk.Get(ctx))
        { Id = "IncorporationLlmOk", Name = "Incorporation LLM OK?" };
        incorporationSuccessCheck.SetDisplayText("Incorporation LLM OK?");

        var emitRequirementsClarified = new EmitClarifyEventActivity
        {
            Id = "EmitRequirementsClarified",
            Name = "Emit Requirements Clarified",
            EventType = new(ClarifyEvents.RequirementsClarified),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            QuestionCount = new(ctx => questionCount.Get(ctx)),
        };
        emitRequirementsClarified.SetDisplayText("Emit Requirements Clarified");

        var setOutputResult = new SetVariable
        {
            Id = "SetOutputResult",
            Name = "Set Output Result",
            Variable = outputStatus,
            Value = new(ctx =>
            {
                var parsed = clarificationOutput.Get(ctx);
                outputClarifiedJson.Set(ctx, clarifiedJson.Get(ctx) ?? "{}");
                outputResolved.Set(ctx, parsed?.Resolved ?? false);
                return "clarified";
            })
        };
        setOutputResult.SetDisplayText("Set Output Result");

        var exposeOutputResponse = new Sequence
        {
            Id = "ExposeOutputResponse",
            Name = "Expose Output Response",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputClarifiedRequirement", Name = "Output Clarified Requirement", OutputName = new("clarifiedRequirement"), OutputValue = new(ctx => (object)(outputClarifiedJson.Get(ctx) ?? "{}")) }, "Output Clarified Requirement"),
                WithLabel(new SetOutput { Id = "OutputResolved", Name = "Output Resolved", OutputName = new("resolved"), OutputValue = new(ctx => (object)outputResolved.Get(ctx)) }, "Output Resolved"),
            }
        };
        exposeOutputResponse.SetDisplayText("Expose Output Response");

        // ── Step 5b: Timeout path ──────────────────────────────────────
        var setTimeoutResult = new SetVariable
        {
            Id = "SetTimeoutResult",
            Name = "Set Timeout Result",
            Variable = outputStatus,
            Value = new(ctx =>
            {
                outputClarifiedJson.Set(ctx, "{}");
                outputResolved.Set(ctx, false);
                return "timed_out";
            })
        };
        setTimeoutResult.SetDisplayText("Set Timeout Result");

        var emitTimedOut = new EmitClarifyEventActivity
        {
            Id = "EmitTimedOut",
            Name = "Emit Timed Out",
            EventType = new(ClarifyEvents.AnswersTimedOut),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("Answer SLA expired with no stakeholder response"),
        };
        emitTimedOut.SetDisplayText("Emit Timed Out");

        var exposeOutputTimeout = new Sequence
        {
            Id = "ExposeOutputTimeout",
            Name = "Expose Output Timeout",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionIdTimeout", Name = "Output Session Id (Timeout)", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputStatusTimeout", Name = "Output Status (Timeout)", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status (Timeout)"),
                WithLabel(new SetOutput { Id = "OutputResolvedTimeout", Name = "Output Resolved (Timeout)", OutputName = new("resolved"), OutputValue = new(ctx => (object)outputResolved.Get(ctx)) }, "Output Resolved (Timeout)"),
            }
        };
        exposeOutputTimeout.SetDisplayText("Expose Output Timeout");

        // ── Fail-closed error terminals (LOUD events + Finish) ─────────
        var emitQuestionsFailed = new EmitClarifyEventActivity
        {
            Id = "EmitQuestionsFailed",
            Name = "Emit Questions Failed",
            EventType = new(ClarifyEvents.QuestionsFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for question generation failed or returned unparseable output"),
        };
        emitQuestionsFailed.SetDisplayText("Emit Questions Failed");

        var emitIncorporationFailed = new EmitClarifyEventActivity
        {
            Id = "EmitIncorporationFailed",
            Name = "Emit Incorporation Failed",
            EventType = new(ClarifyEvents.IncorporationFailed),
            SessionId = new(ctx => sessionId.Get(ctx).ToString()),
            IssueId = new(ctx => issueId.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new("llm-call for answer incorporation failed or returned unparseable output"),
        };
        emitIncorporationFailed.SetDisplayText("Emit Incorporation Failed");

        var llmCallError = new Finish
        {
            Id = "LlmCallError",
            Name = "LLM Call Error"
        };
        llmCallError.SetDisplayText("LLM Call Error");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "ClarifyingQuestionsFlowchart",
            Name = "Clarifying Questions Flowchart",
            Activities =
            {
                readInputs,
                generateQuestionsLlm,
                parseQuestions,
                questionsSuccessCheck,
                emitQuestionsGenerated,
                deliverQuestions,
                emitQuestionsDelivered,
                waitForAnswers,

                // Answer path
                storeAnswers,
                emitAnswersReceived,
                incorporateAnswersLlm,
                parseIncorporation,
                incorporationSuccessCheck,
                emitRequirementsClarified,
                setOutputResult,
                exposeOutputResponse,

                // Timeout path
                setTimeoutResult,
                emitTimedOut,
                exposeOutputTimeout,

                // Fail-closed error terminals
                emitQuestionsFailed,
                emitIncorporationFailed,
                llmCallError
            },
            Connections =
            {
                new(readInputs, generateQuestionsLlm),
                new(generateQuestionsLlm, parseQuestions),
                new(parseQuestions, questionsSuccessCheck),
                new(new FlowEndpoint(questionsSuccessCheck, "True"),  new FlowEndpoint(emitQuestionsGenerated)),
                new(new FlowEndpoint(questionsSuccessCheck, "False"), new FlowEndpoint(emitQuestionsFailed)),
                new(emitQuestionsFailed, llmCallError),

                new(emitQuestionsGenerated, deliverQuestions),
                new(deliverQuestions, emitQuestionsDelivered),
                new(emitQuestionsDelivered, waitForAnswers),

                // Answer path
                new(new FlowEndpoint(waitForAnswers, "Answered"), new FlowEndpoint(storeAnswers)),
                new(storeAnswers, emitAnswersReceived),
                new(emitAnswersReceived, incorporateAnswersLlm),
                new(incorporateAnswersLlm, parseIncorporation),
                new(parseIncorporation, incorporationSuccessCheck),
                new(new FlowEndpoint(incorporationSuccessCheck, "True"),  new FlowEndpoint(emitRequirementsClarified)),
                new(new FlowEndpoint(incorporationSuccessCheck, "False"), new FlowEndpoint(emitIncorporationFailed)),
                new(emitIncorporationFailed, llmCallError),
                new(emitRequirementsClarified, setOutputResult),
                new(setOutputResult, exposeOutputResponse),

                // Timeout path
                new(new FlowEndpoint(waitForAnswers, "Timeout"), new FlowEndpoint(setTimeoutResult)),
                new(setTimeoutResult, emitTimedOut),
                new(emitTimedOut, exposeOutputTimeout)
            }
        };
    }

    /// <summary>
    /// Read the <c>success</c> flag from a dispatched workflow's Result dictionary.
    /// Returns <c>false</c> if the dictionary is null, the key is absent, or the value
    /// is falsy — fail-closed by design. Uses the tolerant <see cref="ResumeInput.AsBool"/>
    /// read (boxed bool / string / JsonElement).
    /// </summary>
    internal static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return ResumeInput.AsBool(s);
    }

    /// <summary>
    /// Compose the context findings for the incorporation llm-call — the original
    /// ambiguity context plus the asked questions and the stakeholder's answers, so the
    /// model incorporates the answers into a disambiguated requirement. Pure; exposed
    /// for unit testing.
    /// </summary>
    internal static string BuildIncorporationContext(string? ambiguityContext, string? questionsJson, string? answers)
    {
        return $"{ambiguityContext ?? ""}\n\n## Clarifying Questions Asked\n{questionsJson ?? ""}\n\n## Stakeholder Answers\n{answers ?? ""}";
    }
}
