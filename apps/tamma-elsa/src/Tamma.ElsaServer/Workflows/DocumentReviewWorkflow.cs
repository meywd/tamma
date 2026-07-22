using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-7 (Design Decision D1) — the thin REVIEW-stage router
/// (<c>DefinitionId = "document-review"</c>, the definition-id contract 39-6 D10
/// pinned). Reads <c>ReviewerSelection.Mode</c> from the resolved acceptance rules
/// (fallback <see cref="AcceptanceDefaults.Rules"/>) and dispatches ONE of the two
/// producers — <see cref="SingleReviewerWorkflow"/> or <see cref="PanelReviewWorkflow"/>
/// — mapping their outputs onto 39-6 D10's contract
/// (<c>success</c>/<c>reviewJson</c>/<c>reviewDocumentId</c> + <c>failureKind</c>/
/// <c>undecidableReason</c>/<c>memberReviewsJson</c>).
///
/// <para>Zero <c>llm-call</c> nodes: the router only dispatches the two producer
/// sub-workflows by their pinned definition ids.</para>
/// </summary>
public class DocumentReviewWorkflow : WorkflowBase
{
    public const string DocumentReviewDefinitionId = "document-review";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Document Review";
        builder.DefinitionId = DocumentReviewDefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Thin REVIEW-stage router: reads ReviewerSelection.Mode and dispatches the single-reviewer or " +
            "panel producer, mapping their outputs onto the 39-6 D10 review contract";

        // ── Inputs (39-6 D10) + derived ──
        var documentType = builder.WithVariable<string>("DocumentType", "");
        var issueId = builder.WithVariable<string>("IssueId", "");
        var correlationId = builder.WithVariable<string>("CorrelationId", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");
        var reviewerRole = builder.WithVariable<string>("ReviewerRole", "");
        var subjectJson = builder.WithVariable<string>("SubjectJson", "");
        var contentJson = builder.WithVariable<string>("ContentJson", "{}");
        var variablesJson = builder.WithVariable<string>("VariablesJson", "{}");
        var isPanel = builder.WithVariable<bool>("IsPanel", false);

        // ── Dispatch results ──
        var producerResult = builder.WithVariable<IDictionary<string, object>?>();

        // ── Outputs ──
        var outSuccess = builder.WithVariable<bool>("OutSuccess", false);
        var outReviewJson = builder.WithVariable<string>("OutReviewJson", "");
        var outReviewDocId = builder.WithVariable<string>("OutReviewDocId", "");
        var outFailureKind = builder.WithVariable<string>("OutFailureKind", "");
        var outUndecidableReason = builder.WithVariable<string>("OutUndecidableReason", "");
        var outMemberReviews = builder.WithVariable<string>("OutMemberReviews", "[]");

        // ================================================================
        // Init — resolve rules + mode, build subject + reviewer variables
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Init",
            Variable = isPanel,
            Value = new(ctx =>
            {
                var docJson = ctx.GetInput<string>("documentJson") ?? "";
                var docType = ctx.GetInput<string>("documentType") ?? "";
                var issue = ctx.GetInput<string>("issueId") ?? "";
                var corr = ctx.GetInput<string>("correlationId") ?? "";
                var tenant = ctx.GetInput<string>("tenantId") ?? "";
                var rulesInput = ctx.GetInput<string>("acceptanceRulesJson") ?? "";
                var suppliedSubject = ctx.GetInput<string>("subjectJson");

                var rules = string.IsNullOrWhiteSpace(rulesInput) ? AcceptanceDefaults.Rules : AcceptanceRulesJson.Deserialize(rulesInput);
                var panel = rules.ReviewerSelection.Mode == ReviewerMode.Panel;

                var subject = !string.IsNullOrWhiteSpace(suppliedSubject)
                    ? suppliedSubject!
                    : JsonSerializer.Serialize(BuildDocumentSubject(docJson, docType), DocumentJson.Options);

                documentType.Set(ctx, docType);
                issueId.Set(ctx, issue);
                correlationId.Set(ctx, string.IsNullOrWhiteSpace(corr) ? issue : corr);
                tenantId.Set(ctx, tenant);
                acceptanceRulesJson.Set(ctx, rulesInput);
                reviewerRole.Set(ctx, rules.ReviewerSelection.ReviewerRole ?? "");
                subjectJson.Set(ctx, subject);
                contentJson.Set(ctx, ExtractPayload(docJson));
                variablesJson.Set(ctx, BuildReviewerVariables(docJson));

                return panel;
            })
        };
        init.SetDisplayText("Init");

        var modeGate = new FlowDecision(ctx => isPanel.Get(ctx))
        { Id = "ModeGate", Name = "Panel?" };
        modeGate.SetDisplayText("Panel?");

        // ── Panel branch ──
        var dispatchPanel = new DispatchWorkflow
        {
            Id = "DispatchPanel", Name = "Dispatch Panel",
            WorkflowDefinitionId = new(PanelReviewWorkflow.ReviewPanelDefinitionId),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["subjectJson"] = subjectJson.Get(ctx) ?? "",
                ["contentJson"] = contentJson.Get(ctx) ?? "{}",
                ["variablesJson"] = variablesJson.Get(ctx) ?? "{}",
                ["documentTypeKey"] = documentType.Get(ctx) ?? "",
                ["issueId"] = issueId.Get(ctx) ?? "",
                ["correlationId"] = correlationId.Get(ctx) ?? "",
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(producerResult),
        };
        dispatchPanel.SetDisplayText("Dispatch Panel");

