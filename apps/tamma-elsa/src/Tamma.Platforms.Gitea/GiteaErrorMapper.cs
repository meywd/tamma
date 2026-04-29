using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Gitea.Dtos;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Maps Gitea HTTP responses to <see cref="PlatformError"/> per
/// impl-plan §3:
/// <list type="bullet">
///   <item>401 → <see cref="PlatformError.AuthExpired"/></item>
///   <item>403 → <see cref="PlatformError.PermissionDenied"/></item>
///   <item>404 → <see cref="PlatformError.NotFound"/></item>
///   <item>422 → <see cref="PlatformError.InvalidRequest"/> (parses
///         the Gitea <c>{"message":...,"url":...}</c> body)</item>
///   <item>429 → <see cref="PlatformError.RateLimited"/> with
///         Retry-After</item>
///   <item>5xx → <see cref="PlatformError.ServiceUnavailable"/></item>
///   <item>other 4xx → <see cref="PlatformError.InvalidRequest"/></item>
///   <item>any other / network → <see cref="PlatformError.Unknown"/></item>
/// </list>
/// </summary>
internal static class GiteaErrorMapper
{
    /// <summary>
    /// Map an HTTP response (after non-success status) to a
    /// <see cref="PlatformError"/>. Reads the body for 4xx so the hint
    /// can surface Gitea's error message.
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
                return new PlatformError.PermissionDenied();
            case HttpStatusCode.NotFound:
                return new PlatformError.NotFound();
            case HttpStatusCode.TooManyRequests:
                return new PlatformError.RateLimited(ParseRetryAfter(response.Headers.RetryAfter));
        }

        if (status >= 500 && status <= 599)
        {
            return new PlatformError.ServiceUnavailable();
        }

        if (status >= 400 && status < 500)
        {
            var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            var (code, hint) = ExtractInvalidRequestDetails(status, body);
            return new PlatformError.InvalidRequest(code, hint);
        }

        // Defensive — should never be called on 2xx; treat as Unknown.
        return new PlatformError.Unknown(
            $"unexpected status {status} from Gitea");
    }

    /// <summary>
    /// Map a transport-level exception (network failure, DNS, TLS) to
    /// <see cref="PlatformError.ServiceUnavailable"/>. Used by
    /// <see cref="GiteaHttpClient"/> when <see cref="HttpRequestException"/>
    /// surfaces.
    /// </summary>
    public static PlatformError FromTransport(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PlatformError.ServiceUnavailable();
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

    private static async Task<string?> ReadBodySafelyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static (string Code, string? Hint) ExtractInvalidRequestDetails(
        int status, string? body)
    {
        // Default: code is the numeric status as string for stability.
        var defaultCode = status.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(body)) return (defaultCode, null);

        // Gitea returns either {"message":"…","url":"…"} or a plain
        // text string. Try JSON first.
        try
        {
            var dto = JsonSerializer.Deserialize<GiteaErrorDto>(body);
            if (dto is { Message: { Length: > 0 } msg })
            {
                // Heuristic codes — drivers benefit from stable
                // identifiers; conflict / merge-conflict / validation
                // are the patterns the brief expects callers to branch
                // on.
                var code = ClassifyMessage(msg, status, defaultCode);
                return (code, msg);
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return (defaultCode, body.Length > 512 ? body[..512] : body);
    }

    private static string ClassifyMessage(string message, int status, string defaultCode)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("merge")
            && (lower.Contains("conflict") || lower.Contains("not mergeable")))
        {
            return "merge_conflict";
        }
        if (lower.Contains("already exists"))
        {
            return "already_exists";
        }
        if (status == 422) return "validation_failed";
        if (status == 409) return "conflict";
        return defaultCode;
    }
}
