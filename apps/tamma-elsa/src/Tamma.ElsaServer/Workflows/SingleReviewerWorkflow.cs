using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-7 (AC1, AC5; Design Decisions D3–D5) — the single-reviewer producer
/// (<c>DefinitionId = "review-single-reviewer"</c>). Given a subject (document or
/// diff reference), a policy-selected reviewer <c>(role, action)</c>, and lineage
/// anchors, it dispatches ONE mediated <c>llm-call</c> and yields exactly one
/// VALIDATED unified <see cref="Review"/> envelope. Unparseable/invalid reviewer
/// output is NOT laundered into a defaulted <c>"concerns"</c> review (the
/// <c>PlanReviewWorkflow.ExtractReview</c> anti-pattern) — it flows through a bounded
/// validation/repair ring back to the SAME dispatch node, and on exhaustion emits a
/// TYPED failure (<c>success=false</c>, <c>failureKind="validation-exhausted"</c> or
/// <c>"llm-failed"</c>) that the 39-6 caller maps to
/// <c>ValidationExhausted</c>/<c>ReviewUndecidable</c>.
///
/// <para>There is NO edge from an invalid mapping to the success outputs (the
/// no-laundering structural pin). The one <c>llm-call</c> dispatch reads its
/// <c>(role, action)</c> from workflow variables (data-driven, D9) — validated
/// fail-loud at <c>Init</c> via <see cref="ReviewerSelectionHelper.Resolve"/>.</para>
/// </summary>
public class SingleReviewerWorkflow : WorkflowBase
{
    public const string ReviewSingleReviewerDefinitionId = "review-single-reviewer";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Single Reviewer";
        builder.DefinitionId = ReviewSingleReviewerDefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Dispatches one policy-selected reviewer (role, action) llm-call and yields exactly one " +
            "validated unified Review envelope (bounded repair; typed failure on exhaustion; never a laundered review)";

        // ── Data-driven dispatch inputs (default "" so the drift scanner sees the
        //    one llm-call dispatch as DATA-DRIVEN, D9) ──
        var reviewerRole = builder.WithVariable<string>("ReviewerRole", "");
        var reviewerAction = builder.WithVariable<string>("ReviewerAction", "");
        var variablesJson = builder.WithVariable<string>("VariablesJson", "{}");
        var documentTypeKey = builder.WithVariable<string>("DocumentTypeKey", "");

        // ── Lineage + config scalars ──
        var subjectJson = builder.WithVariable<string>("SubjectJson", "");
        var feedbackVariableName = builder.WithVariable<string>("FeedbackVariableName", ReviewProducerHelper.DefaultFeedbackVariable);
        var issueId = builder.WithVariable<string>("IssueId", "");
        var correlationId = builder.WithVariable<string>("CorrelationId", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var maxRepairAttempts = builder.WithVariable<int>("MaxRepairAttempts", AcceptanceDefaults.DefaultMaxValidationRepairAttempts);
        var attempts = builder.WithVariable<int>("Attempts", 0);

        // ── Dispatch result + routing ──
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();
        var everSucceededCall = builder.WithVariable<bool>("EverSucceededCall", false);
        var validationOk = builder.WithVariable<bool>("ValidationOk", false);

        // ── Outputs ──
        var reviewJson = builder.WithVariable<string>("ReviewJson", "");
        var reviewEnvelopeJson = builder.WithVariable<string>("ReviewEnvelopeJson", "");
        var reviewDocumentId = builder.WithVariable<string>("ReviewDocumentId", "");
        var violationsJson = builder.WithVariable<string>("ViolationsJson", "[]");
        var failureKind = builder.WithVariable<string>("FailureKind", "");

        // ================================================================
        // Init — resolve reviewer (role, action) fail-loud (AC4), seed config
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Init",
            Variable = reviewerRole,
            Value = new(ctx =>
            {
                var role = ctx.GetInput<string>("reviewerRole") ?? "";
                var actionOverride = ctx.GetInput<string>("reviewerAction");
                var subjJson = ctx.GetInput<string>("subjectJson") ?? "";
                var varsJson = ctx.GetInput<string>("variablesJson") ?? "{}";
                var feedbackVar = ctx.GetInput<string>("feedbackVariableName");
                var issue = ctx.GetInput<string>("issueId") ?? "";
                var corr = ctx.GetInput<string>("correlationId") ?? "";
                var tenant = ctx.GetInput<string>("tenantId") ?? "";
                var docTypeKey = ctx.GetInput<string>("documentTypeKey") ?? "";
                var rulesInput = ctx.GetInput<string>("acceptanceRulesJson") ?? "";

                var subject = ParseSubject(subjJson);
                var spec = ReviewerSelectionHelper.Resolve(role, actionOverride, subject.Kind, docTypeKey);
                var rules = ResolveRules(rulesInput);

                reviewerAction.Set(ctx, spec.Action.ToWire());
                subjectJson.Set(ctx, JsonSerializer.Serialize(subject, DocumentJson.Options));
                variablesJson.Set(ctx, string.IsNullOrWhiteSpace(varsJson) ? "{}" : varsJson);
                feedbackVariableName.Set(ctx, string.IsNullOrWhiteSpace(feedbackVar) ? ReviewProducerHelper.DefaultFeedbackVariable : feedbackVar!);
                issueId.Set(ctx, issue);
                correlationId.Set(ctx, string.IsNullOrWhiteSpace(corr) ? issue : corr);
                tenantId.Set(ctx, tenant);
                documentTypeKey.Set(ctx, docTypeKey);
                maxRepairAttempts.Set(ctx, rules.MaxValidationRepairAttempts);
                attempts.Set(ctx, 0);
                everSucceededCall.Set(ctx, false);

                return spec.Role.ToWire();
            })
        };
        init.SetDisplayText("Init");

