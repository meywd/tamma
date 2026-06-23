using System.Net.Http.Json;

namespace Tamma.Activities.ADL;

/// <summary>
/// Thin seam over the engine issue callbacks (<c>POST /api/engine/issue-comment</c>,
/// <c>POST /api/engine/issue-labels</c>, <c>DELETE /api/engine/issue-labels/...</c>)
/// used by <see cref="UpdateIssueStatusActivity"/>. Extracting the I/O behind an
/// interface lets the activity's <c>ExecuteCoreAsync</c> orchestration (retry,
/// outcome mapping, no-false-success) be unit-tested against a mock without a
/// live HTTP endpoint or Octokit. The default implementation
/// (<see cref="HttpIssueCallbackClient"/>) preserves the existing engine-callback
/// integration path — steps still never call a vendor API directly.
///
/// <para>Each method returns the typed callback result so a failure surfaces as a
/// loud <c>!Success</c> rather than a swallowed exception. The single label-add
/// call is atomic; each label-remove is independent so a partial label failure
/// does not force the comment to be re-posted (duplicate-comment hazard).</para>
/// </summary>
public interface IIssueCallbackClient
{
    Task<IssueCallbackResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct = default);
    Task<IssueCallbackResult> AddLabelsAsync(string repository, int issueNumber, string[] labels, CancellationToken ct = default);
    Task<IssueCallbackResult> RemoveLabelAsync(string repository, int issueNumber, string label, CancellationToken ct = default);
}

/// <summary>Typed result of a single engine issue callback. Never throws — a
/// transport / non-2xx failure becomes <c>Success=false</c> with a reason.</summary>
public sealed record IssueCallbackResult(bool Success, string? Error = null)
{
    public static IssueCallbackResult Ok() => new(true);
    public static IssueCallbackResult Fail(string error) => new(false, error);
}

/// <summary>
/// Default <see cref="IIssueCallbackClient"/> — calls the existing
/// <c>/api/engine/*</c> callback endpoints via <see cref="IHttpClientFactory"/>,
/// using the configured <c>Engine:CallbackUrl</c>. This is the transitional
/// integration path the build-out keeps (no new direct vendor calls); Story 38-1
/// later re-points this at the <c>PATCH /api/v1/git/{repo}/issues/{n}</c>
/// git-mediation endpoint.
/// </summary>
public sealed class HttpIssueCallbackClient : IIssueCallbackClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public HttpIssueCallbackClient(HttpClient httpClient, string callbackUrl)
    {
        _httpClient = httpClient;
        _baseUrl = callbackUrl.TrimEnd('/');
    }

    public async Task<IssueCallbackResult> PostCommentAsync(string repository, int issueNumber, string body, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/engine/issue-comment",
            new { repository, issueNumber, body }, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? IssueCallbackResult.Ok()
            : IssueCallbackResult.Fail($"issue-comment {(int)response.StatusCode}");
    }

    public async Task<IssueCallbackResult> AddLabelsAsync(string repository, int issueNumber, string[] labels, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/api/engine/issue-labels",
            new { repository, issueNumber, labels }, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? IssueCallbackResult.Ok()
            : IssueCallbackResult.Fail($"issue-labels {(int)response.StatusCode}");
    }

    public async Task<IssueCallbackResult> RemoveLabelAsync(string repository, int issueNumber, string label, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync(
            $"{_baseUrl}/api/engine/issue-labels/{Uri.EscapeDataString(repository)}/{issueNumber}/{Uri.EscapeDataString(label)}",
            ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? IssueCallbackResult.Ok()
            : IssueCallbackResult.Fail($"issue-labels-delete {(int)response.StatusCode}");
    }
}
