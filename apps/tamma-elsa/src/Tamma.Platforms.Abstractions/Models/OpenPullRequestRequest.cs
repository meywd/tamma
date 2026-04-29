namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Input shape for
/// <see cref="IGitPlatformClient.OpenPullRequestAsync"/>. Pulled out
/// into a record because the call has more than three parameters.
/// </summary>
public sealed record OpenPullRequestRequest(
    string Owner,
    string RepoName,
    string Title,
    string SourceBranch,
    string TargetBranch,
    string? Body = null,
    bool IsDraft = false);

/// <summary>
/// Input shape for
/// <see cref="IGitPlatformClient.MergePullRequestAsync"/>.
///
/// <para><see cref="MergeMethod"/> values are normalized — drivers map
/// to the platform vocabulary. Drivers MUST return
/// <see cref="PlatformError.InvalidRequest"/> with a stable code if
/// the requested method is unsupported (e.g. squash-merge on a
/// platform that doesn't have it).</para>
/// </summary>
public sealed record MergePullRequestRequest(
    string Owner,
    string RepoName,
    string PrNumber,
    MergeMethod Method,
    string? CommitMessage = null);

public enum MergeMethod
{
    Merge = 1,
    Squash = 2,
    Rebase = 3,
}

/// <summary>
/// Input for <see cref="IGitPlatformClient.CreateBranchAsync"/>.
/// </summary>
public sealed record CreateBranchRequest(
    string Owner,
    string RepoName,
    string NewBranchName,
    string FromSha);

/// <summary>
/// Input for
/// <see cref="IGitPlatformClient.CreatePullRequestReviewCommentAsync"/>
/// — file/line-anchored review comment.
/// </summary>
public sealed record CreatePullRequestReviewCommentRequest(
    string Owner,
    string RepoName,
    string PrNumber,
    string Path,
    int Line,
    string Body,
    string CommitSha);

/// <summary>
/// Input for <see cref="IGitPlatformClient.RegisterWebhookAsync"/>.
///
/// <para><see cref="Secret"/> is the value the platform will sign
/// outbound webhook bodies with (HMAC) or pass back as a static
/// header (GitLab). For GitLab platforms the driver places it in the
/// <c>token</c> field; for HMAC-style platforms in the secret field.
/// Tamma generates this once per webhook registration and stores it
/// alongside the registration id (Story 31-7 wires the verifier).</para>
/// </summary>
public sealed record RegisterWebhookRequest(
    string Owner,
    string RepoName,
    string DeliveryUrl,
    IReadOnlyList<string> Events,
    string Secret,
    bool Active = true);

/// <summary>
/// Input for <see cref="IGitPlatformClient.GetFileContentAsync"/>.
/// </summary>
public sealed record GetFileContentRequest(
    string Owner,
    string RepoName,
    string Path,
    string Ref);
