using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.Ci;

/// <summary>
/// Story 38 (Phase 1) — the single normalized, KEY-FREE result the CI endpoints
/// return. Only <see cref="CredentialSource"/> (the label <c>byok</c>/<c>platform</c>)
/// is ever present; the resolved token NEVER appears here (nor in any log or DCB
/// event). The mirroring engine-side wire type is
/// <c>Tamma.Activities.LlmCall.Models.CiCallResponse</c>.
/// </summary>
public sealed record CiMediationResult
{
    public bool Success { get; init; }

    /// <summary>"byok" | "platform" — the credential label, NEVER the token.</summary>
    public string? CredentialSource { get; init; }

    /// <summary>Operation-specific outcome the caller routes on ("Triggered" / "Read" / "Error").</summary>
    public string? Outcome { get; init; }

    // ── trigger-tests ──
    public CiTestRunDto? TestRun { get; init; }

    // ── build-status ──
    public CiBuildStatusDto? BuildStatus { get; init; }

    // ── failure-only (key-free) ──
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int? PlatformStatusCode { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>A key-free test-run projection. Mirrors <c>Core.Interfaces.TestRunResult</c>.</summary>
public sealed record CiTestRunDto
{
    public string RunId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests { get; init; }
    public int SkippedTests { get; init; }
    public double? CoveragePercentage { get; init; }
}

/// <summary>A key-free build-status projection. Mirrors <c>Core.Interfaces.BuildStatus</c>.</summary>
public sealed record CiBuildStatusDto
{
    public string Status { get; init; } = string.Empty;
    public string? BuildUrl { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}

/// <summary>
/// Story 38 (Phase 1) — the HTTP-status decision, mirroring the Story 38-1 git
/// mapper: success ⇒ 200; expected platform failure ⇒ 200 success:false (the
/// workflow branches on it, preserved platformStatusCode); REPO_NOT_AUTHORIZED ⇒
/// 403; CI_TOKEN_UNAVAILABLE ⇒ 503. A raw 5xx is NEVER produced.
/// </summary>
public static class CiMediationResultExtensions
{
    public static IResult ToHttpResult(this CiMediationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return Results.Ok(result);
        }

        return result.FailureCode switch
        {
            CiFailureCodes.RepoNotAuthorized => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            CiFailureCodes.TokenUnavailable => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Ok(result),
        };
    }
}
