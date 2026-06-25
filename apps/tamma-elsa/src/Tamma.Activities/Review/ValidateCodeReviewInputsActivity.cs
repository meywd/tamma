using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Review;

/// <summary>
/// Validates the code-review workflow inputs (Story 7-1D AC2/AC4, completeness audit
/// 2026-06-22 <c>CodeReview.md</c> §Missing #3) BEFORE any PR is created. Requires a
/// non-empty story id, repository url, and junior id, AND at least one resolvable reviewer
/// (from the explicit <c>reviewerIds</c> input or the configured
/// <c>CodeReview:ReviewerPool</c>). On any failure it routes to the <c>Invalid</c> outcome
/// with a SPECIFIC <see cref="ErrorMessage"/> — no generic "Code review failed", no silent
/// false-path. Follows tenant→system→error: it never falls back to an empty reviewer set.
///
/// <para><b>Dual-caller guard (deferred #2):</b> the mentorship contract keys on
/// <c>sessionId</c>; an autonomous-loop <c>SingleIssueCycle</c>-shaped payload
/// (<c>{repository, prNumber, branchName, tenantId}</c> with no <c>storyId</c>/<c>juniorId</c>)
/// is NOT silently dropped — it fails validation here with a clear reason directing it to the
/// (future) LLM-review variant, rather than proceeding to create a PR for an empty story.</para>
///
/// Outcomes: <c>Valid</c> / <c>Invalid</c>.
/// </summary>
[Activity(
    "Tamma.Review",
    "Validate Code Review Inputs",
    "Validate story/repo/junior/reviewer inputs before creating a PR",
    Kind = ActivityKind.Task
)]
[FlowNode("Valid", "Invalid")]
public class ValidateCodeReviewInputsActivity : CodeActivity
{
    private readonly ILogger<ValidateCodeReviewInputsActivity>? _logger;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Story id")]
    public Input<string?> StoryId { get; set; } = new((string?)null);

    [Input(Description = "Repository url")]
    public Input<string?> RepositoryUrl { get; set; } = new((string?)null);

    [Input(Description = "Junior developer id")]
    public Input<string?> JuniorId { get; set; } = new((string?)null);

    [Input(Description = "Explicit reviewer ids (JSON array of strings)")]
    public Input<string?> ReviewerIdsJson { get; set; } = new((string?)null);

    [Output(Description = "Specific validation error message (empty when valid)")]
    public Output<string> ErrorMessage { get; set; } = default!;

    [Output(Description = "Resolved reviewers (comma-separated; from input or pool)")]
    public Output<string> ResolvedReviewers { get; set; } = default!;

    [JsonConstructor]
    public ValidateCodeReviewInputsActivity() { }

    public ValidateCodeReviewInputsActivity(
        ILogger<ValidateCodeReviewInputsActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var result = Validate(
            StoryId.Get(context),
            RepositoryUrl.Get(context),
            JuniorId.Get(context),
            ReviewerIdsJson.Get(context),
            LoadReviewerPool());

        if (!result.IsValid)
        {
            _logger?.LogWarning("Code review input validation failed: {Message}", result.ErrorMessage);
            ErrorMessage.Set(context, result.ErrorMessage);
            ResolvedReviewers.Set(context, string.Empty);
            await context.CompleteActivityWithOutcomesAsync("Invalid");
            return;
        }

        ResolvedReviewers.Set(context, string.Join(",", result.Reviewers));
        ErrorMessage.Set(context, string.Empty);

        _logger?.LogInformation(
            "Code review inputs valid; {Count} reviewer(s) resolved", result.Reviewers.Count);

        await context.CompleteActivityWithOutcomesAsync("Valid");
    }

    /// <summary>
    /// Pure validation (Story 7-1D AC2/AC4). Requires non-empty story/repo/junior and ≥1
    /// resolvable reviewer (explicit ids, else the supplied <paramref name="reviewerPool"/>).
    /// Returns a SPECIFIC error message on failure — never empty. Exposed for unit testing.
    /// </summary>
    public static ValidationResult Validate(
        string? storyId, string? repositoryUrl, string? juniorId,
        string? reviewerIdsJson, IReadOnlyList<string> reviewerPool)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(storyId)) missing.Add("storyId");
        if (string.IsNullOrWhiteSpace(repositoryUrl)) missing.Add("repositoryUrl");
        if (string.IsNullOrWhiteSpace(juniorId)) missing.Add("juniorId");

        if (missing.Count > 0)
        {
            return ValidationResult.Invalid(
                $"Code review cannot start: missing required input(s) [{string.Join(", ", missing)}]. " +
                "The 'code-review' workflow is the mentorship PR-lifecycle workflow and requires " +
                "sessionId/storyId/juniorId/repositoryUrl. (An autonomous-loop LLM-review payload " +
                "of {repository, prNumber, branchName, tenantId} is not handled by this workflow.)");
        }

        // Resolve reviewers: explicit input first, else the configured pool. ≥1 required.
        var reviewers = ParseReviewerIds(reviewerIdsJson);
        if (reviewers.Count == 0)
            reviewers = reviewerPool
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        if (reviewers.Count == 0)
        {
            return ValidationResult.Invalid(
                $"Code review cannot start for story '{storyId!.Trim()}': no reviewer resolvable. " +
                "Provide 'reviewerIds' or configure a non-empty 'CodeReview:ReviewerPool'.");
        }

        return ValidationResult.Valid(reviewers);
    }

    /// <summary>Pure validation outcome.</summary>
    public sealed class ValidationResult
    {
        public bool IsValid { get; private init; }
        public string ErrorMessage { get; private init; } = string.Empty;
        public IReadOnlyList<string> Reviewers { get; private init; } = Array.Empty<string>();

        public static ValidationResult Valid(IReadOnlyList<string> reviewers)
            => new() { IsValid = true, Reviewers = reviewers };

        public static ValidationResult Invalid(string message)
            => new() { IsValid = false, ErrorMessage = message };
    }

    private static List<string> ParseReviewerIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            return arr?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList() ?? new List<string>();
        }
        catch
        {
            // Tolerate a comma-separated string too.
            return json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
    }

    private List<string> LoadReviewerPool()
    {
        var pool = _configuration?.GetSection("CodeReview:ReviewerPool").Get<string[]>();
        if (pool is { Length: > 0 })
            return pool.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        // Also accept a comma-separated scalar form.
        var scalar = _configuration?["CodeReview:ReviewerPool"];
        if (!string.IsNullOrWhiteSpace(scalar))
            return scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        return new List<string>();
    }
}
