using System.Text;
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
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 39-7 (AC2, AC6, AC7; Design Decision D2) — the panel producer
/// (<c>DefinitionId = "review-panel"</c>). Fans out N single-reviewer runs over the
/// policy-configured roster (a static 7-role superset with per-role membership
/// gates), persists every member review with lineage (each member IS a full
/// <see cref="SingleReviewerWorkflow"/> run — DOCUMENT.* events emitted inside), and
/// aggregates via the pure <see cref="ReviewPanelAggregation"/> into an aggregate
/// <see cref="Review"/> whose <c>AggregatedFrom</c> references its member review ids
/// (D7). An undecidable panel surfaces TYPED (<c>DOCUMENT.REVIEW_PANEL_UNDECIDABLE</c>,
/// <c>success=false</c>) carrying ALL member reviews — NO pessimistic aggregate is
/// ever fabricated (AC6).
///
/// <para>Zero <c>llm-call</c> nodes: the panel dispatches only the
/// <c>review-single-reviewer</c> sub-workflow (the one implementation of
/// dispatch/validate/persist). Vocabulary static, composition dynamic.</para>
/// </summary>
public class PanelReviewWorkflow : WorkflowBase
{
    public const string ReviewPanelDefinitionId = "review-panel";

    /// <summary>
    /// The static 7-role panel roster (the domain of
    /// <see cref="RolePhaseMap.GetReviewActionForRole"/>). Single source of truth:
    /// the dispatch chain, the membership gates, and the capture all iterate it.
    /// </summary>
    private static readonly AgentRole[] PanelRoles = ReviewerSelectionHelper.DocumentPanelRoster.ToArray();

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Panel Review";
        builder.DefinitionId = ReviewPanelDefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Fans out N single-reviewer runs over a policy roster and aggregates their unified Reviews into " +
            "an aggregate Review (or a typed undecidable result carrying all member reviews)";

