using System.Text.Json;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-7 (Design Decisions D4/D5) — the PURE reviewer-reply mapper + repair
/// helpers for the single-reviewer producer. Maps an <c>llm-call</c> reply onto the
/// unified 39-4 <see cref="Review"/> — canonical shape first, the CURRENT reviewer
/// cell shape (top-level <c>issues[]</c> + a <c>verdict</c> half) second, and
/// GARBAGE into typed <see cref="DocumentViolation"/>s, never a defaulted
/// <c>"concerns"</c> review (the <c>PlanReviewWorkflow.ExtractReview</c> anti-pattern
/// AC1 kills). Every mapped payload is validated by
/// <see cref="ReviewDocumentType"/> so blocking-issues⇒not-approvable is enforced.
///
/// <para>No Elsa runtime dependency; same fail-closed posture as
/// the legacy plan-review aggregation — but this SUPERSEDES its parsing half.</para>
/// </summary>
public static class ReviewProducerHelper
{
    /// <summary>Violation code for a reply that could not be mapped to a review at all.</summary>
    public const string UnparseableReply = "REVIEW.PRODUCER.UNPARSEABLE_REPLY";

    /// <summary>
    /// The outcome of <see cref="MapReviewerReply"/>: a VALID review payload, OR a
    /// non-empty violation list (unparseable OR invalid). <see cref="Payload"/> is
    /// non-null IFF the mapped review passed <see cref="ReviewDocumentType"/>
    /// validation — an invalid or unparseable reply carries violations and a null
    /// payload, never a laundered review.
    /// </summary>
    public sealed record MapResult(Review? Payload, IReadOnlyList<DocumentViolation> Violations)
    {
        public bool IsValid => Payload is not null && Violations.Count == 0;
    }

    /// <summary>
    /// Story 41-1c follow-up (adversarial review 2026-07-29) — the parent-linkage
    /// rule for a producer-minted Review envelope: a <c>document</c> subject's id is
    /// the Review's <c>ParentDocumentId</c> (the 39-11 D8 parent-first linkage, so
    /// lineage never has to fall back to the body probe); a <c>diff</c> subject has
    /// no parent document (code is not a document type) — null.
    /// </summary>
    public static Guid? ParentDocumentIdFor(ReviewSubject subject) =>
        string.Equals(subject.Kind, ReviewerSelectionHelper.DocumentSubjectKind, StringComparison.Ordinal)
            ? subject.DocumentId
            : null;

    /// <summary>
    /// The ONE envelope-mint site for the 39-7 review producers (single-reviewer and
    /// panel aggregate). Mints a Validated <c>Review</c> envelope whose
    /// <c>ParentDocumentId</c> comes from <paramref name="subject"/> via
    /// <see cref="ParentDocumentIdFor"/>.
    /// </summary>
    public static DocumentEnvelope MintReviewEnvelope(
        ReviewSubject subject, DocumentProducer producer,
        string issueId, string correlationId, JsonElement payload, DateTimeOffset now)
        => DocumentEnvelope.CreateDraft(
                DocumentTypeKey.Review, 1, issueId, correlationId, producer, payload,
                parentDocumentId: ParentDocumentIdFor(subject),
                now: now)
            .WithState(DocumentState.Validated, now);

    /// <summary>
    /// Map an <c>llm-call</c> reviewer reply onto a validated unified
    /// <see cref="Review"/> (D4). Order: (1) canonical <see cref="Review"/> JSON;
    /// (2) the legacy cell shape — top-level <c>issues[]</c> folded with the
    /// <c>verdict</c> half via <see cref="Review.FromLegacyVerdictJson"/>; (3)
    /// anything else → violations. The caller's <paramref name="subject"/> is
    /// authoritative and overrides any subject in the reply. The mapped review is
    /// then validated — a legacy issue with no suggested fix deserializes but FAILS
    /// validation (routed to repair, not laundered).
    /// </summary>
    public static MapResult MapReviewerReply(string? llmResponse, ReviewSubject subject)
    {
        var json = ExtractJsonObject(llmResponse);
        if (json is null)
            return Fail("The reviewer reply contained no parseable JSON object.");

        // (1) Canonical Review first. A legacy reply lacks the required
        //     subject/decision/summary/issues members, so it throws here and falls
        //     through to the legacy path — it never masquerades as canonical.
        Review? candidate = TryDeserializeCanonical(json);

        // (2) Legacy cell shape.
        if (candidate is null)
        {
            try
            {
                candidate = MapLegacyCellShape(json, subject);
            }
            catch (TammaError)
            {
                // FromLegacyVerdictJson / severity parsing failed loud → violations,
                // NEVER a defaulted review.
                return Fail(
                    "The reviewer reply is neither a canonical review nor a recognised legacy verdict shape " +
                    "(no usable 'decision'/'verdict', or an out-of-vocabulary severity).");
            }
        }
        else
        {
            // Canonical reply: the caller's subject is authoritative.
            candidate = candidate with { Subject = subject };
        }

        // (3) Validate the mapped payload (blocking-issues⇒not-approvable, AC1).
        var result = ValidateReview(candidate);
        return result.IsValid
            ? new MapResult(candidate, Array.Empty<DocumentViolation>())
            : new MapResult(null, result.Violations);
    }

