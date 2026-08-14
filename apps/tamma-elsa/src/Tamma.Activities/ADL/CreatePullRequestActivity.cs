using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Creates (or idempotently reuses / updates) a pull request for the completed
/// implementation. Builds the body from an AI-generated description (passed in
/// via the call-LLM mediation) or a deterministic structured fallback, merged
/// with a change summary (files / lines / commits) and a test/coverage summary.
///
/// Outcomes:
///   - Created: a new PR was opened.
///   - Updated: an existing open PR for head→base was reused / updated (idempotency).
///   - Error:   PR creation failed (the workflow routes this to the failure edge).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Create Pull Request",
    "Create a PR with implementation summary for review",
    Kind = ActivityKind.Task
)]
[FlowNode("Created", "Updated", "Error")]
public class CreatePullRequestActivity : Activity
{
    private readonly ILogger<CreatePullRequestActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Feature branch name")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Base branch to merge into")]
    public Input<string> BaseBranch { get; set; } = new("main");

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Issue title")]
    public Input<string> IssueTitle { get; set; } = default!;

    [Input(Description = "Plan summary JSON")]
    public Input<string?> PlanJson { get; set; } = default!;

    [Input(Description = "Open the PR in draft mode")]
    public Input<bool> Draft { get; set; } = new(false);

    [Input(Description = "AI-generated PR body (from the call-LLM mediation); empty → deterministic fallback")]
    public Input<string?> AiBody { get; set; } = new((string?)null);

    [Input(Description = "Change summary JSON (files added/modified/deleted, +/- lines, commits, top files)")]
    public Input<string?> ChangeSummaryJson { get; set; } = new((string?)null);

    [Input(Description = "Test/coverage summary JSON (testsRun/passed/failed, coverage, ciStatus)")]
    public Input<string?> TestSummaryJson { get; set; } = new((string?)null);

    [Input(Description = "Issue labels JSON array (drives smart labelling)")]
    public Input<string?> IssueLabelsJson { get; set; } = new((string?)null);

    [Input(Description = "Reviewers JSON array (usernames to request)")]
    public Input<string?> ReviewersJson { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "Created/updated PR number")]
    public Output<int> PrNumber { get; set; } = default!;

    [Output(Description = "Created/updated PR URL")]
    public Output<string?> PrUrl { get; set; } = default!;

    [Output(Description = "True when an existing PR was reused/updated instead of created")]
    public Output<bool> Reused { get; set; } = default!;

    [Output(Description = "Final draft state of the PR")]
    public Output<bool> IsDraft { get; set; } = default!;

    [Output(Description = "Labels applied to the PR (JSON array)")]
    public Output<string?> AppliedLabels { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> ErrorCode { get; set; } = default!;

    [JsonConstructor]
    public CreatePullRequestActivity() { }

    /// <summary>
    /// Story 38-1 — thin-client DI constructor. No <c>IGitHubIntegrationService</c>
    /// and no git token: the PR create/update routes through
    /// <c>POST /api/v1/git/{owner}/{repo}/pull-requests</c> via
    /// <see cref="TammaApiClient"/>. Title / body / labels are still composed
    /// engine-side (pure, token-free).
    /// </summary>
    public CreatePullRequestActivity(
        ILogger<CreatePullRequestActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var branchName = BranchName.Get(context);
        var baseBranch = BaseBranch.Get(context) ?? "main";
        var issueNumber = IssueNumber.Get(context);
        var issueTitle = IssueTitle.Get(context) ?? "";
        var planJson = PlanJson.GetOrDefault(context);
        var draft = Draft.Get(context);
        var aiBody = AiBody.GetOrDefault(context);
        var changeSummary = ChangeSummary.Parse(ChangeSummaryJson.GetOrDefault(context));
        var testSummary = TestSummary.Parse(TestSummaryJson.GetOrDefault(context));
        var issueLabels = ParseStringList(IssueLabelsJson.GetOrDefault(context));
        var reviewers = ParseStringList(ReviewersJson.GetOrDefault(context));
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        var title = BuildTitle(issueNumber, issueTitle);
        var body = BuildBody(issueNumber, aiBody, planJson, changeSummary, testSummary);
        var labels = DetermineLabels(issueLabels, changeSummary);

        var request = new GitCreatePrRequest
        {
            Title = title,
            Body = body,
            HeadRef = branchName,
            BaseRef = baseBranch,
            Labels = labels,
            Reviewers = reviewers,
            IsDraft = draft,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.CreatePullRequestAsync(repository, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);
        switch (outcome.Outcome)
        {
            case "Created":
            case "Updated":
                SetSuccessOutputs(context, outcome.PrNumber, outcome.PrUrl, outcome.IsDraft, labels);
                Reused.Set(context, outcome.Reused);
                await context.CompleteActivityWithOutcomesAsync(outcome.Outcome);
                break;
            default:
                ErrorCode.Set(context, outcome.ErrorCode ?? "pr-creation-failed");
                await context.CompleteActivityWithOutcomesAsync("Error");
                break;
        }
    }

    /// <summary>
    /// Story 38-1 (AC5) — project the git-mediation wire response into the SAME
    /// <see cref="PrCreationOutcome"/> the local path produced (Created / Updated /
    /// Error + PrNumber / PrUrl / IsDraft / Reused / ErrorCode). A null response
    /// (guard 403 / token 503 / auth 401 / transport) fails closed to Error.
    /// </summary>
    public static PrCreationOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return PrCreationOutcome.Failed("git-mediation-unavailable");

        return response switch
        {
            { Success: true, Outcome: "Created" } => PrCreationOutcome.Create(response.PrNumber ?? 0, response.PrUrl, response.IsDraft ?? false),
            { Success: true, Outcome: "Updated" } => PrCreationOutcome.Reuse(response.PrNumber ?? 0, response.PrUrl, response.IsDraft ?? false),
            _ => PrCreationOutcome.Failed(response.FailureCode ?? "pr-creation-failed"),
        };
    }

    /// <summary>
    /// Pure-ish orchestration core (no Elsa context): idempotency lookup →
    /// create OR update, with the defensive 422-race fall-back. Returns a typed
    /// outcome so the happy / draft+ready / idempotency / failure paths are
    /// unit-testable against a mocked
    /// <see cref="Tamma.Platforms.Abstractions.IGitPlatformClient"/> (Epic 31 P2
    /// — retyped from the GitHub-specific client onto the platform abstraction;
    /// coarse error classes preserved via the legacy-string projection).
    /// NEVER throws — exceptions become an <c>Error</c> outcome (no silent success).
    /// </summary>
    public static async Task<PrCreationOutcome> ExecuteCoreAsync(
        Tamma.Platforms.Abstractions.IGitPlatformClient client,
        string repository,
        string headBranch,
        string baseBranch,
        bool draft,
        CreatePullRequestRequest request,
        ILogger? logger = null)
    {
        try
        {
            var (owner, repoName) = GitRepoName.Split(repository);

            // ── Idempotency (AC8): reuse / update an existing open PR for head→base. ──
            var lookup = await client.ListOpenPullRequestsForBranchAsync(owner, repoName, headBranch, baseBranch);
            if (lookup is Tamma.Platforms.Abstractions.PlatformResult<IReadOnlyList<Tamma.Platforms.Abstractions.Models.PullRequest>>.Ok lookupOk
                && lookupOk.Value.Count > 0)
            {
                var open = lookupOk.Value[0];
                logger?.LogInformation(
                    "Existing open PR #{Number} found for {Head}->{Base}; updating instead of re-creating",
                    open.Number, headBranch, baseBranch);

                var reused = await UpdateExistingAsync(client, owner, repoName, open, request, logger);
                if (reused.Outcome is not null) return reused.Outcome;
                return PrCreationOutcome.Failed(ClassifyError(reused.Error));
            }

            // ── Create ──
            var result = await client.OpenPullRequestAsync(
                new Tamma.Platforms.Abstractions.Models.OpenPullRequestRequest(
                    Owner: owner,
                    RepoName: repoName,
                    Title: request.Title,
                    SourceBranch: request.Head,
                    TargetBranch: request.Base,
                    Body: request.Body,
                    IsDraft: request.IsDraft));
            if (result is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok createdOk)
            {
                var error = DescribeFailure(result);

                // Defensive: a 422 "already exists" race → fall back to the reuse path.
                if (IsAlreadyExistsError(error))
                {
                    var retry = await client.ListOpenPullRequestsForBranchAsync(owner, repoName, headBranch, baseBranch);
                    if (retry is Tamma.Platforms.Abstractions.PlatformResult<IReadOnlyList<Tamma.Platforms.Abstractions.Models.PullRequest>>.Ok retryOk
                        && retryOk.Value.Count > 0)
                    {
                        var reused = await UpdateExistingAsync(client, owner, repoName, retryOk.Value[0], request, logger);
                        if (reused.Outcome is not null) return reused.Outcome;
                    }
                }

                logger?.LogError("Failed to create PR: {Error}", error);
                return PrCreationOutcome.Failed(ClassifyError(error));
            }

            // Best-effort post-create metadata (labels + reviewers) — the live
            // path's TryAdd*/TryRequest* posture: a failure here MUST NOT fail
            // PR creation (Story 2.8 AC3 — degrade gracefully). Epic 31 P5 M2
            // (DG-3): a reviewer skip is surfaced on the outcome so the
            // mediation layer emits GIT.PR_REVIEWERS.SKIPPED — never silent.
            var metadata = await TryApplyMetadataAsync(client, owner, repoName, createdOk.Value.Number, request, logger);

            logger?.LogInformation("Created {Kind} PR #{Number}",
                draft ? "draft" : "ready", createdOk.Value.Number);
            return PrCreationOutcome.Create(
                ParsePrNumber(createdOk.Value.Number), createdOk.Value.HtmlUrl, draft,
                metadata.ReviewersSkipped, metadata.ReviewersSkipReason);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error creating PR");
            return PrCreationOutcome.Failed(ClassifyError(ex.Message));
        }
    }

    /// <summary>Update the existing PR's title/body + best-effort metadata —
    /// the reuse ("Updated") path. Null outcome = the update failed.</summary>
    private static async Task<(PrCreationOutcome? Outcome, string? Error)> UpdateExistingAsync(
        Tamma.Platforms.Abstractions.IGitPlatformClient client,
        string owner,
        string repoName,
        Tamma.Platforms.Abstractions.Models.PullRequest open,
        CreatePullRequestRequest request,
        ILogger? logger)
    {
        var updated = await client.UpdatePullRequestAsync(
            new Tamma.Platforms.Abstractions.Models.UpdatePullRequestRequest(
                owner, repoName, open.Number, request.Title, request.Body));
        if (updated is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok updatedOk)
        {
            return (null, DescribeFailure(updated));
        }

        var metadata = await TryApplyMetadataAsync(client, owner, repoName, open.Number, request, logger);

        return (PrCreationOutcome.Reuse(
            ParsePrNumber(updatedOk.Value.Number),
            string.IsNullOrEmpty(updatedOk.Value.HtmlUrl) ? open.HtmlUrl : updatedOk.Value.HtmlUrl,
            open.IsDraft,
            metadata.ReviewersSkipped, metadata.ReviewersSkipReason), null);
    }

    /// <summary>Epic 31 P5 M2 (DG-3) — the label a skipped reviewer request
    /// leaves on the PR so a human notices no reviewer was assigned.</summary>
    public const string ReviewersSkippedLabel = "needs-reviewer";

    internal readonly record struct PrMetadataOutcome(bool ReviewersSkipped, string? ReviewersSkipReason);

    /// <summary>Best-effort labels + reviewers (never fails the PR step).
    /// Epic 31 P5 M2 (DG-3): a reviewer request the platform did not perform
    /// (capability unsupported / unresolvable / refusal) is RECORDED — the PR
    /// is labeled <see cref="ReviewersSkippedLabel"/> best-effort and the skip
    /// + key-free reason are returned so the caller emits the
    /// GIT.PR_REVIEWERS.SKIPPED audit event. Silent skips are forbidden (§4.4).</summary>
    private static async Task<PrMetadataOutcome> TryApplyMetadataAsync(
        Tamma.Platforms.Abstractions.IGitPlatformClient client,
        string owner,
        string repoName,
        string prNumber,
        CreatePullRequestRequest request,
        ILogger? logger)
    {
        if (request.Labels is { Count: > 0 })
        {
            try
            {
                var labels = await client.AddPullRequestLabelsAsync(
                    new Tamma.Platforms.Abstractions.Models.AddPullRequestLabelsRequest(
                        owner, repoName, prNumber, request.Labels));
                if (labels is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok)
                    logger?.LogWarning("Label assignment for PR #{Number} failed (continuing)", prNumber);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to add labels to PR #{Number} (continuing)", prNumber);
            }
        }

        if (request.Reviewers is not { Count: > 0 })
        {
            return new PrMetadataOutcome(false, null);
        }

        string? skipReason = null;
        try
        {
            var reviewers = await client.RequestReviewersAsync(
                new Tamma.Platforms.Abstractions.Models.RequestReviewersRequest(
                    owner, repoName, prNumber, request.Reviewers));
            if (reviewers is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok)
            {
                skipReason = ClassifyReviewerSkip(reviewers);
                logger?.LogWarning(
                    "Reviewer request for PR #{Number} skipped ({Reason}) — continuing without reviewers (DG-3)",
                    prNumber, skipReason);
            }
        }
        catch (Exception ex)
        {
            skipReason = "api_error";
            logger?.LogWarning(ex, "Failed to request reviewers on PR #{Number} (continuing, DG-3)", prNumber);
        }

        if (skipReason is null)
        {
            return new PrMetadataOutcome(false, null);
        }

        // DG-3's alternative-step content: label the PR so a human sees no
        // reviewer was assigned. Best-effort — a label failure does not fail
        // the PR step either (the audit event still records the skip).
        try
        {
            var labeled = await client.AddPullRequestLabelsAsync(
                new Tamma.Platforms.Abstractions.Models.AddPullRequestLabelsRequest(
                    owner, repoName, prNumber, new List<string> { ReviewersSkippedLabel }));
            if (labeled is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok)
                logger?.LogWarning("Could not label PR #{Number} '{Label}' after reviewer skip", prNumber, ReviewersSkippedLabel);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not label PR #{Number} '{Label}' after reviewer skip", prNumber, ReviewersSkippedLabel);
        }

        return new PrMetadataOutcome(true, skipReason);
    }

    /// <summary>Key-free classification of a reviewer-request refusal. The
    /// exact <c>capability_unsupported</c> code is preserved (§4.5 exact-match
    /// discipline); everything else coarsens through the legacy projection.</summary>
    internal static string ClassifyReviewerSkip(
        Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest> result)
    {
        if (result is Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Failed f)
        {
            if (Tamma.Platforms.Abstractions.PlatformErrorText.IsCapabilityUnsupported(f.Error))
                return "capability_unsupported";
            if (f.Error is Tamma.Platforms.Abstractions.PlatformError.InvalidRequest)
                return "reviewer_unresolvable";
        }
        return ClassifyError(DescribeFailure(result));
    }

    private static string DescribeFailure<T>(Tamma.Platforms.Abstractions.PlatformResult<T> result) =>
        result switch
        {
            Tamma.Platforms.Abstractions.PlatformResult<T>.Failed f =>
                Tamma.Platforms.Abstractions.PlatformErrorText.ToLegacyString(f.Error),
            Tamma.Platforms.Abstractions.PlatformResult<T>.ServiceUnavailable =>
                "503: platform unavailable",
            _ => "unknown platform result",
        };

    private static int ParsePrNumber(string number) =>
        int.TryParse(number, out var n) ? n : 0;

    private void SetSuccessOutputs(ActivityExecutionContext context, int number, string? url, bool isDraft, List<string> labels)
    {
        PrNumber.Set(context, number);
        PrUrl.Set(context, url);
        IsDraft.Set(context, isDraft);
        AppliedLabels.Set(context, JsonSerializer.Serialize(labels));
    }

    // ================================================================
    // Pure, testable helpers
    // ================================================================

    /// <summary>Build the PR title — fixed ADL convention with the issue number.</summary>
    public static string BuildTitle(int issueNumber, string issueTitle)
        => $"[ADL] #{issueNumber}: {issueTitle}".TrimEnd(':', ' ');

    /// <summary>
    /// True when <paramref name="body"/> already references <c>#{issueNumber}</c>
    /// as a whole token. Word-boundary (no surrounding digit) so <c>#5</c> is not
    /// matched inside <c>#55</c> / <c>#512</c> — otherwise the auto-close keyword
    /// would be wrongly suppressed for low issue numbers.
    /// </summary>
    public static bool HasIssueReference(string? body, int issueNumber)
        => !string.IsNullOrEmpty(body)
           && Regex.IsMatch(body, $@"(?<!\d)#{issueNumber}(?!\d)");

    /// <summary>
    /// Compose the PR body. Prefers the AI-generated description (from the
    /// call-LLM mediation); falls back to a deterministic structured body when
    /// the AI body is empty. Either way the change + test summaries are appended
    /// so the audit data is present regardless of the LLM outcome. Never empty.
    /// </summary>
    public static string BuildBody(
        int issueNumber,
        string? aiBody,
        string? planJson,
        ChangeSummary? changeSummary,
        TestSummary? testSummary)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(aiBody))
        {
            sb.Append(aiBody.Trim());
            sb.Append("\n\n");
            // Ensure the issue-close keyword is present even if the AI omitted it.
            // Word-boundary match so `#5` isn't considered present inside `#55`
            // / `#512` (which would wrongly suppress the auto-close for low
            // issue numbers).
            if (!HasIssueReference(aiBody, issueNumber))
                sb.Append($"Closes #{issueNumber}\n\n");
        }
        else
        {
            sb.Append(BuildFallbackBody(issueNumber, planJson, changeSummary, testSummary));
            sb.Append('\n');
        }

        sb.Append(BuildChangeSummarySection(changeSummary));
        sb.Append(BuildTestSummarySection(testSummary));
        sb.Append("---\n_Generated by Tamma ADL_\n");
        return sb.ToString();
    }

    /// <summary>
    /// Deterministic, non-LLM structured fallback body (FR-19e). Used when the
    /// AI description is unavailable — NEVER an empty or plain dump. Sections:
    /// Summary / Changes / Testing / Breaking Changes / Migration / Checklist.
    /// </summary>
    public static string BuildFallbackBody(
        int issueNumber,
        string? planJson,
        ChangeSummary? changeSummary,
        TestSummary? testSummary)
    {
        var sb = new StringBuilder();
        sb.Append("## Summary\n\n");
        sb.Append($"Closes #{issueNumber}\n\n");
        sb.Append("Implemented by Tamma's Autonomous Development Loop (ADL). ");
        sb.Append("An AI-generated description was unavailable, so this is a structured fallback.\n\n");

        if (!string.IsNullOrWhiteSpace(planJson))
        {
            sb.Append("## Changes\n\n");
            sb.Append("Implementation follows the generated plan:\n\n");
            sb.Append($"```json\n{planJson.Trim()}\n```\n\n");
        }
        else
        {
            sb.Append("## Changes\n\nSee the change summary below.\n\n");
        }

        sb.Append("## Testing\n\n");
        sb.Append(testSummary is null
            ? "- Test results are summarised below if available.\n\n"
            : $"- Tests: {testSummary.TestsPassed}/{testSummary.TestsRun} passed, coverage {testSummary.Coverage:0.#}%.\n\n");

        sb.Append("## Breaking Changes\n\nNone identified.\n\n");
        sb.Append("## Migration\n\nNo migration required.\n\n");
        sb.Append("## Checklist\n\n");
        sb.Append("- [ ] Code follows project conventions\n");
        sb.Append("- [ ] Tests pass and coverage is acceptable\n");
        sb.Append("- [ ] Documentation updated if required\n");
        sb.Append("- [ ] Breaking changes documented (if any)\n\n");
        return sb.ToString();
    }

    /// <summary>Build the change-summary section, or a degrade note if absent.</summary>
    public static string BuildChangeSummarySection(ChangeSummary? changeSummary)
    {
        if (changeSummary is null)
            return "## Change Summary\n\n_Change summary unavailable._\n\n";

        var sb = new StringBuilder();
        sb.Append("## Change Summary\n\n");
        sb.Append($"- **Files changed:** {changeSummary.FilesChanged} ");
        sb.Append($"(added {changeSummary.FilesAdded}, modified {changeSummary.FilesModified}, deleted {changeSummary.FilesDeleted})\n");
        sb.Append($"- **Lines:** +{changeSummary.LinesAdded} / -{changeSummary.LinesDeleted}\n");
        sb.Append($"- **Commits:** {changeSummary.Commits}\n");

        if (changeSummary.TopFiles is { Count: > 0 })
        {
            sb.Append("\n### Modified Files\n");
            foreach (var f in changeSummary.TopFiles.Take(15))
                sb.Append($"- `{f}`\n");
            if (changeSummary.FilesChanged > 15)
                sb.Append($"\n... and {changeSummary.FilesChanged - 15} more files\n");
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Build the test/coverage section, or a degrade note if absent.</summary>
    public static string BuildTestSummarySection(TestSummary? testSummary)
    {
        if (testSummary is null)
            return "## Test Results\n\n_Test summary unavailable._\n\n";

        var sb = new StringBuilder();
        sb.Append("## Test Results\n\n");
        sb.Append($"- **Tests:** {testSummary.TestsPassed} passed, {testSummary.TestsFailed} failed (of {testSummary.TestsRun})\n");
        sb.Append($"- **Coverage:** {testSummary.Coverage:0.#}%\n");
        if (!string.IsNullOrWhiteSpace(testSummary.CiStatus))
            sb.Append($"- **CI:** {testSummary.CiStatus}\n");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Derive labels from issue labels + change type/risk (Story 2.8 AC3 / cap #9),
    /// beyond the two static ones. Always includes the ADL markers; de-dupes.
    /// </summary>
    public static List<string> DetermineLabels(IReadOnlyList<string> issueLabels, ChangeSummary? changeSummary)
    {
        var labels = new List<string> { "tamma-auto", "adl" };

        foreach (var l in issueLabels)
        {
            var lower = l.ToLowerInvariant();
            if (lower.Contains("bug")) labels.Add("bugfix");
            if (lower.Contains("feature") || lower.Contains("enhancement")) labels.Add("enhancement");
            if (lower.Contains("security")) labels.Add("security-review");
            if (lower.Contains("breaking")) labels.Add("breaking-change");
            if (lower.Contains("doc")) labels.Add("documentation");
            if (lower.Contains("performance") || lower.Contains("perf")) labels.Add("performance");
        }

        if (changeSummary is not null)
        {
            if (changeSummary.FilesAdded > 0) labels.Add("new-feature");
            var totalLines = changeSummary.LinesAdded + changeSummary.LinesDeleted;
            if (totalLines >= 500 || changeSummary.FilesChanged >= 20) labels.Add("large");
        }

        return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Classify a create/update failure for the failure edge (permission /
    /// merge-conflict / already-exists / rate-limit / generic).
    /// </summary>
    public static string ClassifyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "pr-creation-failed";
        var lower = error.ToLowerInvariant();
        if (lower.Contains("403") || lower.Contains("forbidden") || lower.Contains("permission")) return "permission-denied";
        if (lower.Contains("409") || lower.Contains("conflict")) return "merge-conflict";
        if (IsAlreadyExistsError(error)) return "pr-already-exists";
        if (lower.Contains("429") || lower.Contains("rate limit")) return "rate-limited";
        return "pr-creation-failed";
    }

    public static bool IsAlreadyExistsError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        var lower = error.ToLowerInvariant();
        return lower.Contains("already exists") || lower.Contains("422");
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// Typed result of <see cref="CreatePullRequestActivity.ExecuteCoreAsync"/> —
/// maps directly to the activity's Elsa outcome (Created / Updated / Error).
/// </summary>
public sealed class PrCreationOutcome
{
    public string Outcome { get; init; } = "Error";
    public int PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public bool IsDraft { get; init; }
    public bool Reused { get; init; }
    public string? ErrorCode { get; init; }

    // ── Epic 31 P5 M2 (DG-3) — reviewer-request degradation read-backs.
    //    The reviewer request is best-effort post-create metadata (a failure
    //    never fails the PR step), but a skip is NEVER silent: the core
    //    records it here so the mediation layer emits the
    //    GIT.PR_REVIEWERS.SKIPPED audit event (§4.4). ──

    /// <summary>True when reviewers were requested but the platform did not
    /// perform the request (capability unsupported / unresolvable reviewer /
    /// refusal). The PR itself was created/updated fine.</summary>
    public bool ReviewersSkipped { get; init; }

    /// <summary>Key-free reason for the skip (e.g. <c>capability_unsupported</c>
    /// or the coarse legacy error string).</summary>
    public string? ReviewersSkipReason { get; init; }

    public static PrCreationOutcome Create(
        int number, string? url, bool isDraft,
        bool reviewersSkipped = false, string? reviewersSkipReason = null)
        => new()
        {
            Outcome = "Created", PrNumber = number, PrUrl = url, IsDraft = isDraft, Reused = false,
            ReviewersSkipped = reviewersSkipped, ReviewersSkipReason = reviewersSkipReason,
        };

    public static PrCreationOutcome Reuse(
        int number, string? url, bool isDraft,
        bool reviewersSkipped = false, string? reviewersSkipReason = null)
        => new()
        {
            Outcome = "Updated", PrNumber = number, PrUrl = url, IsDraft = isDraft, Reused = true,
            ReviewersSkipped = reviewersSkipped, ReviewersSkipReason = reviewersSkipReason,
        };

    public static PrCreationOutcome Failed(string errorCode)
        => new() { Outcome = "Error", ErrorCode = errorCode };
}

/// <summary>
/// Change-summary inputs carried from the cycle's file-change / commit steps.
/// </summary>
public sealed class ChangeSummary
{
    public int FilesChanged { get; set; }
    public int FilesAdded { get; set; }
    public int FilesModified { get; set; }
    public int FilesDeleted { get; set; }
    public int LinesAdded { get; set; }
    public int LinesDeleted { get; set; }
    public int Commits { get; set; }
    public List<string> TopFiles { get; set; } = new();

    public static ChangeSummary? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ChangeSummary>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Build a change summary from raw integration models (reuses Review/CreatePRActivity logic).</summary>
    public static ChangeSummary FromChanges(IReadOnlyList<GitHubFileChange> changes, int commitCount)
    {
        var summary = new ChangeSummary
        {
            FilesChanged = changes.Count,
            LinesAdded = changes.Sum(c => c.Additions),
            LinesDeleted = changes.Sum(c => c.Deletions),
            Commits = commitCount,
            TopFiles = changes
                .OrderByDescending(c => c.Additions + c.Deletions)
                .Take(15)
                .Select(c => c.FilePath)
                .ToList()
        };
        foreach (var c in changes)
        {
            switch (c.ChangeType?.ToLowerInvariant())
            {
                case "added": summary.FilesAdded++; break;
                case "removed":
                case "deleted": summary.FilesDeleted++; break;
                default: summary.FilesModified++; break;
            }
        }
        return summary;
    }
}

/// <summary>
/// Test/coverage inputs carried from the cycle's test / CI steps.
/// </summary>
public sealed class TestSummary
{
    public int TestsRun { get; set; }
    public int TestsPassed { get; set; }
    public int TestsFailed { get; set; }
    public double Coverage { get; set; }
    public string? CiStatus { get; set; }

    public static TestSummary? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TestSummary>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
