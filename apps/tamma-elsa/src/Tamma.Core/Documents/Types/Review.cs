using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The single, closed review-decision vocabulary (Story 39-4 Design Decision D1).
/// The keystone of the epic: the three forked verdict shapes
/// (<c>ReviewAggregationHelper.ParseRoleVerdict</c> string + object forms, the
/// <c>TaskReviewWorkflow</c> inline copy, and the code-review family) collapse onto
/// these three members. Canonical wire is kebab-case (39-2 D8); the legacy spellings
/// map through <see cref="ReviewDecisionExtensions.ParseLegacy"/>, never a lenient
/// schema.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ReviewDecision>))]
public enum ReviewDecision
{
    [Wire("approve")] Approve,
    [Wire("request-changes")] RequestChanges,
    [Wire("needs-discussion")] NeedsDiscussion,
}

/// <summary>
/// The closed severity vocabulary for a <see cref="ReviewIssue"/> (Design Decision
/// D2 — surveyed from the three baselines: plan-review cells emit
/// <c>critical|major|minor|suggestion</c>, code-review emits
/// <c>critical|major|minor|style</c>, <c>ReviewCommentSeverity</c> adds <c>Info</c>).
/// <c>critical</c> is the dominant "blocking" spelling, so AC3's blocking threshold
/// hangs off <see cref="ReviewSeverityExtensions.IsBlocking"/> (true only for
/// <see cref="Critical"/>). Legacy <c>style</c>/<c>info</c>/<c>blocker</c> spellings
/// map through <see cref="ReviewSeverityExtensions.ParseLegacy"/>.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ReviewSeverity>))]
public enum ReviewSeverity
{
    [Wire("critical")] Critical,
    [Wire("major")] Major,
    [Wire("minor")] Minor,
    [Wire("suggestion")] Suggestion,
}

/// <summary>
/// <see cref="ReviewDecision"/> wire + legacy mapping (Design Decision D1).
/// </summary>
public static class ReviewDecisionExtensions
{
    /// <summary>The canonical wire string for <paramref name="decision"/>.</summary>
    public static string ToWire(this ReviewDecision decision) => EnumWire<ReviewDecision>.ToWire(decision);