    private static Review? TryDeserializeCanonical(string json)
    {
        try
        {
            var review = JsonSerializer.Deserialize<Review>(json, DocumentJson.Options);
            // A canonical review must carry the required members; a null or a legacy
            // shape that happened to deserialize leniently is rejected.
            return review;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Map the CURRENT reviewer cell shape (D4): the top-level <c>issues[]</c> array
    /// (<c>task|severity|category|issue|recommendation</c>) plus the <c>verdict</c>
    /// half delegated to <see cref="Review.FromLegacyVerdictJson"/>. The full-cell
    /// issues mapping lives HERE (39-4 scoped its reader to verdict parity).
    /// </summary>
    private static Review MapLegacyCellShape(string json, ReviewSubject subject)
    {
        // Verdict half → decision + summary + blockingIssues-derived Critical issues.
        var verdictReview = Review.FromLegacyVerdictJson(json, subject);

        var issues = new List<ReviewIssue>();
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var severityRaw = GetString(item, "severity");
                    // Skip a fully-empty issue object; otherwise fail loud on a bad
                    // severity (routed to repair, never clamped).
                    if (severityRaw is null && GetString(item, "issue") is null && GetString(item, "category") is null)
                        continue;

                    var severity = ReviewSeverityExtensions.ParseLegacy(severityRaw);
                    var category = GetString(item, "category") ?? string.Empty;
                    var body = GetString(item, "issue") ?? GetString(item, "description") ?? string.Empty;
                    var fix = GetString(item, "recommendation") ?? GetString(item, "suggestedFix") ?? string.Empty;
                    var task = GetString(item, "task");
                    var file = GetString(item, "file");
                    var line = GetString(item, "line");

                    var description = string.IsNullOrWhiteSpace(task) ? body : $"[{task}] {body}";
                    issues.Add(new ReviewIssue(severity, category, description, fix, file, line));
                }
            }
        }

        // Concatenate the cell's issues after any blocking issues the verdict half
        // already produced.
        var combined = new List<ReviewIssue>(verdictReview.Issues);
        combined.AddRange(issues);
        return verdictReview with { Issues = combined };
    }

    private static DocumentValidationResult ValidateReview(Review review)
    {
        var payloadJson = JsonSerializer.Serialize(review, DocumentJson.Options);
        using var doc = JsonDocument.Parse(payloadJson);
        return DocumentTypeRegistry.Resolve(DocumentTypeKey.Review).Validate(doc.RootElement);
    }

    /// <summary>
    /// Build the repair-turn variables (D5): fold the domain-phrased violations +
    /// the review contract into the feedback variable the reviewer cell DECLARES
    /// (default <c>workItemJson</c>), via
    /// <see cref="ValidationFeedbackHelper.AppendFeedback"/> — a supplied-but-
    /// undeclared variable is silently dropped at render, so we only ever write into
    /// a declared one. Byte-identical passthrough when there are no violations.
    /// </summary>
    public static string BuildRepairVariables(
        string? variablesJson,
        IReadOnlyList<DocumentViolation> violations,
        string feedbackVariableName,
        string contract)
    {
        Dictionary<string, object?> vars;
        try
        {
            vars = string.IsNullOrWhiteSpace(variablesJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(variablesJson!) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            vars = new Dictionary<string, object?>();
        }

        // No violations → byte-identical passthrough of the feedback variable
        // (AppendFeedback returns the base unchanged on empty errors).
        var feedback = violations.Count == 0
            ? string.Empty
            : string.Join(
                "; ",
                violations.Select(v => v.Message)
                    .Append("Required output contract:")
                    .Append(contract));

        var baseValue = vars.TryGetValue(feedbackVariableName, out var existing) ? existing?.ToString() : null;
        vars[feedbackVariableName] = ValidationFeedbackHelper.AppendFeedback(baseValue, feedback);

        return JsonSerializer.Serialize(vars);
    }

    /// <summary>The default feedback variable — every reviewer cell declares it.</summary>
    public const string DefaultFeedbackVariable = "workItemJson";

    /// <summary>Whether another repair attempt is allowed (D5): <c>attempts &lt; Max</c>.</summary>
    public static bool ShouldRepair(int attempts, AcceptanceRules rules) =>
        attempts < rules.MaxValidationRepairAttempts;

    private static MapResult Fail(string message) =>
        new(null, new[] { new DocumentViolation(UnparseableReply, message) });

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Carve the first <c>{</c> … last <c>}</c> JSON object out of a reply.</summary>
    internal static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var candidate = text[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
