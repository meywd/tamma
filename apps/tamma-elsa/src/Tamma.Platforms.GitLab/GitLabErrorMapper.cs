using System.Net;
using System.Text.Json;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 §Step 3 — map HTTP responses to <see cref="PlatformError"/>.
///
/// <para>GitLab error bodies come in two shapes:</para>
/// <list type="bullet">
///   <item><c>{ "message": "..." }</c> — single string error.</item>
///   <item><c>{ "message": { "field": [ "rule" ] } }</c> — validation
///         failure with per-field rules.</item>
///   <item><c>{ "error": "...", "error_description": "..." }</c> —
///         OAuth-style error (rare on the v4 REST API).</item>
/// </list>
///
/// <para>The mapper extracts a stable <c>code</c> from the response shape
/// (e.g. <c>"validation_failed"</c> for the multi-field shape) so callers
/// can branch on it without parsing strings.</para>
/// </summary>
internal static class GitLabErrorMapper
{
    /// <summary>
    /// Map an HTTP status + optional body bytes to a
    /// <see cref="PlatformError"/>. The <paramref name="retryAfter"/>
    /// is taken from the <c>Retry-After</c> header if the caller pre-parsed it.
    /// </summary>
    public static PlatformError Map(HttpStatusCode status, string? body, TimeSpan? retryAfter)
    {
        return status switch
        {
            HttpStatusCode.Unauthorized => new PlatformError.AuthExpired(),
            HttpStatusCode.Forbidden => new PlatformError.PermissionDenied(),
            HttpStatusCode.NotFound => new PlatformError.NotFound(),
            (HttpStatusCode)429 => new PlatformError.RateLimited(retryAfter),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict
                => MapInvalidRequest(status, body),
            _ when (int)status >= 500 => new PlatformError.ServiceUnavailable(),
            _ => new PlatformError.Unknown($"http_{(int)status}: {Truncate(body, 200)}"),
        };
    }

    private static PlatformError.InvalidRequest MapInvalidRequest(HttpStatusCode status, string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new PlatformError.InvalidRequest(
                CodeForStatus(status),
                $"HTTP {(int)status}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // OAuth-style: { "error": "...", "error_description": "..." }
            if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
            {
                var code = errProp.GetString() ?? CodeForStatus(status);
                var hint = root.TryGetProperty("error_description", out var descProp) &&
                           descProp.ValueKind == JsonValueKind.String
                    ? descProp.GetString()
                    : null;
                return new PlatformError.InvalidRequest(code, hint);
            }

            // GitLab default: { "message": "..." } or { "message": { ... } }
            if (root.TryGetProperty("message", out var msgProp))
            {
                if (msgProp.ValueKind == JsonValueKind.String)
                {
                    return new PlatformError.InvalidRequest(
                        CodeForStatus(status),
                        msgProp.GetString());
                }
                if (msgProp.ValueKind == JsonValueKind.Object)
                {
                    // Validation: aggregate first error rule for hint, code = "validation_failed"
                    var hint = SerializeFieldErrors(msgProp);
                    return new PlatformError.InvalidRequest("validation_failed", hint);
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to default
        }

        return new PlatformError.InvalidRequest(
            CodeForStatus(status),
            Truncate(body, 200));
    }

    private static string CodeForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "bad_request",
        HttpStatusCode.UnprocessableEntity => "unprocessable_entity",
        HttpStatusCode.Conflict => "conflict",
        _ => $"http_{(int)status}",
    };

    private static string SerializeFieldErrors(JsonElement obj)
    {
        // Flatten { "branch": ["already exists"], "title": ["can't be blank"] }
        // → "branch: already exists; title: can't be blank"
        var parts = new List<string>();
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        parts.Add($"{prop.Name}: {item.GetString()}");
                    }
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                parts.Add($"{prop.Name}: {prop.Value.GetString()}");
            }
        }
        return parts.Count > 0 ? string.Join("; ", parts) : "validation failed";
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