        // ================================================================
        // DispatchReviewerCall — the one mediated llm-call (loop target)
        // ================================================================
        var dispatchReviewerCall = new DispatchWorkflow
        {
            Id = "DispatchReviewerCall", Name = "Dispatch Reviewer Call",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                // agentRole is the REAL llm-call input key (NOT role).
                ["agentRole"] = reviewerRole.Get(ctx) ?? "",
                ["action"] = reviewerAction.Get(ctx) ?? "",
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                ["documentType"] = documentTypeKey.Get(ctx) ?? "",
                ["issueId"] = issueId.Get(ctx) ?? "",
                ["variables"] = ParseVarsDict(variablesJson.Get(ctx)),
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        dispatchReviewerCall.SetDisplayText("Dispatch Reviewer Call");

        // ================================================================
        // MapAndValidate — map reply → unified Review + validate (no laundering)
        // ================================================================
        var mapAndValidate = new SetVariable
        {
            Id = "MapAndValidate", Name = "Map And Validate",
            Variable = validationOk,
            Value = new(ctx =>
            {
                var result = llmResult.Get(ctx);
                var success = ReadSuccessFlag(result);
                if (success) everSucceededCall.Set(ctx, true);

                if (!success)
                {
                    violationsJson.Set(ctx, SerializeViolations(new[]
                    {
                        new DocumentViolation("REVIEW.PRODUCER.LLM_FAILED",
                            "The reviewer llm-call reported failure (no usable reply this turn)."),
                    }));
                    return false;
                }

                var response = ReadLlmResponse(result);
                var subject = ParseSubject(subjectJson.Get(ctx));
                var map = ReviewProducerHelper.MapReviewerReply(response, subject);

                if (map.IsValid)
                {
                    reviewJson.Set(ctx, JsonSerializer.Serialize(map.Payload, DocumentJson.Options));
                    violationsJson.Set(ctx, "[]");
                    return true;
                }

                violationsJson.Set(ctx, SerializeViolations(map.Violations));
                return false;
            })
        };
        mapAndValidate.SetDisplayText("Map And Validate");

        var validationGate = new FlowDecision(ctx => validationOk.Get(ctx))
        { Id = "ValidationGate", Name = "Valid?" };
        validationGate.SetDisplayText("Valid?");

        // ================================================================
        // Valid path — build envelope, emit PRODUCED/VALIDATED success, outputs
        // ================================================================
        var buildEnvelope = new SetVariable
        {
            Id = "BuildEnvelope", Name = "Build Envelope",
            Variable = reviewEnvelopeJson,
            Value = new(ctx =>
            {
                JsonElement payload;
                using (var doc = JsonDocument.Parse(reviewJson.Get(ctx)))
                    payload = doc.RootElement.Clone();

                var producer = DocumentProducer.Create(
                    reviewerRole.Get(ctx), reviewerAction.Get(ctx), ReviewSingleReviewerDefinitionId);

                var envelope = DocumentEnvelope.CreateDraft(
                        DocumentTypeKey.Review, 1, issueId.Get(ctx), correlationId.Get(ctx), producer, payload,
                        now: DateTimeOffset.UtcNow)
                    .WithState(DocumentState.Validated, DateTimeOffset.UtcNow);

                reviewDocumentId.Set(ctx, envelope.Id.ToString());
                return DocumentJson.Serialize(envelope);
            })
        };
        buildEnvelope.SetDisplayText("Build Envelope");

        var emitProduced = ReviewDocEvent(
            "EmitProduced", "Emit Produced", DocumentEvents.ProducedSuccess,
            reviewDocumentId, issueId, correlationId, tenantId, reviewEnvelopeJson, "Review produced");
        var emitValidated = ReviewDocEvent(
            "EmitValidated", "Emit Validated", DocumentEvents.ValidatedSuccess,
            reviewDocumentId, issueId, correlationId, tenantId, reviewEnvelopeJson, "Review validated");

        var setOutputsSuccess = new Sequence
        {
            Id = "SetOutputsSuccess", Name = "Set Outputs (Success)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccessTrue", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutReviewJson", OutputName = new("reviewJson"), OutputValue = new(ctx => (object)(reviewJson.Get(ctx) ?? "")) }, "Output reviewJson"),
                WithLabel(new SetOutput { Id = "OutReviewEnvelope", OutputName = new("reviewEnvelopeJson"), OutputValue = new(ctx => (object)(reviewEnvelopeJson.Get(ctx) ?? "")) }, "Output reviewEnvelopeJson"),
                WithLabel(new SetOutput { Id = "OutReviewDocId", OutputName = new("reviewDocumentId"), OutputValue = new(ctx => (object)(reviewDocumentId.Get(ctx) ?? "")) }, "Output reviewDocumentId"),
            }
        };
        setOutputsSuccess.SetDisplayText("Set Outputs (Success)");

