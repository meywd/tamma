namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Snapshot of the platform's rate-limit headers as of the last call.
/// Drivers populate this from response headers (GitHub
/// <c>X-RateLimit-*</c>; GitLab <c>RateLimit-*</c>; etc.) and expose it
/// to callers that want to throttle proactively rather than waiting
/// for a 429.
/// </summary>
/// <param name="Limit">Max requests per window. Null when unknown.</param>
/// <param name="Remaining">Requests remaining in current window. Null when unknown.</param>
/// <param name="ResetsAt">When the window resets. Null when unknown.</param>
public sealed record RateLimitInfo(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetsAt);
