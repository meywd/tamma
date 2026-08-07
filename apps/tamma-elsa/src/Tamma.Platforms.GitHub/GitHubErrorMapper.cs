using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — maps GitHub HTTP responses to
/// <see cref="PlatformError"/>, preserving the coarse classification
/// the live path (<c>GitHubIntegrationService</c>'s status-prefixed
/// <c>"404: body"</c> strings, parsed by mediation's
/// <c>ParsePlatformStatus</c>) produces, so the P2 swap is
/// behavior-identical:
/// <list type="bullet">
///   <item>401 → <see cref="PlatformError.AuthExpired"/></item>
///   <item>403 with an exhausted <c>X-RateLimit-Remaining</c> or a
///         <c>Retry-After</c> header (GitHub's primary/secondary rate
///         limits ride 403, not 429) →
///         <see cref="PlatformError.RateLimited"/>; a plain 403 →
///         <see cref="PlatformError.PermissionDenied"/></item>
///   <item>404 → <see cref="PlatformError.NotFound"/></item>
///   <item>405 → <see cref="PlatformError.InvalidRequest"/> code
///         <c>"not_mergeable"</c> (GitHub answers 405 "Pull Request is
///         not mergeable" on the merge endpoint)</item>
///   <item>409 → <see cref="PlatformError.InvalidRequest"/> code
///         <c>"merge_conflict"</c> when the message names a
///         conflict/head-modification, else <c>"conflict"</c></item>
///   <item>422 → <see cref="PlatformError.InvalidRequest"/> code
///         <c>"already_exists"</c> when the message says so (branch
///         "Reference already exists", release tag exists), else
///         <c>"validation_failed"</c></item>
///   <item>429 → <see cref="PlatformError.RateLimited"/> with
///         Retry-After</item>
///   <item>5xx → <see cref="PlatformError.ServiceUnavailable"/></item>
///   <item>other 4xx → <see cref="PlatformError.InvalidRequest"/> with
///         the stringified status as code (the same numeric identity
///         the live path's status prefix carries)</item>
/// </list>
/// </summary>
internal static class GitHubErrorMapper
{
    /// <summary>
    /// Map a non-success HTTP response to a <see cref="PlatformError"/>.
    /// Reads the body for 4xx so the hint surfaces GitHub's
    /// <c>{"message": "..."}</c> detail.
    /// </summary>
    public static async Task<PlatformError> MapAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new PlatformError.AuthExpired();
            case HttpStatusCode.Forbidden:
                if (IsRateLimited(response))
                {
                    return new PlatformError.RateLimited(ResolveRetryAfter(response));
                }
                return new PlatformError.PermissionDenied();
            case HttpStatusCode.NotFound:
                return new PlatformError.NotFound();
            case HttpStatusCode.TooManyRequests:
                return new PlatformError.RateLimited(ResolveRetryAfter(response));
        }

        if (status >= 500 && status <= 599)
        {
            return new PlatformError.ServiceUnavailable();
        }

        if (status >= 400 && status < 500)
        {
            var message = await ReadMessageSafelyAsync(response, ct).ConfigureAwait(false);
            var code = ClassifyClientError(status, message);
            return new PlatformError.InvalidRequest(code, message);
        }

        // Defensive — should never be called on 2xx.
        return new PlatformError.Unknown($"unexpected status {status} from GitHub");
    }

    /// <summary>
    /// GitHub rides rate limiting on 403: primary limits set
    /// <c>X-RateLimit-Remaining: 0</c>; secondary/abuse limits send
    /// <c>Retry-After</c>.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not null) return true;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            var first = values.FirstOrDefault();
            if (long.TryParse(first, out var remaining) && remaining <= 0) return true;
        }
        return false;
    }

    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage response)
    {
        var parsed = ParseRetryAfter(response.Headers.RetryAfter);
        if (parsed is not null) return parsed;
        // Primary limits carry X-RateLimit-Reset (unix seconds).
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix))
        {
            var diff = DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero) return diff;
        }
        return null;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (header.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero) return diff;
        }
        return null;
    }

    private static async Task<string?> ReadMessageSafelyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string? body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(body)) return null;

        // GitHub error bodies are {"message":"…","errors":[…],…}.
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString();
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }
        return body.Length > 512 ? body[..512] : body;
    }

    internal static string ClassifyClientError(int status, string? message)
    {
        var lower = message?.ToLowerInvariant() ?? string.Empty;
        if (status == 405)
        {
            return "not_mergeable";
        }
        if (status == 409)
        {
            return lower.Contains("conflict") || lower.Contains("was modified")
                ? "merge_conflict"
                : "conflict";
        }
        if (status == 422)
        {
            return lower.Contains("already exists") ? "already_exists" : "validation_failed";
        }
        return status.ToString(CultureInfo.InvariantCulture);
    }
}
