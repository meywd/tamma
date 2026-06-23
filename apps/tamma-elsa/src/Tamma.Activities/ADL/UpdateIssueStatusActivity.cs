using System.Text;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.ADL;

/// <summary>
/// Updates a GitHub issue during the autonomous cycle: posts a status comment,
/// adds / removes labels and (when supplied) composes a PR-linked close comment,
/// keeping the issue a "living log" of what Tamma is doing.
///
/// <para>Story 2.10 build-out: this activity used to <b>swallow</b> a failed
/// status update — after 3 failed attempts it logged a WARN and returned
/// normally, so the workflow always reported success even when nothing was
/// posted (a false-success / silent-failure violation). It is now an
/// <b>outcome-bearing</b> activity: a genuine callback failure surfaces a loud
/// <c>Failed</c> outcome (the workflow routes it to a failure edge that emits
/// <c>ISSUE_STATUS.UPDATED.FAILED</c>); a successful (or degraded local-no-op)
/// update surfaces <c>Updated</c>. It never reports success on a real failure.</para>
///
/// Outcomes:
///   - Updated: the update was applied (or degraded to a local no-op when no
///     engine callback is configured — flagged <c>degraded</c>, nothing failed).
///   - Failed:  the configured callback failed after retries (loud failure).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Update Issue Status",
    "Update a GitHub issue: status comment, labels, and PR-linked close",
    Kind = ActivityKind.Task
)]
[FlowNode("Updated", "Failed")]
public class UpdateIssueStatusActivity : Activity
{
    private const int MaxAttempts = 3;

    private readonly ILogger<UpdateIssueStatusActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Status message to post")]
    public Input<string> Message { get; set; } = default!;

    [Input(Description = "Optional labels to add")]
    public Input<string[]?> AddLabels { get; set; } = new((string[]?)null);

    [Input(Description = "Optional labels to remove")]
    public Input<string[]?> RemoveLabels { get; set; } = new((string[]?)null);

    [Input(Description = "Tenant id (empty / single-user → platform-scope event)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Merged-PR number to link in the close comment (0 → no link)")]
    public Input<int> PrNumber { get; set; } = new(0);

    [Input(Description = "Merged-PR URL to link in the close comment (empty → no link)")]
    public Input<string?> PrUrl { get; set; } = new((string?)null);

    [Output(Description = "True when the update was applied / degraded (no real failure)")]
    public Output<bool> Updated { get; set; } = default!;

    [Output(Description = "True when the update degraded to a local no-op (no callback configured)")]
    public Output<bool> Degraded { get; set; } = default!;

    [Output(Description = "Failure classification when the Failed outcome fires")]
    public Output<string?> ErrorCode { get; set; } = default!;

    [Output(Description = "Human-readable error reason when the Failed outcome fires")]
    public Output<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public UpdateIssueStatusActivity() { }

    public UpdateIssueStatusActivity(
        ILogger<UpdateIssueStatusActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repo = Repository.Get(context);
        var issueNum = IssueNumber.Get(context);
        var message = Message.Get(context) ?? "";
        var addLabels = AddLabels.Get(context);
        var removeLabels = RemoveLabels.Get(context);
        var prNumber = PrNumber.Get(context);
        var prUrl = PrUrl.Get(context);

        var body = ComposeBody(message, prNumber, prUrl);

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory is null)
        {
            // Degraded local/dev no-op: log and report a flagged success.
            // Nothing was attempted against a platform, so this is NOT a failure —
            // it is an auditable degrade (Degraded=true), never a false success.
            _logger?.LogInformation("[Issue #{IssueNumber}] {Message}", issueNum, body);
            SetSuccess(context, degraded: true);
            await context.CompleteActivityWithOutcomesAsync("Updated");
            return;
        }

        var client = new HttpIssueCallbackClient(_httpClientFactory.CreateClient(), callbackUrl);
        var outcome = await ExecuteCoreAsync(client, repo, issueNum, body, addLabels, removeLabels, _logger, context.CancellationToken);