        // ── Single branch ──
        var dispatchSingle = new DispatchWorkflow
        {
            Id = "DispatchSingle", Name = "Dispatch Single",
            WorkflowDefinitionId = new(SingleReviewerWorkflow.ReviewSingleReviewerDefinitionId),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["reviewerRole"] = reviewerRole.Get(ctx) ?? "",
                ["subjectJson"] = subjectJson.Get(ctx) ?? "",
                ["contentJson"] = contentJson.Get(ctx) ?? "{}",
                ["variablesJson"] = variablesJson.Get(ctx) ?? "{}",
                ["documentTypeKey"] = documentType.Get(ctx) ?? "",
                ["issueId"] = issueId.Get(ctx) ?? "",
                ["correlationId"] = correlationId.Get(ctx) ?? "",
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(producerResult),
        };
        dispatchSingle.SetDisplayText("Dispatch Single");

        var mapOutputs = new SetVariable
        {
            Id = "MapOutputs", Name = "Map Outputs",
            Variable = outSuccess,
            Value = new(ctx =>
            {
                var result = producerResult.Get(ctx);
                outReviewJson.Set(ctx, ReadString(result, "reviewJson"));
                outReviewDocId.Set(ctx, ReadString(result, "reviewDocumentId"));
                outFailureKind.Set(ctx, ReadString(result, "failureKind"));
                outUndecidableReason.Set(ctx, ReadString(result, "undecidableReason"));
                var members = ReadString(result, "memberReviewsJson");
                outMemberReviews.Set(ctx, string.IsNullOrWhiteSpace(members) ? "[]" : members);
                return ReadBool(result, "success");
            })
        };
        mapOutputs.SetDisplayText("Map Outputs");

        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(ctx => (object)outSuccess.Get(ctx)) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutReviewJson", OutputName = new("reviewJson"), OutputValue = new(ctx => (object)(outReviewJson.Get(ctx) ?? "")) }, "Output reviewJson"),
                WithLabel(new SetOutput { Id = "OutReviewDocId", OutputName = new("reviewDocumentId"), OutputValue = new(ctx => (object)(outReviewDocId.Get(ctx) ?? "")) }, "Output reviewDocumentId"),
                WithLabel(new SetOutput { Id = "OutFailureKind", OutputName = new("failureKind"), OutputValue = new(ctx => (object)(outFailureKind.Get(ctx) ?? "")) }, "Output failureKind"),
                WithLabel(new SetOutput { Id = "OutUndecidableReason", OutputName = new("undecidableReason"), OutputValue = new(ctx => (object)(outUndecidableReason.Get(ctx) ?? "")) }, "Output undecidableReason"),
                WithLabel(new SetOutput { Id = "OutMemberReviews", OutputName = new("memberReviewsJson"), OutputValue = new(ctx => (object)(outMemberReviews.Get(ctx) ?? "[]")) }, "Output memberReviewsJson"),
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Finish" };
        finish.SetDisplayText("Finish");

        builder.Root = new Flowchart
        {
            Id = "DocumentReviewFlowchart",
            Start = init,
            Activities =
            {
                init, modeGate, dispatchPanel, dispatchSingle, mapOutputs, setOutputs, finish,
            },
            Connections =
            {
                Connect(init, modeGate),
                ConnectOutcome(modeGate, "True", dispatchPanel),
                ConnectOutcome(modeGate, "False", dispatchSingle),
                Connect(dispatchPanel, mapOutputs),
                Connect(dispatchSingle, mapOutputs),
                Connect(mapOutputs, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    // ====================================================================
    // Pure helpers
    // ====================================================================

    private static ReviewSubject BuildDocumentSubject(string documentJson, string documentType)
    {
        Guid? documentId = null;
        if (!string.IsNullOrWhiteSpace(documentJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(documentJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(idEl.GetString(), out var g))
                    documentId = g;
            }
            catch (JsonException) { /* leave null */ }
        }

        return new ReviewSubject
        {
            Kind = ReviewerSelectionHelper.DocumentSubjectKind,
            DocumentId = documentId,
            DocumentType = documentType,
        };
    }

    private static string ExtractPayload(string documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(documentJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("payload", out var payload))
                return payload.GetRawText();
        }
        catch (JsonException) { /* fall through */ }
        return documentJson;
    }

    private static string BuildReviewerVariables(string documentJson)
    {
        var payload = ExtractPayload(documentJson);
        var vars = new Dictionary<string, object?>
        {
            ["planJson"] = payload,
            ["documentJson"] = documentJson ?? "",
        };
        return JsonSerializer.Serialize(vars);
    }

    private static string ReadString(IDictionary<string, object>? result, string key)
        => result != null && result.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static bool ReadBool(IDictionary<string, object>? result, string key)
    {
        if (result == null || !result.TryGetValue(key, out var v)) return false;
        return v is true || (v is string s && bool.TryParse(s, out var b) && b);
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