    /// <summary>
    /// Maps ANY spelling either <c>ParseRoleVerdict</c> shape accepts onto the
    /// unified enum (D1), case-insensitively: <c>approve</c>/<c>APPROVE</c> →
    /// <see cref="ReviewDecision.Approve"/>; <c>REQUEST_CHANGES</c> →
    /// <see cref="ReviewDecision.RequestChanges"/>; <c>NEEDS_DISCUSSION</c> →
    /// <see cref="ReviewDecision.NeedsDiscussion"/>; the legacy pessimistic string
    /// <c>concerns</c> → <see cref="ReviewDecision.RequestChanges"/> (the
    /// revision-triggering decision); code-review's <c>COMMENT</c> →
    /// <see cref="ReviewDecision.NeedsDiscussion"/> (non-blocking, non-approving).
    /// Anything else fails LOUD — never a default.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.REVIEW.UNKNOWN_DECISION</c> for an unrecognized spelling.
    /// </exception>
    public static ReviewDecision ParseLegacy(string? raw)
    {
        var norm = (raw ?? string.Empty).Trim();
        switch (norm.ToUpperInvariant())
        {
            case "APPROVE":
            case "APPROVED":
                return ReviewDecision.Approve;
            case "REQUEST_CHANGES":
            case "REQUEST-CHANGES":
            case "CONCERNS":
                return ReviewDecision.RequestChanges;
            case "NEEDS_DISCUSSION":
            case "NEEDS-DISCUSSION":
            case "COMMENT":
                return ReviewDecision.NeedsDiscussion;
        }

        throw new TammaError(
            "DOCUMENT.REVIEW.UNKNOWN_DECISION",
            $"Unknown review decision '{raw}' — no legacy verdict spelling maps to it. " +
            "Valid decisions: approve, request-changes, needs-discussion.",
            new Dictionary<string, object?> { ["input"] = raw },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}

/// <summary>
/// <see cref="ReviewSeverity"/> wire + legacy mapping + the AC3 blocking predicate
/// (Design Decision D2).
/// </summary>
public static class ReviewSeverityExtensions
{
    /// <summary>The canonical wire string for <paramref name="severity"/>.</summary>
    public static string ToWire(this ReviewSeverity severity) => EnumWire<ReviewSeverity>.ToWire(severity);

    /// <summary>
    /// AC3's single blocking threshold: only <see cref="ReviewSeverity.Critical"/>
    /// blocks. The flagship "approve while a blocking issue exists" rule hangs off
    /// exactly this function.
    /// </summary>
    public static bool IsBlocking(this ReviewSeverity severity) => severity == ReviewSeverity.Critical;

    /// <summary>
    /// Maps the legacy severity spellings the three baselines emit onto the closed
    /// enum (D2), case-insensitively: <c>style</c>/<c>info</c> →
    /// <see cref="ReviewSeverity.Suggestion"/>; <c>blocker</c> →
    /// <see cref="ReviewSeverity.Critical"/>; plus the canonical wires.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.REVIEW.UNKNOWN_SEVERITY</c> for an unrecognized spelling.
    /// </exception>
    public static ReviewSeverity ParseLegacy(string? raw)
    {
        var norm = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (norm)
        {
            case "critical":
            case "blocker":
                return ReviewSeverity.Critical;
            case "major":
                return ReviewSeverity.Major;
            case "minor":
                return ReviewSeverity.Minor;
            case "suggestion":
            case "style":
            case "info":
                return ReviewSeverity.Suggestion;
        }

        throw new TammaError(
            "DOCUMENT.REVIEW.UNKNOWN_SEVERITY",
            $"Unknown review severity '{raw}'. Valid severities: critical, major, minor, suggestion.",
            new Dictionary<string, object?> { ["input"] = raw },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}

/// <summary>
/// The subject a <see cref="Review"/> is about — a closed two-kind reference union
/// (Design Decision D3). One shape serves plan review (kind <c>document</c>, a
/// <c>plan</c>), task review (kind <c>document</c>), and code review (kind
/// <c>diff</c>) — honoring "code is NOT a document type": the diff gets a
/// <em>reference</em> (repository + PR/commit), never a schema.
/// </summary>
public sealed record ReviewSubject
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }              // "document" | "diff"
    [JsonPropertyName("documentId")] public Guid? DocumentId { get; init; }
    [JsonPropertyName("documentType")] public string? DocumentType { get; init; }      // a DocumentTypeKey wire
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("prNumber")] public int? PrNumber { get; init; }
    [JsonPropertyName("commitSha")] public string? CommitSha { get; init; }
}

/// <summary>
/// One issue raised by a review — severity + category + a concrete suggested fix
/// (AC2). <see cref="File"/>/<see cref="Line"/> are optional location hints.
/// </summary>
public sealed record ReviewIssue(
    [property: JsonPropertyName("severity")] ReviewSeverity Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("suggestedFix")] string SuggestedFix,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("line")] string? Line = null);

/// <summary>
/// The unified review document (Story 39-4 keystone, AC2/AC3). Models a SINGLE
/// reviewer's review over a <see cref="ReviewSubject"/>; aggregation/quorum is
/// 39-7. Reviewer provenance (role) rides the envelope's <c>ProducedBy</c>, not the
/// payload (D10). <see cref="AggregatedFrom"/> is the optional panel-provenance seam
/// reserved for 39-7 D7 (null for single reviews).
/// </summary>
public sealed record Review
{
    [JsonPropertyName("subject")] public required ReviewSubject Subject { get; init; }
    [JsonPropertyName("decision")] public required ReviewDecision Decision { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("issues")] public required IReadOnlyList<ReviewIssue> Issues { get; init; }

    /// <summary>
    /// OPTIONAL panel provenance (39-7 D7): null for a single reviewer's review;
    /// when present it must be non-empty and duplicate-free (validated). Defined
    /// HERE so the field stays inside 39-7's diff surface.
    /// </summary>
    [JsonPropertyName("aggregatedFrom")] public IReadOnlyList<Guid>? AggregatedFrom { get; init; }

