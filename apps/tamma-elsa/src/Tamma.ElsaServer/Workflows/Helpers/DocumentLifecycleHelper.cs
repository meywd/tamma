using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Resume;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-6 (Design Decision D1) — the PURE, Elsa-free decision core of the
/// generic <c>document-lifecycle</c> workflow. The Elsa graph only ROUTES; every
/// stage transition, round/repair accounting, violation-feedback composition,
/// ambiguity-threshold check, and outcome/lineage assembly lives here so the
/// fail-closed behaviour is unit-testable without a workflow runtime (the
/// <see cref="TriagePoDecisionHelper"/> / <see cref="ReviewAggregationHelper"/>
/// precedent).
///
/// <para>All loop state lives in a single serializable <see cref="LifecycleState"/>
/// record held in ONE workflow variable as JSON (<see cref="DocumentJson.Options"/>),
/// which Elsa persists across suspend/restart — exactly what 39-10 needs. Every
/// function is TOTAL: unparseable input yields a typed failure result or a
/// conservative branch, never a throw out of a routing lambda (the exception is
/// <see cref="ValidateProducerSpec"/> / <see cref="Init"/>, which fail LOUD at
/// Init per D2 before any loop state exists).</para>
/// </summary>
public static class DocumentLifecycleHelper
{
    /// <summary>The default feedback variable a producer template declares (D11).</summary>
    public const string DefaultFeedbackVariable = "revisionNotes";

    /// <summary>The workflow definition id the lifecycle dispatches to produce a draft.</summary>
    public const string ProducerWorkflowDefinitionId = "llm-call";

    // ====================================================================
    // Serializable loop state (D1)
    // ====================================================================

    /// <summary>
    /// The entire lifecycle loop state (D1). Serializes to ONE workflow variable.
    /// Drafts/Reviews hold the FULL envelopes (D9 — the accept request + lineage are
    /// built from them); the audit-facing <see cref="DocumentLineage"/> projects
    /// them to id+state on exit.
    /// </summary>
    public sealed record LifecycleState
    {
        [JsonPropertyName("typeKey")] public required string TypeKey { get; init; }
        [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
        [JsonPropertyName("issueId")] public required string IssueId { get; init; }
        [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
        [JsonPropertyName("sessionId")] public required Guid SessionId { get; init; }
        [JsonPropertyName("producerRole")] public required string ProducerRole { get; init; }
        [JsonPropertyName("producerAction")] public required string ProducerAction { get; init; }
        [JsonPropertyName("producerVariablesJson")] public required string ProducerVariablesJson { get; init; }
        [JsonPropertyName("feedbackVariableName")] public required string FeedbackVariableName { get; init; }
        [JsonPropertyName("round")] public int Round { get; init; }
        [JsonPropertyName("repairAttempts")] public int RepairAttempts { get; init; }
        [JsonPropertyName("ambiguityScore")] public double? AmbiguityScore { get; init; }
        [JsonPropertyName("rules")] public required ResolvedAcceptanceRules Rules { get; init; }
        [JsonPropertyName("drafts")] public IReadOnlyList<DocumentEnvelope> Drafts { get; init; } = Array.Empty<DocumentEnvelope>();
        [JsonPropertyName("reviews")] public IReadOnlyList<DocumentEnvelope> Reviews { get; init; } = Array.Empty<DocumentEnvelope>();
        [JsonPropertyName("lastViolations")] public IReadOnlyList<DocumentViolation> LastViolations { get; init; } = Array.Empty<DocumentViolation>();

        /// <summary>The current (latest) document envelope, or null before the first produce.</summary>
        public DocumentEnvelope? Current => Drafts.Count == 0 ? null : Drafts[^1];

        /// <summary>The rules reference the decision is made under (<c>source@version</c>).</summary>
        public string RulesReference => $"{Rules.Source.ToWire()}@{Rules.Version}";
    }

    // ====================================================================
    // Serialization
    // ====================================================================

    /// <summary>Serialize the state to the ONE workflow variable JSON.</summary>
    public static string Serialize(LifecycleState state) =>
        JsonSerializer.Serialize(state, DocumentJson.Options);

    /// <summary>Deserialize the state from the workflow variable JSON.</summary>
    /// <exception cref="TammaError">Code <c>DOCUMENT.LIFECYCLE.STATE_CORRUPT</c> on unparseable state.</exception>
    public static LifecycleState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw StateCorrupt("empty state json");
        try
        {
            return JsonSerializer.Deserialize<LifecycleState>(json, DocumentJson.Options)
                   ?? throw StateCorrupt("state json deserialized to null");
        }
        catch (JsonException ex)
        {
            throw StateCorrupt(ex.Message);
        }
    }