        // ================================================================
        // Invalid path — bounded repair ring back to the SAME dispatch node
        // ================================================================
        var repairGate = new FlowDecision(ctx =>
            ReviewProducerHelper.ShouldRepair(attempts.Get(ctx), RepairRules(maxRepairAttempts.Get(ctx))))
        { Id = "RepairGate", Name = "Can Repair?" };
        repairGate.SetDisplayText("Can Repair?");

        var buildRepairFeedback = new SetVariable
        {
            Id = "BuildRepairFeedback", Name = "Build Repair Feedback",
            Variable = variablesJson,
            Value = new(ctx =>
            {
                attempts.Set(ctx, attempts.Get(ctx) + 1);
                var violations = DeserializeViolations(violationsJson.Get(ctx));
                var contract = DocumentTypeRegistry.Resolve(DocumentTypeKey.Review).RenderContract();
                return ReviewProducerHelper.BuildRepairVariables(
                    variablesJson.Get(ctx), violations, feedbackVariableName.Get(ctx), contract);
            })
        };
        buildRepairFeedback.SetDisplayText("Build Repair Feedback");

        var setFailureKind = new SetVariable
        {
            Id = "SetFailureKind", Name = "Set Failure Kind",
            Variable = failureKind,
            Value = new(ctx => (object)(everSucceededCall.Get(ctx) ? "validation-exhausted" : "llm-failed"))
        };
        setFailureKind.SetDisplayText("Set Failure Kind");

        var emitProducedFailed = ReviewDocEvent(
            "EmitProducedFailed", "Emit Produced Failed", DocumentEvents.ProducedFailed,
            reviewDocumentId, issueId, correlationId, tenantId, violationsJson, "Reviewer produced no valid review");
        var emitValidatedFailed = ReviewDocEvent(
            "EmitValidatedFailed", "Emit Validated Failed", DocumentEvents.ValidatedFailed,
            reviewDocumentId, issueId, correlationId, tenantId, violationsJson, "Review failed validation (exhausted)");