    /// <summary>
    /// Ingest BOTH legacy verdict shapes <c>ReviewAggregationHelper.ParseRoleVerdict</c>
    /// accepts today (Design Decision D4) into the unified type, attaching the caller's
    /// resolved <paramref name="subject"/>:
    /// <list type="bullet">
    ///   <item>string verdict — <c>{"verdict":"approve","comments":"...","suggestedChanges":"..."}</c>
    ///     — decision via <see cref="ReviewDecisionExtensions.ParseLegacy"/>, comments → summary,
    ///     no issues.</item>
    ///   <item>object verdict — <c>{"verdict":{"decision":"APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION",
    ///     "summary":"...","blockingIssues":[...]}}</c> — summary → <see cref="Summary"/>, each
    ///     blocking issue → a <see cref="ReviewSeverity.Critical"/> issue (category <c>blocking</c>,
    ///     empty suggested fix).</item>
    /// </list>
    /// <para><b>Fail-loud, never a default.</b> Garbage (null/blank/<c>{}</c>/no <c>verdict</c>)
    /// throws <c>DOCUMENT.REVIEW.LEGACY_UNPARSEABLE</c>; an unknown decision spelling throws
    /// <c>DOCUMENT.REVIEW.UNKNOWN_DECISION</c> — the pessimistic-default question is settled by
    /// the lifecycle (repair ring → ValidationExhausted), never encoded in the type. A
    /// legacy-ingested review whose issues lack a suggested fix deserializes fine but FAILS
    /// <see cref="ReviewDocumentType.Validate"/> (<c>ISSUE_MISSING_FIX</c>) — deliberate: incomplete
    /// legacy content goes to repair, it is not laundered.</para>
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.REVIEW.LEGACY_UNPARSEABLE</c> on unparseable/empty/verdict-less input;
    /// code <c>DOCUMENT.REVIEW.UNKNOWN_DECISION</c> on an unrecognized decision spelling.
    /// </exception>
    public static Review FromLegacyVerdictJson(string json, ReviewSubject subject)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            throw LegacyUnparseable(json);

        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw LegacyUnparseable(json);
        }

        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("verdict", out var verdict))
                throw LegacyUnparseable(json);

            ReviewDecision decision;
            var summary = string.Empty;
            var issues = new List<ReviewIssue>();

            if (verdict.ValueKind == JsonValueKind.Object)
            {
                var decisionStr = verdict.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                decision = ReviewDecisionExtensions.ParseLegacy(decisionStr);

                if (verdict.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.String)
                    summary = sum.GetString() ?? string.Empty;

                if (verdict.TryGetProperty("blockingIssues", out var bi) && bi.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bi.EnumerateArray())
                    {
                        var text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
                        if (string.IsNullOrWhiteSpace(text))
                            continue;
                        issues.Add(new ReviewIssue(ReviewSeverity.Critical, "blocking", text, string.Empty));
                    }
                }
            }
            else if (verdict.ValueKind == JsonValueKind.String)
            {
                decision = ReviewDecisionExtensions.ParseLegacy(verdict.GetString());
                if (root.TryGetProperty("comments", out var c) && c.ValueKind == JsonValueKind.String)
                    summary = c.GetString() ?? string.Empty;
            }
            else
            {
                throw LegacyUnparseable(json);
            }

            return new Review
            {
                Subject = subject,
                Decision = decision,
                Summary = summary,
                Issues = issues,
            };
        }
    }

    private static TammaError LegacyUnparseable(string? json) => new(
        "DOCUMENT.REVIEW.LEGACY_UNPARSEABLE",
        "The legacy verdict JSON could not be parsed into a review — a parse failure is a " +
        "validation failure routed to the repair ring, never a defaulted 'concerns' document.",
        new Dictionary<string, object?> { ["json"] = json },
        retryable: false,
        severity: TammaErrorSeverity.High);
}
