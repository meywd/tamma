using System.Net;

namespace Tamma.Api.Services.Provisioning.Cranl;

/// <summary>
/// Typed exception thrown by <see cref="CranlApiClient"/> for non-success
/// responses. Carries the HTTP status code and the parsed
/// <c>{ "error": "..." }</c> body so callers can branch on transport-level
/// vs. resource-level failures (404 = "not found, retry pointless"; 429 =
/// "rate limited, back off"; 5xx = "transient, retry").
/// </summary>
public sealed class CranlApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Parsed error message from the response body. May be empty when the
    /// body wasn't JSON or didn't contain an <c>error</c> field.
    /// </summary>
    public string CranlError { get; }

    /// <summary>
    /// True when the failure is worth retrying (429, 502, 503, 504, network
    /// timeouts). False for 4xx that signal a client-side problem.
    /// </summary>
    public bool IsRetryable { get; }

    public CranlApiException(HttpStatusCode statusCode, string cranlError, string message)
        : base(message)
    {
        StatusCode = statusCode;
        CranlError = cranlError;
        IsRetryable = ClassifyRetryable(statusCode);
    }

    public CranlApiException(HttpStatusCode statusCode, string cranlError, string message, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
        CranlError = cranlError;
        IsRetryable = ClassifyRetryable(statusCode);
    }

    private static bool ClassifyRetryable(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        HttpStatusCode.RequestTimeout => true,
        _ => false
    };
}