    // ====================================================================
    // D2 — producer spec validation (fail-loud at Init)
    // ====================================================================

    /// <summary>
    /// Validate the producer dispatch spec (D2). Reuses <see cref="DocumentProducer"/>
    /// (which parses role/action via the agent taxonomy and asserts
    /// <c>RolePhaseMap.IsRoleEligibleForPhase(action, role)</c>) and resolves the
    /// document type fail-loud through <see cref="DocumentTypeRegistry"/>. Any failure
    /// is rewrapped as <c>DOCUMENT.LIFECYCLE.INVALID_PRODUCER</c>.
    /// </summary>
    /// <exception cref="TammaError">Code <c>DOCUMENT.LIFECYCLE.INVALID_PRODUCER</c>.</exception>
    public static void ValidateProducerSpec(string role, string action, string typeKey)
    {
        try
        {
            // Reuse D2's encapsulation: parses role/action + asserts eligibility.
            _ = DocumentProducer.Create(role, action, ProducerWorkflowDefinitionId);
            // Fail-loud type-key resolution (AC1).
            _ = DocumentTypeRegistry.Resolve(typeKey);
        }
        catch (TammaError ex)
        {
            throw new TammaError(
                "DOCUMENT.LIFECYCLE.INVALID_PRODUCER",
                $"Invalid producer spec (role='{role}', action='{action}', type='{typeKey}'): {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["action"] = action,
                    ["typeKey"] = typeKey,
                    ["cause"] = ex.Code,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    // ====================================================================
    // Init
    // ====================================================================

    /// <summary>
    /// Build the initial <see cref="LifecycleState"/> from the workflow inputs. Runs
    /// <see cref="ValidateProducerSpec"/> FIRST (fail-loud, D2), then seeds an empty
    /// draft/review/round state.
    /// </summary>
    /// <exception cref="TammaError">Code <c>DOCUMENT.LIFECYCLE.INVALID_PRODUCER</c>.</exception>
    public static LifecycleState Init(
        string role,
        string action,
        string variablesJson,
        string typeKey,
        string issueId,
        string correlationId,
        Guid sessionId,
        string feedbackVariableName,
        double? ambiguityScore,
        ResolvedAcceptanceRules rules)
    {
        ValidateProducerSpec(role, action, typeKey);

        var schemaVersion = DocumentTypeRegistry.Resolve(typeKey).SchemaVersion;

        return new LifecycleState
        {
            TypeKey = typeKey,
            SchemaVersion = schemaVersion,
            IssueId = issueId,
            CorrelationId = correlationId,
            SessionId = sessionId,
            ProducerRole = role,
            ProducerAction = action,
            ProducerVariablesJson = string.IsNullOrWhiteSpace(variablesJson) ? "{}" : variablesJson,
            FeedbackVariableName = string.IsNullOrWhiteSpace(feedbackVariableName)
                ? DefaultFeedbackVariable
                : feedbackVariableName,
            Round = 0,
            RepairAttempts = 0,
            AmbiguityScore = ambiguityScore,
            Rules = rules,
        };
    }

    /// <summary>
    /// Resolve the effective rules from the input JSON, falling back to the per-type
    /// static defaults on an empty input (D4 — a bare dispatch is safe). Wraps the
    /// body in a <see cref="ResolvedAcceptanceRules"/> stamped <c>SystemDefault</c>.
    /// </summary>
    public static ResolvedAcceptanceRules ResolveRules(string? rulesJson, string typeKey, DateTimeOffset now)
    {
        var key = DocumentTypeKeyExtensions.Parse(typeKey);
        AcceptanceRules rules;
        AcceptanceRulesSource source;
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            rules = AcceptanceDefaults.For(key);
            source = AcceptanceRulesSource.SystemDefault;
        }
        else
        {
            rules = AcceptanceRulesJson.Deserialize(rulesJson);
            // The engine never resolves per-principal storage itself (D4); a supplied
            // body is treated as the server-resolved effective rules.
            source = AcceptanceRulesSource.PrincipalDefault;
        }

        return new ResolvedAcceptanceRules(rules, source, Version: 1, DocumentTypeKey: key.ToWire(), ResolvedAt: now);
    }

    // ====================================================================
    // Envelope minting + transitions (D9)
    // ====================================================================

    /// <summary>
    /// Mint a fresh Draft envelope for a produce/repair/revise turn. On a revise turn
    /// (<paramref name="supersedes"/> non-null) it records the supersedes chain — a
    /// revision never rewinds (39-2 D4).
    /// </summary>
    public static DocumentEnvelope MintDraft(
        LifecycleState state, JsonElement payload, DocumentProducer producer, Guid? supersedes, DateTimeOffset now)
        => DocumentEnvelope.CreateDraft(
            DocumentTypeKeyExtensions.Parse(state.TypeKey),
            state.SchemaVersion,
            state.IssueId,
            state.CorrelationId,
            producer,
            payload,
            supersedesDocumentId: supersedes,
            now: now);

    /// <summary>
    /// Apply a legal state transition to an envelope through the 39-2
    /// <see cref="DocumentEnvelope.WithState"/> seam (D9). Exposed so the AC6 negative
    /// pin can drive an illegal transition and assert
    /// <c>DOCUMENT.STATE.ILLEGAL_TRANSITION</c> (loud, never a silent overwrite).
    /// </summary>
    /// <exception cref="TammaError">Code <c>DOCUMENT.STATE.ILLEGAL_TRANSITION</c>.</exception>
    public static DocumentEnvelope ApplyTransition(DocumentEnvelope envelope, DocumentState next, DateTimeOffset now)
        => envelope.WithState(next, now);

    /// <summary>Replace the current (latest) draft with a transitioned copy.</summary>
    public static LifecycleState TransitionCurrent(LifecycleState state, DocumentState next, DateTimeOffset now)
    {
        if (state.Current is null) return state;
        var drafts = state.Drafts.ToList();
        drafts[^1] = ApplyTransition(drafts[^1], next, now);
        return state with { Drafts = drafts };
    }

    /// <summary>Append a freshly minted draft envelope to the lineage.</summary>
    public static LifecycleState AppendDraft(LifecycleState state, DocumentEnvelope envelope)
    {
        var drafts = state.Drafts.ToList();
        drafts.Add(envelope);
        return state with { Drafts = drafts };
    }

    /// <summary>Append a review envelope to the lineage.</summary>
    public static LifecycleState AppendReview(LifecycleState state, DocumentEnvelope review)
    {
        var reviews = state.Reviews.ToList();
        reviews.Add(review);
        return state with { Reviews = reviews };
    }

    /// <summary>Record the last validation violations (empty on a pass).</summary>
    public static LifecycleState WithViolations(LifecycleState state, IReadOnlyList<DocumentViolation> violations)
        => state with { LastViolations = violations.ToList() };

    /// <summary>Consume one validation-repair attempt.</summary>
    public static LifecycleState IncrementRepairAttempts(LifecycleState state)
        => state with { RepairAttempts = state.RepairAttempts + 1 };

    /// <summary>Consume one revision round.</summary>
    public static LifecycleState IncrementRound(LifecycleState state)
        => state with { Round = state.Round + 1 };

    // ====================================================================
    // Bounds (D — provably bounded loops)
    // ====================================================================

    /// <summary>Whether another validation-repair turn is within the rules budget.</summary>
    public static bool ShouldRepair(LifecycleState state)
        => state.RepairAttempts < state.Rules.Rules.MaxValidationRepairAttempts;

    /// <summary>Whether another revision round is within the rules budget.</summary>
    public static bool ShouldRevise(LifecycleState state)
        => state.Round < state.Rules.Rules.MaxRevisionRounds;

    // ====================================================================
    // D8 — ambiguity threshold
    // ====================================================================

    /// <summary>
    /// D8 — post-VALIDATE ambiguity check. For an <c>ambiguity-assessment</c> payload,
    /// reads its <c>score</c>; for any type, an optional threaded
    /// <paramref name="inputScore"/> is also considered. A score AT OR ABOVE the
    /// rules threshold escalates before REVIEW.
    /// </summary>
    public static bool IsAmbiguityAboveThreshold(string typeKey, string? payloadJson, AcceptanceRules rules, double? inputScore)
    {
        var threshold = rules.AmbiguityEscalationThreshold;

        if (inputScore is { } threaded && threaded >= threshold)
            return true;

        if (string.Equals(typeKey, DocumentTypeKey.AmbiguityAssessment.ToWire(), StringComparison.Ordinal)
            && TryReadAmbiguityScore(payloadJson, out var score)
            && score >= threshold)
            return true;

        return false;
    }

    private static bool TryReadAmbiguityScore(string? payloadJson, out double score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("score", out var s) &&
                s.ValueKind == JsonValueKind.Number)
            {
                score = s.GetDouble();
                return true;
            }
        }
        catch (JsonException)
        {
            // unreadable payload → no threaded score (validation already catches malformed payloads)
        }
        return false;
    }