        var setOutputsFail = new Sequence
        {
            Id = "SetOutputsFail", Name = "Set Outputs (Fail)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccessFalse", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutFailureKind", OutputName = new("failureKind"), OutputValue = new(ctx => (object)(failureKind.Get(ctx) ?? "")) }, "Output failureKind"),
                WithLabel(new SetOutput { Id = "OutViolations", OutputName = new("violationsJson"), OutputValue = new(ctx => (object)(violationsJson.Get(ctx) ?? "[]")) }, "Output violationsJson"),
            }
        };
        setOutputsFail.SetDisplayText("Set Outputs (Fail)");

        var finish = new Finish { Id = "Finish", Name = "Finish" };
        finish.SetDisplayText("Finish");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "SingleReviewerFlowchart",
            Start = init,
            Activities =
            {
                init, dispatchReviewerCall, mapAndValidate, validationGate,
                buildEnvelope, emitProduced, emitValidated, setOutputsSuccess,
                repairGate, buildRepairFeedback,
                setFailureKind, emitProducedFailed, emitValidatedFailed, setOutputsFail,
                finish,
            },
            Connections =
            {
                Connect(init, dispatchReviewerCall),
                Connect(dispatchReviewerCall, mapAndValidate),
                Connect(mapAndValidate, validationGate),

                ConnectOutcome(validationGate, "True", buildEnvelope),
                Connect(buildEnvelope, emitProduced),
                Connect(emitProduced, emitValidated),
                Connect(emitValidated, setOutputsSuccess),
                Connect(setOutputsSuccess, finish),

                ConnectOutcome(validationGate, "False", repairGate),
                ConnectOutcome(repairGate, "True", buildRepairFeedback),
                Connect(buildRepairFeedback, dispatchReviewerCall),   // loop back to the SAME dispatch node
                ConnectOutcome(repairGate, "False", setFailureKind),
                Connect(setFailureKind, emitProducedFailed),
                Connect(emitProducedFailed, emitValidatedFailed),
                Connect(emitValidatedFailed, setOutputsFail),
                Connect(setOutputsFail, finish),
            }
        };
    }

    // ====================================================================
    // Node factories
    // ====================================================================

    private static EmitDocumentEventActivity ReviewDocEvent(
        string id, string name, string eventType,
        Variable<string> docId, Variable<string> issueId, Variable<string> corr,
        Variable<string> tenant, Variable<string> dataJson, string detail)
    {
        var e = new EmitDocumentEventActivity
        {
            Id = id, Name = name,
            EventType = new(eventType),
            DocumentId = new(ctx => docId.Get(ctx)),
            DocumentType = new("review"),
            Round = new(0),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => corr.Get(ctx)),
            TenantId = new(ctx => tenant.Get(ctx)),
            Detail = new(detail),
            DataJson = new(ctx => { var d = dataJson.Get(ctx); return string.IsNullOrWhiteSpace(d) ? null : d; }),
        };
        e.SetDisplayText(name);
        return e;
    }

    // ====================================================================
    // Pure helpers
    // ====================================================================

    private static AcceptanceRules ResolveRules(string? rulesInput) =>
        string.IsNullOrWhiteSpace(rulesInput) ? AcceptanceDefaults.Rules : AcceptanceRulesJson.Deserialize(rulesInput!);

    private static AcceptanceRules RepairRules(int maxRepairAttempts) =>
        AcceptanceDefaults.Rules with { MaxValidationRepairAttempts = maxRepairAttempts };

    private static ReviewSubject ParseSubject(string? subjectJson)
    {
        if (string.IsNullOrWhiteSpace(subjectJson))
            throw SubjectMissing();
        try
        {
            var subject = JsonSerializer.Deserialize<ReviewSubject>(subjectJson!, DocumentJson.Options);
            return subject ?? throw SubjectMissing();
        }
        catch (JsonException)
        {
            throw SubjectMissing();
        }
    }

    private static TammaError SubjectMissing() => new(
        "REVIEW.PRODUCER.SUBJECT_MISSING",
        "The single-reviewer producer requires a parseable ReviewSubject (subjectJson).",
        retryable: false,
        severity: TammaErrorSeverity.High);

    private static string SerializeViolations(IReadOnlyList<DocumentViolation> violations) =>
        JsonSerializer.Serialize(violations);

    private static IReadOnlyList<DocumentViolation> DeserializeViolations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DocumentViolation>();
        try
        {
            return JsonSerializer.Deserialize<List<DocumentViolation>>(json!) ?? new List<DocumentViolation>();
        }
        catch (JsonException)
        {
            return Array.Empty<DocumentViolation>();
        }
    }

    private static Dictionary<string, object> ParseVarsDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json!) ?? new Dictionary<string, object>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    private static bool ReadSuccessFlag(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (!result.TryGetValue("success", out var s)) return false;
        return s is true || (s is string str && bool.TryParse(str, out var b) && b);
    }

    private static string? ReadLlmResponse(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("llmResponse", out var r) && r is not null)
            return r.ToString();
        return null;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