        // ── Inputs threaded to each member + the aggregation ──
        var subjectJson = builder.WithVariable<string>("SubjectJson", "");
        var contentJson = builder.WithVariable<string>("ContentJson", "{}");
        var variablesJson = builder.WithVariable<string>("VariablesJson", "{}");
        var feedbackVariableName = builder.WithVariable<string>("FeedbackVariableName", ReviewProducerHelper.DefaultFeedbackVariable);
        var documentTypeKey = builder.WithVariable<string>("DocumentTypeKey", "");
        var issueId = builder.WithVariable<string>("IssueId", "");
        var correlationId = builder.WithVariable<string>("CorrelationId", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── Resolved panel config ──
        var rosterJson = builder.WithVariable<string>("RosterJson", "[]");
        var decisionRuleWire = builder.WithVariable<string>("DecisionRuleWire", "unanimous");
        var minimumUsableReviews = builder.WithVariable<int>("MinimumUsableReviews", 0);
        var memberCount = builder.WithVariable<int>("MemberCount", 0);

        // ── Per-role member captures (JSON). Default "{}" = not dispatched. ──
        var memberVars = PanelRoles.ToDictionary(
            r => r,
            r => builder.WithVariable<string>($"{r}MemberResultJson", "{}"));

        // ── Shared sub-workflow dispatch result ──
        var memberResult = builder.WithVariable<IDictionary<string, object>?>();

        // ── Aggregate outputs ──
        var decided = builder.WithVariable<bool>("Decided", false);
        var aggregateReviewJson = builder.WithVariable<string>("AggregateReviewJson", "");
        var aggregateEnvelopeJson = builder.WithVariable<string>("AggregateEnvelopeJson", "");
        var aggregateDocumentId = builder.WithVariable<string>("AggregateDocumentId", "");
        var aggregateProducerRole = builder.WithVariable<string>("AggregateProducerRole", "");
        var aggregateProducerAction = builder.WithVariable<string>("AggregateProducerAction", "");
        var memberReviewsJson = builder.WithVariable<string>("MemberReviewsJson", "[]");
        var undecidableReason = builder.WithVariable<string>("UndecidableReason", "");
        var succeededCount = builder.WithVariable<int>("SucceededCount", 0);

        // ================================================================
        // Init — resolve roster + rule + quorum (D11)
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Init",
            Variable = rosterJson,
            Value = new(ctx =>
            {
                var subjJson = ctx.GetInput<string>("subjectJson") ?? "";
                var rulesInput = ctx.GetInput<string>("acceptanceRulesJson") ?? "";
                var overrideRule = ctx.GetInput<string>("panelDecisionRule");

                var subject = ParseSubject(subjJson);
                var roster = ReviewerSelectionHelper.ResolvePanelRoster(rulesInput);
                var rules = string.IsNullOrWhiteSpace(rulesInput) ? AcceptanceDefaults.Rules : AcceptanceRulesJson.Deserialize(rulesInput);

                var ruleWire = !string.IsNullOrWhiteSpace(overrideRule)
                    ? overrideRule!
                    : rules.ReviewerSelection.DecisionRule.ToWire();
                var minimum = rules.ReviewerSelection.Quorum ?? roster.Count;

                contentJson.Set(ctx, ctx.GetInput<string>("contentJson") ?? "{}");
                variablesJson.Set(ctx, ctx.GetInput<string>("variablesJson") ?? "{}");
                var feedbackVar = ctx.GetInput<string>("feedbackVariableName");
                feedbackVariableName.Set(ctx, string.IsNullOrWhiteSpace(feedbackVar) ? ReviewProducerHelper.DefaultFeedbackVariable : feedbackVar!);
                documentTypeKey.Set(ctx, ctx.GetInput<string>("documentTypeKey") ?? "");
                var issue = ctx.GetInput<string>("issueId") ?? "";
                issueId.Set(ctx, issue);
                var corr = ctx.GetInput<string>("correlationId") ?? "";
                correlationId.Set(ctx, string.IsNullOrWhiteSpace(corr) ? issue : corr);
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, rulesInput);

                subjectJson.Set(ctx, JsonSerializer.Serialize(subject, DocumentJson.Options));
                decisionRuleWire.Set(ctx, ruleWire);
                minimumUsableReviews.Set(ctx, minimum);
                memberCount.Set(ctx, roster.Count);

                return JsonSerializer.Serialize(roster);
            })
        };
        init.SetDisplayText("Init");

        var emitPanelStarted = PanelEvent(
            "EmitPanelStarted", "Emit Panel Started", DocumentEvents.ReviewPanelStarted,
            issueId, correlationId, tenantId,
            ctx => $"{{\"memberCount\":{memberCount.Get(ctx)}}}", "Panel started");

        // ================================================================
        // Per-role chain: InPanel? → DispatchMember → CaptureMember → next
        // ================================================================
        var roleNodes = new List<(FlowDecision gate, DispatchWorkflow dispatch, SetVariable capture)>();
        foreach (var role in PanelRoles)
        {
            var roleWire = role.ToWire();
            var idBase = role.ToString();

            var gate = new FlowDecision(ctx => RosterContains(rosterJson.Get(ctx), roleWire))
            { Id = $"InPanel{idBase}", Name = $"{roleWire} In Panel?" };
            gate.SetDisplayText($"{roleWire} In Panel?");

            var dispatch = new DispatchWorkflow
            {
                Id = $"DispatchMember{idBase}", Name = $"Dispatch {roleWire} Review",
                WorkflowDefinitionId = new(SingleReviewerWorkflow.ReviewSingleReviewerDefinitionId),
                Input = new(ctx => new Dictionary<string, object>
                {
                    ["reviewerRole"] = roleWire,
                    ["subjectJson"] = subjectJson.Get(ctx) ?? "",
                    ["contentJson"] = contentJson.Get(ctx) ?? "{}",
                    ["variablesJson"] = variablesJson.Get(ctx) ?? "{}",
                    ["feedbackVariableName"] = feedbackVariableName.Get(ctx) ?? ReviewProducerHelper.DefaultFeedbackVariable,
                    ["documentTypeKey"] = documentTypeKey.Get(ctx) ?? "",
                    ["issueId"] = issueId.Get(ctx) ?? "",
                    ["correlationId"] = correlationId.Get(ctx) ?? "",
                    ["tenantId"] = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"] = acceptanceRulesJson.Get(ctx) ?? "",
                }),
                WaitForCompletion = new(true),
                Result = new(memberResult),
            };
            dispatch.SetDisplayText($"Dispatch {roleWire} Review");

            var capture = new SetVariable
            {
                Id = $"CaptureMember{idBase}", Name = $"Capture {roleWire} Review",
                Variable = memberVars[role],
                Value = new(ctx => (object)CaptureMember(roleWire, memberResult.Get(ctx)))
            };
            capture.SetDisplayText($"Capture {roleWire} Review");

            roleNodes.Add((gate, dispatch, capture));
        }

        // ================================================================
        // Aggregate — build PanelMembers from captures → ReviewPanelAggregation
        // ================================================================
        var aggregate = new SetVariable
        {
            Id = "AggregateResults", Name = "Aggregate Results",
            Variable = decided,
            Value = new(ctx =>
            {
                var subject = ParseSubject(subjectJson.Get(ctx));
                var roster = DeserializeRoster(rosterJson.Get(ctx));

                var members = new List<ReviewPanelAggregation.PanelMember>();
                var captures = new List<string>();
                foreach (var role in PanelRoles)
                {
                    var roleWire = role.ToWire();
                    if (!roster.Contains(roleWire)) continue;

                    var captureJson = memberVars[role].Get(ctx);
                    captures.Add(captureJson);
                    members.Add(ToPanelMember(roleWire, captureJson));
                }

                var rule = EnumWire<ReviewDecisionRule>.TryParse(decisionRuleWire.Get(ctx), out var dr)
                    ? (dr == ReviewDecisionRule.Majority ? ReviewPanelAggregation.PanelDecisionRule.Majority : ReviewPanelAggregation.PanelDecisionRule.Unanimous)
                    : ReviewPanelAggregation.PanelDecisionRule.Unanimous;

                var rules = new ReviewPanelAggregation.PanelAggregationRules(rule, minimumUsableReviews.Get(ctx));
                var result = ReviewPanelAggregation.Aggregate(members, rules, subject);

                memberReviewsJson.Set(ctx, "[" + string.Join(",", captures) + "]");
                succeededCount.Set(ctx, result.SucceededCount);

                if (result.Decided)
                {
                    aggregateReviewJson.Set(ctx, JsonSerializer.Serialize(result.Aggregate, DocumentJson.Options));
                    var firstUsable = members.First(m => m.IsUsable);
                    var spec = ReviewerSelectionHelper.Resolve(firstUsable.Role, null, subject.Kind, documentTypeKey.Get(ctx));
                    aggregateProducerRole.Set(ctx, spec.Role.ToWire());
                    aggregateProducerAction.Set(ctx, spec.Action.ToWire());
                }
                else
                {
                    undecidableReason.Set(ctx, result.Reason!.Value.ToString());
                }

                return result.Decided;
            })
        };
        aggregate.SetDisplayText("Aggregate Results");

        var decidedGate = new FlowDecision(ctx => decided.Get(ctx))
        { Id = "DecidedGate", Name = "Decided?" };
        decidedGate.SetDisplayText("Decided?");

        // ── Decided path ──
        var buildAggregateEnvelope = new SetVariable
        {
            Id = "BuildAggregateEnvelope", Name = "Build Aggregate Envelope",
            Variable = aggregateEnvelopeJson,
            Value = new(ctx =>
            {
                JsonElement payload;
                using (var doc = JsonDocument.Parse(aggregateReviewJson.Get(ctx)))
                    payload = doc.RootElement.Clone();

                var producer = DocumentProducer.Create(
                    aggregateProducerRole.Get(ctx), aggregateProducerAction.Get(ctx), ReviewPanelDefinitionId);

                // 41-1c follow-up (adversarial review 2026-07-29): the subject's
                // document id becomes the aggregate Review's ParentDocumentId
                // (39-11 D8 parent-first linkage); a diff subject yields null.
                var envelope = ReviewProducerHelper.MintReviewEnvelope(
                    ParseSubject(subjectJson.Get(ctx)), producer,
                    issueId.Get(ctx), correlationId.Get(ctx), payload, DateTimeOffset.UtcNow);

                aggregateDocumentId.Set(ctx, envelope.Id.ToString());
                return DocumentJson.Serialize(envelope);
            })
        };
        buildAggregateEnvelope.SetDisplayText("Build Aggregate Envelope");

        var emitAggregateProduced = ReviewDocEvent(
            "EmitAggregateProduced", "Emit Aggregate Produced", DocumentEvents.ProducedSuccess,
            aggregateDocumentId, issueId, correlationId, tenantId, aggregateEnvelopeJson, "Aggregate review produced");
        var emitAggregateValidated = ReviewDocEvent(
            "EmitAggregateValidated", "Emit Aggregate Validated", DocumentEvents.ValidatedSuccess,
            aggregateDocumentId, issueId, correlationId, tenantId, aggregateEnvelopeJson, "Aggregate review validated");

        var emitPanelCompleted = PanelEvent(
            "EmitPanelCompleted", "Emit Panel Completed", DocumentEvents.ReviewPanelCompleted,
            issueId, correlationId, tenantId,
            ctx => $"{{\"memberCount\":{memberCount.Get(ctx)},\"succeededCount\":{succeededCount.Get(ctx)},\"memberReviewIds\":{memberReviewsJson.Get(ctx)}}}",
            "Panel completed");

        var setOutputsDecided = new Sequence
        {
            Id = "SetOutputsDecided", Name = "Set Outputs (Decided)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutDecidedSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutDecidedReview", OutputName = new("reviewJson"), OutputValue = new(ctx => (object)(aggregateReviewJson.Get(ctx) ?? "")) }, "Output reviewJson"),
                WithLabel(new SetOutput { Id = "OutDecidedEnvelope", OutputName = new("reviewEnvelopeJson"), OutputValue = new(ctx => (object)(aggregateEnvelopeJson.Get(ctx) ?? "")) }, "Output reviewEnvelopeJson"),
                WithLabel(new SetOutput { Id = "OutDecidedDocId", OutputName = new("reviewDocumentId"), OutputValue = new(ctx => (object)(aggregateDocumentId.Get(ctx) ?? "")) }, "Output reviewDocumentId"),
                WithLabel(new SetOutput { Id = "OutDecidedMembers", OutputName = new("memberReviewsJson"), OutputValue = new(ctx => (object)(memberReviewsJson.Get(ctx) ?? "[]")) }, "Output memberReviewsJson"),
            }
        };
        setOutputsDecided.SetDisplayText("Set Outputs (Decided)");

        // ── Undecidable path ──
        var emitPanelUndecidable = PanelEvent(
            "EmitPanelUndecidable", "Emit Panel Undecidable", DocumentEvents.ReviewPanelUndecidable,
            issueId, correlationId, tenantId,
            ctx => $"{{\"reason\":\"{undecidableReason.Get(ctx)}\",\"memberCount\":{memberCount.Get(ctx)},\"succeededCount\":{succeededCount.Get(ctx)},\"memberReviewIds\":{memberReviewsJson.Get(ctx)}}}",
            "Panel undecidable");

        var setOutputsUndecidable = new Sequence
        {
            Id = "SetOutputsUndecidable", Name = "Set Outputs (Undecidable)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutUndecidableSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success"),
                WithLabel(new SetOutput { Id = "OutUndecidableReason", OutputName = new("undecidableReason"), OutputValue = new(ctx => (object)(undecidableReason.Get(ctx) ?? "")) }, "Output undecidableReason"),
                WithLabel(new SetOutput { Id = "OutUndecidableMembers", OutputName = new("memberReviewsJson"), OutputValue = new(ctx => (object)(memberReviewsJson.Get(ctx) ?? "[]")) }, "Output memberReviewsJson"),
            }
        };
        setOutputsUndecidable.SetDisplayText("Set Outputs (Undecidable)");

        var finish = new Finish { Id = "Finish", Name = "Finish" };
        finish.SetDisplayText("Finish");

        // ================================================================
        // Flowchart
        // ================================================================
        var activities = new List<IActivity> { init, emitPanelStarted };
        foreach (var (gate, dispatch, capture) in roleNodes)
        {
            activities.Add(gate);
            activities.Add(dispatch);
            activities.Add(capture);
        }
        activities.Add(aggregate);
        activities.Add(decidedGate);
        activities.Add(buildAggregateEnvelope);
        activities.Add(emitAggregateProduced);
        activities.Add(emitAggregateValidated);
        activities.Add(emitPanelCompleted);
        activities.Add(setOutputsDecided);
        activities.Add(emitPanelUndecidable);
        activities.Add(setOutputsUndecidable);
        activities.Add(finish);

        var connections = new List<FlowConnection>
        {
            Connect(init, emitPanelStarted),
            Connect(emitPanelStarted, roleNodes[0].gate),
        };

        for (var i = 0; i < roleNodes.Count; i++)
        {
            var (gate, dispatch, capture) = roleNodes[i];
            var next = i + 1 < roleNodes.Count ? (IActivity)roleNodes[i + 1].gate : aggregate;

            connections.Add(ConnectOutcome(gate, "True", dispatch));
            connections.Add(Connect(dispatch, capture));
            connections.Add(Connect(capture, next));
            connections.Add(ConnectOutcome(gate, "False", next));   // skipped role
        }

        connections.Add(Connect(aggregate, decidedGate));

        connections.Add(ConnectOutcome(decidedGate, "True", buildAggregateEnvelope));
        connections.Add(Connect(buildAggregateEnvelope, emitAggregateProduced));
        connections.Add(Connect(emitAggregateProduced, emitAggregateValidated));
        connections.Add(Connect(emitAggregateValidated, emitPanelCompleted));
        connections.Add(Connect(emitPanelCompleted, setOutputsDecided));
        connections.Add(Connect(setOutputsDecided, finish));

        connections.Add(ConnectOutcome(decidedGate, "False", emitPanelUndecidable));
        connections.Add(Connect(emitPanelUndecidable, setOutputsUndecidable));
        connections.Add(Connect(setOutputsUndecidable, finish));

        builder.Root = new Flowchart
        {
            Id = "PanelReviewFlowchart",
            Start = init,
            Activities = activities,
            Connections = connections,
        };
    }

    // ====================================================================
    // Node factories
    // ====================================================================

    private static EmitDocumentEventActivity PanelEvent(
        string id, string name, string eventType,
        Variable<string> issueId, Variable<string> corr, Variable<string> tenant,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> dataJson, string detail)
    {
        var e = new EmitDocumentEventActivity
        {
            Id = id, Name = name,
            EventType = new(eventType),
            DocumentType = new("review"),
            Round = new(0),
            IssueId = new(ctx => issueId.Get(ctx)),
            CorrelationId = new(ctx => corr.Get(ctx)),
            TenantId = new(ctx => tenant.Get(ctx)),
            Detail = new(detail),
            DataJson = new(ctx => dataJson(ctx)),
        };
        e.SetDisplayText(name);
        return e;
    }

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

    private static bool RosterContains(string? rosterJson, string roleWire)
        => DeserializeRoster(rosterJson).Contains(roleWire);

    private static HashSet<string> DeserializeRoster(string? rosterJson)
    {
        if (string.IsNullOrWhiteSpace(rosterJson)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(rosterJson!) ?? new List<string>())
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Build a member-capture JSON blob from a single-reviewer dispatch result.</summary>
    private static string CaptureMember(string roleWire, IDictionary<string, object>? result)
    {
        var ok = result != null && result.TryGetValue("success", out var s)
            && (s is true || (s is string str && bool.TryParse(str, out var b) && b));
        var reviewJson = result != null && result.TryGetValue("reviewJson", out var rj) ? rj?.ToString() ?? "" : "";
        var reviewDocId = result != null && result.TryGetValue("reviewDocumentId", out var rd) ? rd?.ToString() ?? "" : "";
        var failureKind = result != null && result.TryGetValue("failureKind", out var fk) ? fk?.ToString() ?? "" : "";

        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"role\":").Append(JsonSerializer.Serialize(roleWire)).Append(',');
        sb.Append("\"ok\":").Append(ok ? "true" : "false").Append(',');
        sb.Append("\"reviewDocumentId\":").Append(JsonSerializer.Serialize(reviewDocId)).Append(',');
        sb.Append("\"failureKind\":").Append(JsonSerializer.Serialize(failureKind)).Append(',');
        sb.Append("\"reviewJson\":").Append(JsonSerializer.Serialize(reviewJson));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Parse a member-capture blob into a <see cref="ReviewPanelAggregation.PanelMember"/>.</summary>
    private static ReviewPanelAggregation.PanelMember ToPanelMember(string roleWire, string captureJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(captureJson);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var reviewJson = root.TryGetProperty("reviewJson", out var rjEl) && rjEl.ValueKind == JsonValueKind.String
                ? rjEl.GetString() ?? "" : "";
            var reviewDocId = root.TryGetProperty("reviewDocumentId", out var rdEl) && rdEl.ValueKind == JsonValueKind.String
                ? rdEl.GetString() ?? "" : "";
            var failureKind = root.TryGetProperty("failureKind", out var fkEl) && fkEl.ValueKind == JsonValueKind.String
                ? fkEl.GetString() : null;

            Review? review = null;
            if (ok && !string.IsNullOrWhiteSpace(reviewJson))
            {
                try { review = JsonSerializer.Deserialize<Review>(reviewJson, DocumentJson.Options); }
                catch (JsonException) { review = null; }
            }

            Guid? docId = Guid.TryParse(reviewDocId, out var g) ? g : null;
            return new ReviewPanelAggregation.PanelMember(roleWire, docId, review, ok && review is not null, string.IsNullOrWhiteSpace(failureKind) ? null : failureKind);
        }
        catch (JsonException)
        {
            return new ReviewPanelAggregation.PanelMember(roleWire, null, null, false, "capture-parse-failed");
        }
    }

    private static ReviewSubject ParseSubject(string? subjectJson)
    {
        if (string.IsNullOrWhiteSpace(subjectJson))
            throw new Tamma.Core.TammaError(
                "REVIEW.PRODUCER.SUBJECT_MISSING",
                "The panel producer requires a parseable ReviewSubject (subjectJson).",
                retryable: false,
                severity: Tamma.Core.TammaErrorSeverity.High);
        try
        {
            var subject = JsonSerializer.Deserialize<ReviewSubject>(subjectJson!, DocumentJson.Options);
            if (subject is not null) return subject;
        }
        catch (JsonException) { /* fall through */ }
        throw new Tamma.Core.TammaError(
            "REVIEW.PRODUCER.SUBJECT_MISSING",
            "The panel producer requires a parseable ReviewSubject (subjectJson).",
            retryable: false,
            severity: Tamma.Core.TammaErrorSeverity.High);
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