    // ====================================================================
    // D11 — feedback composition (into the DECLARED feedback variable only)
    // ====================================================================

    /// <summary>
    /// Fold domain-phrased validation violations into the producer's declared
    /// feedback variable for a repair turn (D11). Byte-identical passthrough when
    /// there are no violations (the <see cref="ValidationFeedbackHelper"/> contract).
    /// </summary>
    public static string BuildRepairVariables(
        string variablesJson, IReadOnlyList<DocumentViolation> violations, string feedbackVariable)
    {
        if (violations is null || violations.Count == 0)
            return NormalizeVariables(variablesJson);

        var joined = string.Join("; ", violations.Select(v => v.Message));
        return AppendToFeedbackVariables(variablesJson, feedbackVariable, joined);
    }

    /// <summary>
    /// Fold the review's summary + issues (severity / category / suggested fix) into
    /// the producer's declared feedback variable AND the canonical
    /// <c>revisionNotes</c> variable for a revise turn (D11).
    /// </summary>
    public static string BuildRevisionVariables(string variablesJson, string? reviewJson, string feedbackVariable)
    {
        var notes = ComposeReviewNotes(reviewJson);
        if (string.IsNullOrWhiteSpace(notes))
            return NormalizeVariables(variablesJson);

        // Always thread the canonical revisionNotes AND the spec's designated variable.
        var withCanonical = AppendToFeedbackVariables(variablesJson, DefaultFeedbackVariable, notes);
        return string.Equals(feedbackVariable, DefaultFeedbackVariable, StringComparison.Ordinal)
            ? withCanonical
            : AppendToFeedbackVariables(withCanonical, feedbackVariable, notes);
    }

