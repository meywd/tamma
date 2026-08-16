using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Merges the pull request (configurable strategy), closes the associated issue,
/// and deletes the feature branch — with a pre-merge readiness/idempotency read,
/// verified (not inferred) success, and an EXPLICIT failure outcome.
///
/// <para>Story 2-10 build-out. The thin wrapper merged blind, inferred success
/// from a non-empty SHA, swallowed close-issue/branch-delete failures, and
/// surfaced an <c>Error</c> outcome the workflow left dangling (a silent stall).
/// This rewrite:</para>
/// <list type="bullet">
///   <item><description><b>Idempotency / pre-merge read</b> — fetch PR state first.
///     Already-merged → success (reuse the existing merge SHA, skip re-merge: no
///     GitHub 405 → spurious Error). Closed-unmerged or a confirmed conflict
///     (<c>mergeable == false</c>) → an explicit <c>Error</c> (never a blind
///     merge attempt). An <i>unknown</i> mergeable state does NOT block — GitHub
///     often hasn't computed it yet; the merge call itself is the authoritative
///     gate (a real conflict 405/409s, which we classify).</description></item>
///   <item><description><b>Configurable strategy</b> — <c>merge | squash | rebase</c>
///     (default squash) plumbed to the service.</description></item>
///   <item><description><b>Verified success</b> — the close-issue and branch-delete
///     results are captured into outputs. A merged PR whose issue-close failed
///     completes <c>MergedWithWarnings</c> (success=true, partial=true), not a
///     blanket clean success.</description></item>
///   <item><description><b>Explicit failure</b> — a merge that did not happen
///     classifies the failure (<c>merge_conflict | not_mergeable |
///     permission_denied | branch_protected | ci_pending | api_error</c>), sets
///     <c>FailureCode</c>/<c>FailureReason</c>, and completes <c>Error</c> (the
///     workflow routes this to a loud <c>MERGE.FAILED</c> terminal with
///     <c>success=false</c>).</description></item>
/// </list>
///
/// <para>This is a <see cref="TammaOutcomeActivity"/> so the umbrella
/// <c>PR.MERGE.STARTED/.COMPLETED/.FAILED</c> lifecycle events auto-emit; the
/// workflow additionally emits the headline <c>MERGE.SUCCESS</c>/<c>MERGE.FAILED</c>
/// + <c>ISSUE.CLOSED.*</c> + <c>BRANCH.DELETED.*</c> DCB events on the audit
/// stream via <see cref="EmitMergeEventActivity"/>.</para>
///
/// Outcomes:
///   - Merged:             PR merged, issue closed, branch deleted (clean).
///   - MergedWithWarnings: PR merged, but a post-merge sub-action (issue close /
///                         branch delete) failed — success=true, partial=true.
///   - Error:              the merge did not happen — success=false (workflow
///                         routes to the failure terminal).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Merge Pull Request",
    "Merge PR (configurable strategy), close issue, and delete branch with an explicit failure path",
    Kind = ActivityKind.Task
)]
[FlowNode("Merged", "MergedWithWarnings", "Error")]
public class MergePullRequestActivity : TammaOutcomeActivity
{
    /// <summary>Default merge strategy when none is supplied.</summary>
    public const string DefaultStrategy = "squash";

    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Issue number to close")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Branch to delete after merge")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Merge strategy: merge | squash | rebase (default squash)")]
    public Input<string> MergeStrategy { get; set; } = new(DefaultStrategy);

    [Input(Description = "When false, skip deleting the feature branch after merge (default true)")]
    public Input<bool> AutoDeleteBranch { get; set; } = new(true);

    [Input(Description = "When false, skip closing the associated issue after merge (default true)")]
    public Input<bool> CloseAssociatedIssue { get; set; } = new(true);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "Merge commit SHA")]
    public Output<string?> MergeSha { get; set; } = default!;

    [Output(Description = "The strategy actually applied (echoes the input, normalized)")]
    public Output<string?> AppliedStrategy { get; set; } = default!;

    [Output(Description = "True when the associated issue was closed")]
    public Output<bool> IssueClosed { get; set; } = default!;

    [Output(Description = "True when the feature branch was deleted")]
    public Output<bool> BranchDeleted { get; set; } = default!;

    [Output(Description = "True when the PR was already merged (idempotent re-dispatch)")]
    public Output<bool> AlreadyMerged { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> FailureCode { get; set; } = default!;

    [Output(Description = "Human-readable failure / warning reason")]
    public Output<string?> FailureReason { get; set; } = default!;

    [JsonConstructor]
    public MergePullRequestActivity() { }

    /// <summary>
    /// Story 38-1 — thin-client DI constructor. No <c>IGitHubIntegrationService</c>
    /// and no git token: the pre-merge read + merge + verified post-merge
    /// close-issue/branch-delete run server-side behind
    /// <c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/merge</c>. This stays a
    /// <see cref="TammaOutcomeActivity"/> so the <c>PR.MERGE.STARTED/COMPLETED/FAILED</c>
    /// lifecycle events still auto-emit.
    /// </summary>
    public MergePullRequestActivity(
        ILogger<MergePullRequestActivity>? logger,
        TammaApiClient? apiClient)
    {
        Logger = logger;
        _apiClient = apiClient;
    }

    /// <summary>
    /// The umbrella merge lifecycle event prefix — drives the auto
    /// <c>PR.MERGE.STARTED</c> / <c>PR.MERGE.COMPLETED</c> / <c>PR.MERGE.FAILED</c>
    /// events on this <see cref="TammaOutcomeActivity"/>. The workflow emits the
    /// richer headline <c>MERGE.*</c> + sub-action events separately.
    /// </summary>
    public override string? EventType => "PR.MERGE";

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["prNumber"] = PrNumber.Get(context),
        ["issueNumber"] = IssueNumber.Get(context),
        ["repository"] = Repository.Get(context),
        ["mergeStrategy"] = NormalizeStrategy(MergeStrategy.Get(context)),
    };

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var prNumber = PrNumber.Get(context);
        var issueNumber = IssueNumber.Get(context);
        var branchName = BranchName.Get(context) ?? "";
        var strategy = NormalizeStrategy(MergeStrategy.Get(context));
        var autoDeleteBranch = AutoDeleteBranch.Get(context);
        var closeIssue = CloseAssociatedIssue.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        var request = new GitMergePrRequest
        {
            MergeStrategy = strategy,
            IssueNumber = issueNumber,
            BranchName = branchName,
            AutoDeleteBranch = autoDeleteBranch,
            CloseAssociatedIssue = closeIssue,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.MergePullRequestAsync(repository, prNumber, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);

        MergeSha.Set(context, outcome.MergeSha ?? "");
        // Epic 31 P5 M2 (DG-4) — surface the method that actually merged
        // (the mediation core may have fallen back along rebase→squash→merge).
        AppliedStrategy.Set(context, outcome.AppliedStrategy ?? strategy);
        IssueClosed.Set(context, outcome.IssueClosed);
        BranchDeleted.Set(context, outcome.BranchDeleted);
        AlreadyMerged.Set(context, outcome.AlreadyMerged);
        FailureCode.Set(context, outcome.FailureCode);
        FailureReason.Set(context, outcome.FailureReason);

        await context.CompleteActivityWithOutcomesAsync(outcome.Outcome);
    }

    /// <summary>
    /// Story 38-1 (AC5) — project the git-mediation wire response into the SAME
    /// <see cref="MergeOutcome"/> the local path produced, so the outputs
    /// (MergeSha / IssueClosed / BranchDeleted / AlreadyMerged / FailureCode /
    /// FailureReason) and the Merged / MergedWithWarnings / Error outcome are
    /// byte-compatible. A null response (guard 403 / token 503 / auth 401 /
    /// transport) fails closed to Error.
    /// </summary>
    public static MergeOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return MergeOutcome.Failed("api_error", "git mediation endpoint unavailable");

        if (response.Success)
        {
            var warnings = response.Outcome == "MergedWithWarnings"
                ? (response.FailureReason ?? "post-merge warning")
                : null;
            return MergeOutcome.Merged(
                response.MergeSha ?? "",
                response.IssueClosed ?? false,
                response.BranchDeleted ?? false,
                response.AlreadyMerged ?? false,
                warnings) with
            {
                AppliedStrategy = response.AppliedMergeStrategy,
            };
        }

        return MergeOutcome.Failed(
            response.FailureCode ?? "api_error",
            response.FailureReason ?? "merge failed");
    }

    /// <summary>
    /// Pure orchestration core (no Elsa context): pre-merge read (idempotency +
    /// confirmed-conflict gate) → merge (configurable strategy) → verified
    /// close-issue + best-effort branch-delete → typed outcome. NEVER throws — an
    /// unexpected exception becomes an <c>Error</c> outcome (no silent success);
    /// a merge that did not happen is ALWAYS an Error (never a fabricated SHA).
    /// Returns a typed outcome so happy / idempotency / conflict / permission /
    /// partial paths are unit-testable against a mocked
    /// <see cref="Tamma.Platforms.Abstractions.IGitPlatformClient"/> (Epic 31 P2
    /// — retyped from the GitHub-specific client onto the platform abstraction;
    /// coarse error classes preserved via the legacy-string projection).
    /// </summary>
    public static async Task<MergeOutcome> ExecuteCoreAsync(
        Tamma.Platforms.Abstractions.IGitPlatformClient client,
        string repository,
        int prNumber,
        int issueNumber,
        string branchName,
        string strategy,
        bool autoDeleteBranch,
        bool closeIssue,
        ILogger? logger = null)
    {
        try
        {
            var (owner, repoName) = GitRepoName.Split(repository);
            var prNumberText = prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // ── 1. Pre-merge read: idempotency + confirmed-conflict gate ──
            var detail = await client.GetPullRequestAsync(owner, repoName, prNumberText);
            if (detail is not Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok detailOk)
            {
                // A transient read failure must NOT be mistaken for "go ahead and
                // merge blind". Classify and fail explicit (the merge gate already
                // gave human approval; a read outage is a real failure here).
                var error = DescribeFailure(detail);
                var code = ClassifyError(error);
                logger?.LogError("Pre-merge PR #{Pr} read failed: {Error}", prNumber, error);
                return MergeOutcome.Failed(code, error);
            }

            var pr = detailOk.Value;
            if (pr.State == Tamma.Platforms.Abstractions.Models.PullRequestState.Merged)
            {
                // Idempotent re-dispatch / webhook double-fire — already merged.
                // Reuse the existing merge SHA, run the post-merge cleanup, treat
                // as success. Never re-PUT /merge (that 405s on a merged PR).
                logger?.LogInformation("PR #{Pr} already merged ({Sha}) — idempotent path", prNumber, pr.MergeCommitSha ?? "?");
                return await CompletePostMergeAsync(
                    client, owner, repoName, issueNumber, branchName,
                    pr.MergeCommitSha ?? "", alreadyMerged: true,
                    autoDeleteBranch, closeIssue, logger);
            }

            if (pr.State == Tamma.Platforms.Abstractions.Models.PullRequestState.Closed)
            {
                // Closed but NOT merged → there is nothing to merge; failing
                // explicit beats a blind merge that 405s.
                logger?.LogError("PR #{Pr} is closed but not merged — cannot merge", prNumber);
                return MergeOutcome.Failed("not_mergeable", $"PR #{prNumber} is closed but not merged");
            }

            if (pr.Mergeable == false)
            {
                // The platform has CONFIRMED a conflict (mergeable == false). Fail
                // explicit with the conflict reason — never attempt a doomed
                // merge. (Mergeable == null = not yet computed → fall through; the
                // merge call is then the authoritative gate.)
                var reason = string.IsNullOrWhiteSpace(pr.MergeableState)
                    ? $"PR #{prNumber} has merge conflicts"
                    : $"PR #{prNumber} not mergeable (state: {pr.MergeableState})";
                logger?.LogError("PR #{Pr} not mergeable ({State})", prNumber, pr.MergeableState ?? "dirty");
                return MergeOutcome.Failed("merge_conflict", reason);
            }

            // ── 2. Merge (configurable strategy) — Epic 31 P5 M2 (DG-4):
            // when the platform refuses the requested method with the EXACT
            // typed merge_method_unsupported code (GitLab's rebase answers
            // exactly this), auto-fall back along the fixed order
            // rebase→squash→merge. Any other failure (conflict, permission,
            // protection) fails loud immediately — §4.5 exact-code-match
            // discipline; a real failure must never be consumed as
            // "try another method". The applied strategy is surfaced on the
            // outcome so mediation emits GIT.PR_MERGE.METHOD_FALLBACK.
            Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok? mergedOk = null;
            var appliedStrategy = strategy;
            foreach (var candidate in BuildMethodFallbackOrder(strategy))
            {
                var mergeResult = await client.MergePullRequestAsync(
                    new Tamma.Platforms.Abstractions.Models.MergePullRequestRequest(
                        owner, repoName, prNumberText, ToMergeMethod(candidate)));
                if (mergeResult is Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Ok ok)
                {
                    mergedOk = ok;
                    appliedStrategy = candidate;
                    break;
                }

                if (IsMergeMethodUnsupported(mergeResult))
                {
                    logger?.LogWarning(
                        "Merge method {Method} unsupported for PR #{Pr} — DG-4 fallback continues (rebase→squash→merge)",
                        candidate, prNumber);
                    continue;
                }

                var error = DescribeFailure(mergeResult);
                var code = ClassifyError(error);
                logger?.LogError("Failed to merge PR #{Pr}: {Error}", prNumber, error);
                return MergeOutcome.Failed(code, error);
            }

            if (mergedOk is null)
            {
                // Every method in the fixed order was typed-unsupported —
                // fail LOUD (DG-4's "fail loud only if none work").
                logger?.LogError("No merge method accepted by the platform for PR #{Pr}", prNumber);
                return MergeOutcome.Failed(
                    "merge_method_unsupported",
                    "the platform refused every merge method in the fallback order (rebase, squash, merge)");
            }

            var mergeSha = mergedOk.Value.MergeCommitSha ?? "";
            if (string.IsNullOrEmpty(mergeSha))
            {
                // The merge call reported success but no SHA — do not fabricate
                // one; treat as a failed (un-verifiable) merge.
                logger?.LogError("Merge of PR #{Pr} returned success with no SHA — treating as failure", prNumber);
                return MergeOutcome.Failed("api_error", "merge reported success but returned no commit SHA");
            }

            logger?.LogInformation("Merged PR #{Pr} (strategy {Strategy}) → {Sha}", prNumber, appliedStrategy, mergeSha);

            // ── 3. Verified post-merge cleanup ──
            var outcome = await CompletePostMergeAsync(
                client, owner, repoName, issueNumber, branchName, mergeSha,
                alreadyMerged: false, autoDeleteBranch, closeIssue, logger);
            return outcome with
            {
                AppliedStrategy = appliedStrategy,
                MethodFallbackFrom = appliedStrategy == strategy ? null : strategy,
            };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error merging PR #{Pr}", prNumber);
            return MergeOutcome.Failed("api_error", ex.Message);
        }
    }

    /// <summary>Normalized strategy token → the abstraction's merge method.</summary>
    internal static Tamma.Platforms.Abstractions.Models.MergeMethod ToMergeMethod(string strategy) =>
        (strategy ?? "").Trim().ToLowerInvariant() switch
        {
            "merge" => Tamma.Platforms.Abstractions.Models.MergeMethod.Merge,
            "rebase" => Tamma.Platforms.Abstractions.Models.MergeMethod.Rebase,
            _ => Tamma.Platforms.Abstractions.Models.MergeMethod.Squash,
        };

    /// <summary>DG-4's fixed fallback order. The requested method is tried
    /// first; when the platform answers the exact typed
    /// <c>merge_method_unsupported</c> code, the remaining methods are tried
    /// in the owner-decided order rebase→squash→merge.</summary>
    internal static readonly string[] MethodFallbackOrder = ["rebase", "squash", "merge"];

    /// <summary>Requested method first, then the fixed order minus it.</summary>
    internal static IReadOnlyList<string> BuildMethodFallbackOrder(string requested)
    {
        var normalized = NormalizeStrategy(requested);
        var order = new List<string>(4) { normalized };
        foreach (var m in MethodFallbackOrder)
        {
            if (!string.Equals(m, normalized, StringComparison.Ordinal)) order.Add(m);
        }
        return order;
    }

    /// <summary>§4.5 exact-code classification: ONLY the typed
    /// <c>merge_method_unsupported</c> InvalidRequest code (ordinal match —
    /// GitLab's rebase refusal answers exactly this) is consumed by the DG-4
    /// fallback. Everything else — including <c>capability_unsupported</c>
    /// (the whole merge verb missing; another method cannot help) — stays a
    /// real failure.</summary>
    internal static bool IsMergeMethodUnsupported(
        Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest> result) =>
        result is Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.PullRequest>.Failed f
        && f.Error is Tamma.Platforms.Abstractions.PlatformError.InvalidRequest ir
        && string.Equals(ir.Code, "merge_method_unsupported", StringComparison.Ordinal);

    private static string DescribeFailure<T>(Tamma.Platforms.Abstractions.PlatformResult<T> result) =>
        result switch
        {
            Tamma.Platforms.Abstractions.PlatformResult<T>.Failed f =>
                Tamma.Platforms.Abstractions.PlatformErrorText.ToLegacyString(f.Error),
            Tamma.Platforms.Abstractions.PlatformResult<T>.ServiceUnavailable =>
                "503: platform unavailable",
            _ => "unknown platform result",
        };

    /// <summary>
    /// Post-merge close-issue (verified) + branch-delete (best-effort). A failed
    /// issue-close downgrades the merged result to <c>MergedWithWarnings</c>
    /// (success=true, partial=true) — the merge stands but the audit shows it was
    /// not fully clean. A failed branch-delete is a warning only (the PRD treats
    /// branch cleanup as best-effort), but it is still surfaced as a warning +
    /// emitted as <c>BRANCH.DELETED.FAILED</c> by the workflow.
    /// </summary>
    private static async Task<MergeOutcome> CompletePostMergeAsync(
        Tamma.Platforms.Abstractions.IGitPlatformClient client,
        string owner,
        string repoName,
        int issueNumber,
        string branchName,
        string mergeSha,
        bool alreadyMerged,
        bool autoDeleteBranch,
        bool closeIssue,
        ILogger? logger)
    {
        var warnings = new List<string>();
        var issueClosed = false;
        var branchDeleted = false;

        // Close the issue (verified — capture the result).
        if (closeIssue && issueNumber > 0)
        {
            try
            {
                var comment = $"Resolved by PR (merge SHA: {mergeSha}).";
                var close = await client.CloseIssueAsync(
                    owner, repoName,
                    issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    comment);
                issueClosed =
                    close is Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.Ok closedOk
                    && closedOk.Value.State == Tamma.Platforms.Abstractions.Models.IssueState.Closed;
                if (!issueClosed)
                {
                    var reason = close is Tamma.Platforms.Abstractions.PlatformResult<Tamma.Platforms.Abstractions.Models.Issue>.Ok
                        ? "issue close returned an unsuccessful result"
                        : DescribeFailure(close);
                    logger?.LogWarning("Failed to close issue #{Issue} after merge: {Error}", issueNumber, reason);
                    warnings.Add($"issue-close-failed: {reason}");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to close issue #{Issue} after merge (non-fatal to merge)", issueNumber);
                warnings.Add($"issue-close-failed: {ex.Message}");
            }
        }
        else
        {
            // Closing disabled or no issue → not a warning; just not done.
            issueClosed = false;
        }

        // Delete the feature branch (best-effort).
        if (autoDeleteBranch && !string.IsNullOrWhiteSpace(branchName))
        {
            try
            {
                var del = await client.DeleteBranchAsync(owner, repoName, branchName);
                branchDeleted =
                    del is Tamma.Platforms.Abstractions.PlatformResult<bool>.Ok delOk && delOk.Value;
                if (!branchDeleted)
                {
                    var reason = del is Tamma.Platforms.Abstractions.PlatformResult<bool>.Ok
                        ? "branch delete returned an unsuccessful result"
                        : DescribeFailure(del);
                    logger?.LogWarning("Failed to delete branch {Branch} after merge: {Error}", branchName, reason);
                    warnings.Add($"branch-delete-failed: {reason}");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to delete branch {Branch} after merge (non-fatal)", branchName);
                warnings.Add($"branch-delete-failed: {ex.Message}");
            }
        }

        return MergeOutcome.Merged(
            mergeSha, issueClosed, branchDeleted, alreadyMerged,
            warnings.Count == 0 ? null : string.Join("; ", warnings));
    }

    /// <summary>
    /// Normalize a merge strategy token to <c>merge | squash | rebase</c>; an
    /// unknown / empty value falls back to <c>squash</c> (the platform default).
    /// </summary>
    public static string NormalizeStrategy(string? strategy)
        => (strategy ?? "").Trim().ToLowerInvariant() switch
        {
            "merge" => "merge",
            "rebase" => "rebase",
            _ => DefaultStrategy,
        };

    /// <summary>
    /// Classify a merge / read failure (Story 2-10 AC8): permission /
    /// merge-conflict / branch-protected / not-mergeable / ci-pending / transient
    /// (api_error). The integration layer surfaces a status-prefixed message
    /// (e.g. <c>"409: ..."</c> / <c>"405: ..."</c>) or a typed sentinel.
    /// </summary>
    public static string ClassifyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "api_error";
        var lower = error.ToLowerInvariant();
        if (lower.Contains("conflict") || lower.StartsWith("409")) return "merge_conflict";
        if (lower.Contains("403") || lower.Contains("forbidden") || lower.Contains("permission")) return "permission_denied";
        if (lower.Contains("protected") || lower.Contains("required status check") || lower.Contains("review")) return "branch_protected";
        if (lower.Contains("405") || lower.Contains("not mergeable") || lower.Contains("not_mergeable")) return "not_mergeable";
        if (lower.Contains("pending") || lower.Contains("checks are not")) return "ci_pending";
        if (lower.Contains("429") || lower.Contains("rate limit")
            || lower.Contains("500") || lower.Contains("502") || lower.Contains("503") || lower.Contains("504")
            || lower.Contains("timeout") || lower.Contains("unavailable")) return "api_error";
        return "api_error";
    }
}