        if (outcome.Success)
        {
            SetSuccess(context, degraded: false);
            await context.CompleteActivityWithOutcomesAsync("Updated");
        }
        else
        {
            // No false success — surface the failure loudly. The workflow's
            // Failed edge emits ISSUE_STATUS.UPDATED.FAILED.
            Updated.Set(context, false);
            Degraded.Set(context, false);
            ErrorCode.Set(context, outcome.ErrorCode ?? "issue-update-failed");
            Error.Set(context, outcome.Error);
            await context.CompleteActivityWithOutcomesAsync("Failed");
        }
    }

    private void SetSuccess(ActivityExecutionContext context, bool degraded)
    {
        Updated.Set(context, true);
        Degraded.Set(context, degraded);
        ErrorCode.Set(context, null);
        Error.Set(context, null);
    }

    /// <summary>
    /// Pure-ish orchestration core (no Elsa context): post the comment, then add
    /// labels (single atomic call) and remove labels (each independent) with a
    /// 3-attempt backoff. Returns a typed outcome so the happy / partial-failure
    /// / total-failure paths are unit-testable against a mocked
    /// <see cref="IIssueCallbackClient"/>.
    ///
    /// <para>NEVER swallows a real failure: a non-retryable result after the last
    /// attempt becomes a <c>Failed</c> outcome (closes the headline bug). The
    /// comment is posted first and, once it succeeds, is NOT re-posted on a
    /// subsequent label-only retry (duplicate-comment de-dup).</para>
    /// </summary>
    public static async Task<IssueUpdateOutcome> ExecuteCoreAsync(
        IIssueCallbackClient client,
        string repository,
        int issueNumber,
        string body,
        string[]? addLabels,
        string[]? removeLabels,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var commentPosted = string.IsNullOrEmpty(body);
        var labelsAdded = addLabels is not { Length: > 0 };
        var pendingRemovals = new List<string>(removeLabels ?? Array.Empty<string>());

        IssueCallbackResult last = IssueCallbackResult.Ok();

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (!commentPosted)
                {
                    last = await client.PostCommentAsync(repository, issueNumber, body, ct).ConfigureAwait(false);
                    if (!last.Success) { await BackoffAsync(attempt, last, logger, ct); continue; }
                    commentPosted = true; // de-dup: never re-post on a later retry
                }

                if (!labelsAdded)
                {
                    last = await client.AddLabelsAsync(repository, issueNumber, addLabels!, ct).ConfigureAwait(false);
                    if (!last.Success) { await BackoffAsync(attempt, last, logger, ct); continue; }
                    labelsAdded = true;
                }

                while (pendingRemovals.Count > 0)
                {
                    last = await client.RemoveLabelAsync(repository, issueNumber, pendingRemovals[0], ct).ConfigureAwait(false);
                    if (!last.Success) break;
                    pendingRemovals.RemoveAt(0); // de-dup: completed removals are not retried
                }
                if (pendingRemovals.Count > 0) { await BackoffAsync(attempt, last, logger, ct); continue; }

                return IssueUpdateOutcome.Updated(); // every block applied
            }
            catch (Exception ex)
            {
                last = IssueCallbackResult.Fail(ex.Message);
                if (attempt < MaxAttempts - 1)
                {
                    await BackoffAsync(attempt, last, logger, ct);
                }
                else
                {
                    logger?.LogWarning(ex, "Failed to update issue #{IssueNumber} after {Attempts} attempts", issueNumber, MaxAttempts);
                    return IssueUpdateOutcome.Failed(ClassifyError(ex.Message), ex.Message);
                }
            }
        }

        // Final attempt failed without throwing — surface the loud failure.
        logger?.LogWarning("Failed to update issue #{IssueNumber} after {Attempts} attempts: {Error}", issueNumber, MaxAttempts, last.Error);
        return IssueUpdateOutcome.Failed(ClassifyError(last.Error), last.Error);
    }

    private static async Task BackoffAsync(int attempt, IssueCallbackResult last, ILogger? logger, CancellationToken ct)
    {
        if (attempt >= MaxAttempts - 1) return;
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
        logger?.LogWarning("Issue update attempt {Attempt} failed ({Error}), retrying in {Delay}s",
            attempt + 1, last.Error, delay.TotalSeconds);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compose the comment body. When a merged-PR number / URL is supplied, link
    /// it (Story 2.10 AC5 — "comment linking to merged PR") rather than posting a
    /// static string. Never returns null. Pure — exposed for unit testing.
    /// </summary>
    public static string ComposeBody(string? message, int prNumber, string? prUrl)
    {
        var sb = new StringBuilder(message?.Trim() ?? "");
        var hasNumber = prNumber > 0;
        var hasUrl = !string.IsNullOrWhiteSpace(prUrl);
        if (hasNumber || hasUrl)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            if (hasNumber && hasUrl) sb.Append($"Resolved by #{prNumber} ({prUrl!.Trim()})");
            else if (hasNumber) sb.Append($"Resolved by #{prNumber}");
            else sb.Append($"Resolved by {prUrl!.Trim()}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Classify a callback failure for the failure edge (not-found / permission /
    /// rate-limit / generic) — drives the <c>errorCode</c> output / event tag.
    /// </summary>
    public static string ClassifyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "issue-update-failed";
        var lower = error.ToLowerInvariant();
        if (lower.Contains("404") || lower.Contains("not found")) return "issue-not-found";
        if (lower.Contains("403") || lower.Contains("forbidden") || lower.Contains("permission")) return "permission-denied";
        if (lower.Contains("401") || lower.Contains("unauthorized")) return "unauthorized";
        if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("rate_limited")) return "rate-limited";
        if (lower.Contains("503") || lower.Contains("not_configured")) return "callback-unavailable";
        return "issue-update-failed";
    }
}

/// <summary>
/// Typed result of <see cref="UpdateIssueStatusActivity.ExecuteCoreAsync"/> —
/// maps directly to the activity's Elsa outcome (Updated / Failed).
/// </summary>
public sealed class IssueUpdateOutcome
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }

    public static IssueUpdateOutcome Updated() => new() { Success = true };
    public static IssueUpdateOutcome Failed(string errorCode, string? error)
        => new() { Success = false, ErrorCode = errorCode, Error = error };
}