    private static string ComposeReviewNotes(string? reviewJson)
    {
        if (string.IsNullOrWhiteSpace(reviewJson)) return string.Empty;
        try
        {
            var review = JsonSerializer.Deserialize<Review>(reviewJson, DocumentJson.Options);
            if (review is null) return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(review.Summary)) parts.Add(review.Summary);
            foreach (var issue in review.Issues ?? Array.Empty<ReviewIssue>())
            {
                parts.Add($"[{issue.Severity.ToWire()}/{issue.Category}] {issue.Description} — fix: {issue.SuggestedFix}");
            }
            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string AppendToFeedbackVariables(string variablesJson, string feedbackVariable, string feedback)
    {
        var vars = ParseVariables(variablesJson);
        var existing = vars.TryGetValue(feedbackVariable, out var e) ? e?.ToString() : string.Empty;
        vars[feedbackVariable] = ValidationFeedbackHelper.AppendFeedback(existing, feedback);
        return JsonSerializer.Serialize(vars);
    }

    private static Dictionary<string, object?> ParseVariables(string? variablesJson)
    {
        if (string.IsNullOrWhiteSpace(variablesJson)) return new Dictionary<string, object?>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static string NormalizeVariables(string? variablesJson)
        => JsonSerializer.Serialize(ParseVariables(variablesJson));

    // ====================================================================
    // Review facts + routing
    // ====================================================================

    /// <summary>The routing signal computed from a landed review (post-VALIDATE).</summary>
    public enum ReviewRoute { Accept, Revise, RoundsExhausted, Undecidable }

    /// <summary>Facts a landed review contributes (39-5 guardrail input + routing).</summary>
    public sealed record ReviewFactsResult(bool Usable, ReviewDecision Decision, bool HasBlockingIssues);

    /// <summary>
    /// Extract the guardrail-relevant facts from a review payload. An unparseable /
    /// invalid review is NOT usable (fail-closed) — the lifecycle routes it to
    /// <c>ReviewUndecidable</c> rather than guessing a verdict.
    /// </summary>
    public static ReviewFactsResult ExtractReviewFacts(string? reviewJson)
    {
        if (string.IsNullOrWhiteSpace(reviewJson))
            return new ReviewFactsResult(false, ReviewDecision.NeedsDiscussion, false);
        try
        {
            var review = JsonSerializer.Deserialize<Review>(reviewJson, DocumentJson.Options);
            if (review is null)
                return new ReviewFactsResult(false, ReviewDecision.NeedsDiscussion, false);

            var hasBlocking = (review.Issues ?? Array.Empty<ReviewIssue>()).Any(i => i.Severity.IsBlocking());
            return new ReviewFactsResult(true, review.Decision, hasBlocking);
        }
        catch (JsonException)
        {
            return new ReviewFactsResult(false, ReviewDecision.NeedsDiscussion, false);
        }
    }

    /// <summary>
    /// Route a landed review: <c>Approve</c> → ACCEPT; <c>RequestChanges</c> /
    /// <c>NeedsDiscussion</c> → revise if within the round budget, else
    /// rounds-exhausted; an unusable review → undecidable.
    /// </summary>
    public static ReviewRoute ComputeReviewRoute(LifecycleState state, string? reviewJson)
    {
        var facts = ExtractReviewFacts(reviewJson);
        if (!facts.Usable) return ReviewRoute.Undecidable;
        if (facts.Decision == ReviewDecision.Approve) return ReviewRoute.Accept;
        return ShouldRevise(state) ? ReviewRoute.Revise : ReviewRoute.RoundsExhausted;
    }

    // ====================================================================
    // D7 — outcome + lineage assembly
    // ====================================================================

    /// <summary>Build the accepted terminal result (Status=accepted, Outcome=null).</summary>
    public static DocumentLifecycleResult BuildAccepted(LifecycleState state, Guid docId)
        => new(DocumentLifecycleResult.StatusAccepted, null, docId, BuildLineage(state));

    /// <summary>Build the rejected terminal result (Status=rejected, Outcome=null — a first-class terminal).</summary>
    public static DocumentLifecycleResult BuildRejected(LifecycleState state, Guid docId)
        => new(DocumentLifecycleResult.StatusRejected, null, docId, BuildLineage(state));

    /// <summary>Build an escalated terminal result carrying the typed outcome (D7).</summary>
    public static DocumentLifecycleResult BuildOutcome(LifecycleState state, DocumentLifecycleOutcome outcome)
        => new(DocumentLifecycleResult.StatusEscalated, outcome, state.Current?.Id, BuildLineage(state));

    /// <summary>
    /// Map an accept-stage <see cref="AcceptanceEscalationReason"/> onto a terminal
    /// <see cref="DocumentLifecycleOutcome"/> for <see cref="BuildOutcome"/>. The two
    /// reasons with a 1:1 mapping ride the 39-5 drift-pinned
    /// <c>ToLifecycleOutcome</c>; the four escalation-only reasons
    /// (blocking-review / always-escalate / acceptor-judgment / reject-requires-human)
    /// have no terminal state of their own and collapse to <c>ReviewUndecidable</c>
    /// (the acceptor could not settle the review), keeping <c>Outcome</c> non-null on
    /// every escalated exit (D7).
    /// </summary>
    public static DocumentLifecycleOutcome OutcomeForEscalationReason(AcceptanceEscalationReason reason)
        => reason.ToLifecycleOutcome() ?? DocumentLifecycleOutcome.ReviewUndecidable;

    /// <summary>Project the state into the audit-facing lineage (D7).</summary>
    public static DocumentLineage BuildLineage(LifecycleState state)
    {
        var drafts = state.Drafts.Select(d => new DraftRef(d.Id, d.State.ToWire())).ToList();
        var reviewIds = state.Reviews.Select(r => r.Id).ToList();
        return new DocumentLineage(
            Drafts: drafts,
            ReviewIds: reviewIds,
            RoundsUsed: state.Round,
            RepairAttemptsUsed: state.RepairAttempts,
            LastViolations: state.LastViolations,
            RulesReference: state.RulesReference);
    }

    // ====================================================================
    // Story 39-10 (D6/D10) — crash re-entry guards
    // ====================================================================

    /// <summary>
    /// The tolerant read-back of a re-entry position payload (D10). Mirrors the
    /// <c>ClarifyResumeReadBackTests</c> matrix: the position JSON may arrive as a
    /// <see cref="string"/> or a <see cref="JsonElement"/> (in-process vs serializing
    /// runtime), and every boolean flag coerces via <see cref="ResumeInput.AsBool"/>
    /// (never <c>is true</c>). A missing/garbage payload fail-closes to a fresh Produce.
    /// </summary>
    public sealed record ReEntryReadResult(
        LifecycleResumePosition? Position, bool SkipProduce, bool SkipReview, bool ShortCircuitAccepted)
    {
        public static readonly ReEntryReadResult Fresh = new(null, false, false, false);

        /// <summary>The coarse resume stage (Produce when there is no usable position).</summary>
        public LifecycleResumeStage Stage => Position?.ResumeAt ?? LifecycleResumeStage.Produce;
    }

    /// <summary>Deserialize a re-entry position from its JSON form (null on empty/garbage).</summary>
    public static LifecycleResumePosition? DeserializeReEntryPosition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<LifecycleResumePosition>(json, DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read a re-entry position + derived skip flags from a resume/output dictionary,
    /// tolerant of serialization (D10). When <c>PositionJson</c> is present the flags are
    /// DERIVED from the position; otherwise explicit boolean flags
    /// (<c>SkipProduce</c>/<c>SkipReview</c>/<c>ShortCircuit</c>) are read via
    /// <see cref="ResumeInput.AsBool"/>. Missing everything → fresh Produce (fail-closed).
    /// </summary>
    public static ReEntryReadResult ReadReEntryPosition(IDictionary<string, object>? input)
    {
        if (input is null) return ReEntryReadResult.Fresh;

        LifecycleResumePosition? position = null;
        if (input.TryGetValue("PositionJson", out var raw))
            position = DeserializeReEntryPosition(CoerceJson(raw));

        if (position is not null)
            return new ReEntryReadResult(
                position, ShouldSkipProduce(position), ShouldSkipReview(position), ShouldShortCircuitAccepted(position));

        return new ReEntryReadResult(
            null, ReadFlag(input, "SkipProduce"), ReadFlag(input, "SkipReview"), ReadFlag(input, "ShortCircuit"));
    }

    private static bool ReadFlag(IDictionary<string, object> input, string key)
        => input.TryGetValue(key, out var v) && ResumeInput.AsBool(v);

    private static string? CoerceJson(object? raw) => raw switch
    {
        null => null,
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
        _ => raw.ToString(),
    };

    /// <summary>Re-entry skips the produce+validate stage for any non-Produce position.</summary>
    public static bool ShouldSkipProduce(LifecycleResumePosition? position)
        => position is not null && position.ResumeAt
            is LifecycleResumeStage.Review or LifecycleResumeStage.Accept or LifecycleResumeStage.Complete;

    /// <summary>Re-entry skips the review stage once past it (Accept/Complete).</summary>
    public static bool ShouldSkipReview(LifecycleResumePosition? position)
        => position is not null && position.ResumeAt
            is LifecycleResumeStage.Accept or LifecycleResumeStage.Complete;

    /// <summary>An already-accepted document short-circuits to the accepted terminal (no re-emit).</summary>
    public static bool ShouldShortCircuitAccepted(LifecycleResumePosition? position)
        => position is not null && position.ResumeAt == LifecycleResumeStage.Complete;

    /// <summary>
    /// Fold a reconstructed re-entry position into the freshly-seeded loop state (D6). A
    /// Produce position (or a missing existing body) is a passthrough — today's behaviour.
    /// A skip-produce position appends the stored revision as the current draft (in its
    /// stored state) so the guarded stage reviews/accepts it instead of re-producing;
    /// an Accept position additionally synthesizes a recovered review envelope so the
    /// acceptance request can be rebuilt.
    /// </summary>
    public static LifecycleState ApplyReEntry(
        LifecycleState state, LifecycleResumePosition position, DocumentEnvelope? existing)
    {
        if (position.ResumeAt == LifecycleResumeStage.Produce || existing is null)
            return state;

        var round = Math.Max(0, (position.ExistingRevision ?? 1) - 1);
        var withDraft = AppendDraft(state, existing) with { Round = round };

        if (position.ResumeAt == LifecycleResumeStage.Accept)
        {
            // Fresh-dispatch Accept re-entry needs a review envelope for the acceptance
            // request. (The surviving-bookmark AC8 path resumes the live gate directly and
            // does NOT pass through here.) Synthesize a minimal recovered review.
            var reviewer = new DocumentProducer
            {
                Role = state.ProducerRole,
                Action = state.ProducerAction,
                WorkflowDefinitionId = "document-review",
            };
            using var doc = JsonDocument.Parse(
                "{\"decision\":\"approve\",\"summary\":\"recovered on re-entry\",\"issues\":[]}");
            var review = DocumentEnvelope.CreateDraft(
                DocumentTypeKey.Review, 1, state.IssueId,
                string.IsNullOrWhiteSpace(state.CorrelationId) ? state.IssueId : state.CorrelationId,
                reviewer, doc.RootElement, now: DateTimeOffset.UtcNow);
            withDraft = AppendReview(withDraft, review);
        }

        return withDraft;
    }

    // ====================================================================

    private static TammaError StateCorrupt(string detail) => new(
        "DOCUMENT.LIFECYCLE.STATE_CORRUPT",
        $"The document-lifecycle state could not be read: {detail}.",
        retryable: false,
        severity: TammaErrorSeverity.High);
}