/// <summary>
/// Typed result of <see cref="MergePullRequestActivity.ExecuteCoreAsync"/> — maps
/// to the activity's Elsa outcome (Merged / MergedWithWarnings / Error). On
/// failure <see cref="MergeSha"/> is empty so a consumer can never read a false
/// merge; the workflow's <c>SetSuccess</c> reads <c>Outcome != "Error"</c>, NOT a
/// non-empty SHA (the old false-success inference).
/// </summary>
public sealed record MergeOutcome
{
    public string Outcome { get; init; } = "Error";
    public string? MergeSha { get; init; }
    public bool IssueClosed { get; init; }
    public bool BranchDeleted { get; init; }
    public bool AlreadyMerged { get; init; }
    public bool Partial { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }

    // ── Epic 31 P5 M2 (DG-4) — merge-method fallback read-backs. ──

    /// <summary>The strategy that actually performed the merge (null on
    /// failure paths and on the idempotent already-merged path).</summary>
    public string? AppliedStrategy { get; init; }

    /// <summary>The originally requested strategy when the fixed-order
    /// fallback re-routed the merge; null when no fallback occurred. A
    /// non-null value is mirrored by a GIT.PR_MERGE.METHOD_FALLBACK audit
    /// event at the mediation layer.</summary>
    public string? MethodFallbackFrom { get; init; }

    /// <summary>True when the merge happened (clean OR with warnings).</summary>
    public bool MergeSucceeded => Outcome != "Error";

    public static MergeOutcome Merged(
        string mergeSha, bool issueClosed, bool branchDeleted, bool alreadyMerged, string? warnings)
        => new()
        {
            Outcome = warnings is null ? "Merged" : "MergedWithWarnings",
            MergeSha = mergeSha,
            IssueClosed = issueClosed,
            BranchDeleted = branchDeleted,
            AlreadyMerged = alreadyMerged,
            Partial = warnings is not null,
            FailureReason = warnings,
        };

    public static MergeOutcome Failed(string failureCode, string failureReason)
        => new()
        {
            Outcome = "Error",
            MergeSha = "",
            FailureCode = failureCode,
            FailureReason = failureReason,
        };
}
