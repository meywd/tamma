namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Story 31-13 — input shapes for the PR lifecycle verbs
/// (<see cref="IGitPlatformClient.RequestReviewersAsync"/>,
/// <see cref="IGitPlatformClient.AddPullRequestLabelsAsync"/>,
/// <see cref="IGitPlatformClient.SetDraftAsync"/>). Close/reopen and
/// single-label removal take positional args on the interface (three or four
/// scalars); the multi-value verbs get records, matching the existing
/// convention on <see cref="OpenPullRequestRequest"/>.
/// </summary>
public sealed record RequestReviewersRequest(
    string Owner,
    string RepoName,
    string PrNumber,
    IReadOnlyList<string> Reviewers,
    IReadOnlyList<string>? TeamReviewers = null);

/// <summary>Input for <see cref="IGitPlatformClient.AddPullRequestLabelsAsync"/>.</summary>
public sealed record AddPullRequestLabelsRequest(
    string Owner,
    string RepoName,
    string PrNumber,
    IReadOnlyList<string> Labels);

/// <summary>
/// Input for <see cref="IGitPlatformClient.SetDraftAsync"/>. On GitHub this is a
/// GraphQL-only mutation (<c>convertPullRequestToDraft</c> /
/// <c>markPullRequestReadyForReview</c>); drivers without the
/// <see cref="PlatformCapability.PrLifecycle"/> flag return
/// <c>capability_unsupported</c>.
/// </summary>
public sealed record SetPullRequestDraftRequest(
    string Owner,
    string RepoName,
    string PrNumber,
    bool Draft);
