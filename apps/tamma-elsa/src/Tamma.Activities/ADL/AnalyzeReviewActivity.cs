using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Fetches PR review comments from GitHub and analyzes them to determine
/// which comments are actionable and need fixes. Categorizes each comment
/// as bug/style/design/question/praise/security/performance.
///
/// Outcomes:
///   - Done: analysis complete (check HasActionableComments output)
///   - Error: failed to fetch or analyze comments
/// </summary>
[Activity(
    "Tamma.ADL",
    "Analyze Review",
    "Fetch and analyze PR review comments for actionable items",
    Kind = ActivityKind.Task
)]
[FlowNode("Done", "Error")]
public class AnalyzeReviewActivity : Activity
{
    private readonly ILogger<AnalyzeReviewActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "Whether there are actionable review comments")]
    public Output<bool> HasActionableComments { get; set; } = default!;

    [Output(Description = "Review analysis result as JSON")]
    public Output<string?> AnalysisJson { get; set; } = default!;

    [JsonConstructor]
    public AnalyzeReviewActivity() { }

    /// <summary>
    /// Story 38-1 — thin-client DI constructor. No <c>IGitHubIntegrationService</c>
    /// and no git token: the review comments are fetched through
    /// <c>GET /api/v1/git/{owner}/{repo}/pull-requests/{n}/comments</c> via
    /// <see cref="TammaApiClient"/>; the (token-free) categorization runs engine-side.
    /// </summary>
    public AnalyzeReviewActivity(
        ILogger<AnalyzeReviewActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var prNumber = PrNumber.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        try
        {
            var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
            var response = await apiClient
                .GetPullRequestCommentsAsync(repository, prNumber, context.WorkflowExecutionContext.Id, tenantId, context.CancellationToken)
                .ConfigureAwait(false);

            if (response is null || !response.Success)
            {
                _logger?.LogError(
                    "Failed to fetch review comments for PR #{Number}: {Failure}",
                    prNumber, response?.FailureReason ?? "git mediation endpoint unavailable");
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            var comments = response.Comments ?? new List<GitCommentDto>();

            var fixItems = comments.Select(c =>
            {
                var category = CategorizeComment(c.Body);
                var priority = DeterminePriority(category);
                return new ReviewFixItem
                {
                    FilePath = c.Path ?? "",
                    Line = c.Line,
                    Comment = c.Body,
                    Category = category,
                    Priority = priority
                };
            }).ToList();

            var actionableCount = fixItems.Count(f => ReviewCommentCategory.IsActionable(f.Category));

            var analysis = new ReviewAnalysisResult
            {
                TotalComments = comments.Count,
                HasActionableComments = actionableCount > 0,
                ActionableComments = actionableCount,
                FixItems = fixItems,
                Summary = BuildSummary(fixItems, actionableCount)
            };

            HasActionableComments.Set(context, analysis.HasActionableComments);
            AnalysisJson.Set(context, JsonSerializer.Serialize(analysis));

            _logger?.LogInformation(
                "Analyzed PR #{Number}: {Total} comments, {Actionable} actionable (categories: {Categories})",
                prNumber, comments.Count, actionableCount,
                string.Join(", ", fixItems.GroupBy(f => f.Category).Select(g => $"{g.Key}={g.Count()}")));
            await context.CompleteActivityWithOutcomesAsync("Done");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error analyzing review for PR #{Number}", prNumber);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    /// <summary>
    /// Categorize a review comment based on keyword analysis.
    /// Uses simple heuristic matching — no LLM call needed for categorization.
    /// </summary>
    internal static string CategorizeComment(string commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
            return ReviewCommentCategory.Unknown;

        var lower = commentBody.ToLowerInvariant();

        // Praise patterns — check first since they often contain other keywords
        if (IsPraise(lower))
            return ReviewCommentCategory.Praise;

        // Bug patterns
        if (IsBug(lower))
            return ReviewCommentCategory.Bug;

        // Security patterns
        if (IsSecurity(lower))
            return ReviewCommentCategory.Security;

        // Performance patterns
        if (IsPerformance(lower))
            return ReviewCommentCategory.Performance;

        // Design patterns
        if (IsDesign(lower))
            return ReviewCommentCategory.Design;

        // Style patterns
        if (IsStyle(lower))
            return ReviewCommentCategory.Style;

        // Question patterns
        if (IsQuestion(lower))
            return ReviewCommentCategory.Question;

        // Default: treat as style if the comment is short, otherwise design
        return lower.Length < 50 ? ReviewCommentCategory.Style : ReviewCommentCategory.Design;
    }

    private static bool IsPraise(string lower)
    {
        var praisePatterns = new[]
        {
            "lgtm", "looks good", "nice", "great", "well done", "good job",
            "love this", "perfect", "excellent", "awesome", "clean", "neat",
            "good catch", "ship it", "thumbs up", "approve"
        };
        // "+1" is checked separately to avoid false positives like "n+1"
        if (lower.Trim() == "+1" || lower.Contains(" +1") || lower.StartsWith("+1"))
            return true;
        return praisePatterns.Any(p => lower.Contains(p));
    }

    private static bool IsBug(string lower)
    {
        var bugPatterns = new[]
        {
            "bug", "crash", "null ref", "null pointer", "nullref", "npe",
            "off by one", "off-by-one", "race condition", "deadlock",
            "memory leak", "infinite loop", "stack overflow", "exception",
            "wrong result", "incorrect", "broken", "doesn't work",
            "does not work", "fails when", "will fail", "would fail",
            "missing null check", "missing check", "unhandled", "undefined behavior",
            "index out of", "out of bounds", "overflow", "underflow",
            "this will throw", "this throws", "this could throw"
        };
        return bugPatterns.Any(p => lower.Contains(p));
    }

    private static bool IsSecurity(string lower)
    {
        var securityPatterns = new[]
        {
            "security", "vulnerab", "injection", "xss", "csrf", "sql injection",
            "sanitize", "escape", "secret", "credential", "password", "token",
            "auth", "permission", "privilege", "access control", "unsafe",
            "untrusted", "user input", "validation missing"
        };
        return securityPatterns.Any(p => lower.Contains(p));
    }

    private static bool IsPerformance(string lower)
    {
        var perfPatterns = new[]
        {
            "performance", "slow", "n+1", "o(n^2)", "o(n²)", "quadratic",
            "cache", "memoize", "optimize", "optimise", "bottleneck",
            "expensive", "heavy", "inefficient", "unnecessary allocation",
            "unnecessary copy", "batch", "bulk", "lazy load"
        };
        return perfPatterns.Any(p => lower.Contains(p));
    }

    private static bool IsDesign(string lower)
    {
        var designPatterns = new[]
        {
            "refactor", "extract", "single responsibility", "solid",
            "coupling", "cohesion", "abstract", "interface", "pattern",
            "architecture", "separation of concern", "dependency",
            "encapsulat", "composition", "inheritance", "polymorphism",
            "should be", "consider using", "better approach", "alternative",
            "restructure", "reorganize", "simplify", "complex"
        };
        return designPatterns.Any(p => lower.Contains(p));
    }

    private static bool IsStyle(string lower)
    {
        var stylePatterns = new[]
        {
            "naming", "typo", "spacing", "indent", "format", "convention",
            "consistent", "whitespace", "camelcase", "pascalcase", "snake_case",
            "lint", "nit", "nit:", "minor:", "style", "readability",
            "comment", "documentation", "doc", "todo", "fixme",
            "magic number", "magic string", "hard-coded", "hardcoded"
        };
        return stylePatterns.Any(p => lower.Contains(p));
    }

    private static bool IsQuestion(string lower)
    {
        var questionPatterns = new[]
        {
            "why", "what", "how", "when", "where", "could you explain",
            "can you explain", "is this", "are we", "do we", "should we",
            "not sure", "confused", "unclear", "understand"
        };
        // Must end with ? or contain explicit question patterns
        if (lower.TrimEnd().EndsWith("?"))
            return true;
        return questionPatterns.Any(p => lower.Contains(p));
    }

    internal static string DeterminePriority(string category) => category switch
    {
        ReviewCommentCategory.Bug => "critical",
        ReviewCommentCategory.Security => "critical",
        ReviewCommentCategory.Performance => "high",
        ReviewCommentCategory.Design => "normal",
        ReviewCommentCategory.Style => "low",
        ReviewCommentCategory.Question => "low",
        ReviewCommentCategory.Praise => "none",
        _ => "normal"
    };

    private static string BuildSummary(List<ReviewFixItem> fixItems, int actionableCount)
    {
        if (fixItems.Count == 0)
            return "No review comments found";

        var categoryCounts = fixItems
            .GroupBy(f => f.Category)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();

        return $"Found {fixItems.Count} comment(s): {string.Join(", ", categoryCounts)}. " +
               $"{actionableCount} actionable item(s) requiring fixes.";
    }
}
