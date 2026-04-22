using System.Text.Json;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Result envelope used by the engine callback GitHub-proxy endpoints. When
/// a real GitHub App client is wired the implementations populate
/// <see cref="Result"/>; until then they short-circuit to
/// <see cref="ServiceUnavailable"/> so callers see the documented 503
/// instead of a stub success.
/// </summary>
public sealed record GitHubCallbackResult<T>(bool ServiceUnavailable, T? Result, string? ErrorReason)
{
    public static GitHubCallbackResult<T> NotConfigured() =>
        new(true, default, "github_client_not_configured");
    public static GitHubCallbackResult<T> Ok(T value) =>
        new(false, value, null);
    public static GitHubCallbackResult<T> Failed(string reason) =>
        new(false, default, reason);
}

/// <summary>
/// GitHub-proxy surface for the engine callback endpoints. Mirrors the deleted
/// TS <c>engine-github-routes.ts</c> module.
///
/// <para>Audit findings 005, 006, 007, 008, 009, 010, 011 (all P0) — the
/// individual stub endpoints share a single underlying blocker: there is no
/// GitHub App / Octokit client wired into the C# port. Implementations
/// today return <c>ServiceUnavailable</c> across the board so the deployed
/// Elsa activities receive the documented 503 (TS contract) instead of the
/// false-positive 200 the previous one-line stubs returned. Once a GitHub
/// App client lands (cross-ref github audit scope, finding 021), each
/// method gets a real Octokit-backed implementation.</para>
///
/// <para>TODO(epic-1, github-scope): wire Octokit.NET (or HttpClient) and
/// replace the <c>NotConfigured()</c> short-circuits.</para>
/// </summary>
public interface IGitHubEngineCallbackService
{
    /// <summary>Fetch <c>.tamma/config.json</c> from the repo's branch. (Finding 005)</summary>
    Task<GitHubCallbackResult<JsonElement>> ReadRepoConfigAsync(
        string owner, string repo, string branch, CancellationToken ct = default);

    /// <summary>List repository issues with PR filtering. (Finding 006)</summary>
    Task<GitHubCallbackResult<IssueListResult>> ListIssuesAsync(
        string owner, string repo, string state, string? labels, int perPage, int page,
        CancellationToken ct = default);

    /// <summary>Fetch Dependabot / CodeQL alerts. (Finding 007)</summary>
    Task<GitHubCallbackResult<SecurityAlertResult>> ListSecurityAlertsAsync(
        string owner, string repo, string alertType, CancellationToken ct = default);

    /// <summary>Post an issue comment. (Finding 008)</summary>
    Task<GitHubCallbackResult<IssueCommentResult>> PostIssueCommentAsync(
        string owner, string repo, int issueNumber, string body, CancellationToken ct = default);

    /// <summary>Add labels to an issue. (Finding 009)</summary>
    Task<GitHubCallbackResult<string[]>> AddIssueLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default);

    /// <summary>Remove a single label from an issue. (Finding 009)</summary>
    Task<GitHubCallbackResult<bool>> RemoveIssueLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken ct = default);

    /// <summary>Create a new issue. (Finding 010)</summary>
    Task<GitHubCallbackResult<CreatedIssueResult>> CreateIssueAsync(
        string owner, string repo, string title, string? body,
        string[]? labels, string[]? assignees, CancellationToken ct = default);

    /// <summary>Dispatch a GitHub Actions workflow. (Finding 011)</summary>
    Task<GitHubCallbackResult<DispatchedWorkflowResult>> TriggerCiAsync(
        string owner, string repo, string branchName, string workflowFile,
        Dictionary<string, string>? inputs, CancellationToken ct = default);
}

public sealed record IssueListResult(IReadOnlyList<JsonElement> Issues, int Total);
public sealed record SecurityAlertResult(IReadOnlyList<JsonElement> Dependabot, IReadOnlyList<JsonElement> CodeScanning);
public sealed record IssueCommentResult(long Id, string HtmlUrl);
public sealed record CreatedIssueResult(int Number, string HtmlUrl, string Title);
public sealed record DispatchedWorkflowResult(bool Dispatched, string WorkflowFile, string Branch);
