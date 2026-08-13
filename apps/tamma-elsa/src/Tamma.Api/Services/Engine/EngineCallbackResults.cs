using System.Text.Json;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Result envelope used by the engine callback platform-proxy endpoints
/// (<see cref="IEngineGitCallbackService"/>). Epic 31 P3 (4/4): moved out of
/// the deleted <c>IGitHubEngineCallbackService.cs</c> — the record name is kept
/// (with its <c>NotConfigured</c>/<c>github_client_not_configured</c> wire
/// code) because the deployed Elsa activities branch on that exact legacy
/// contract; only the MEANING moved: "not configured" now says "no platform
/// driver resolved", not "no GitHub App wired".
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

public sealed record IssueListResult(IReadOnlyList<JsonElement> Issues, int Total);
public sealed record SecurityAlertResult(IReadOnlyList<JsonElement> Dependabot, IReadOnlyList<JsonElement> CodeScanning);
public sealed record IssueCommentResult(long Id, string HtmlUrl);
public sealed record CreatedIssueResult(int Number, string HtmlUrl, string Title);
