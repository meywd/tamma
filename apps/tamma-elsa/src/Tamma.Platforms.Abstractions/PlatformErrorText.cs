using System.Globalization;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Epic 31 P2 — projects a <see cref="PlatformError"/> into the
/// status-prefixed legacy error string (<c>"404: not found"</c>,
/// <c>"409: merge conflict"</c>, …) the pre-swap live GitHub path
/// (<c>GitHubIntegrationService</c>) surfaced and that the mediation
/// layer's coarse classifiers still read:
///
/// <list type="bullet">
///   <item><c>GitMediationService.ParsePlatformStatus</c> parses the
///         leading numeric prefix into <c>platformStatusCode</c>;</item>
///   <item>the ADL cores' <c>ClassifyError</c> helpers substring-match
///         status tokens ("403", "409", "not mergeable", …).</item>
/// </list>
///
/// Centralizing the projection here is what makes the P2 swap
/// behavior-identical: the driver's typed error becomes the SAME wire
/// string family the live path produced, so every downstream
/// classification (failure code, platform status, retry hints) lands
/// in the same coarse class. Pinned by
/// <c>PlatformErrorTextParityTests</c>.
/// </summary>
public static class PlatformErrorText
{
    /// <summary>
    /// The exact <see cref="PlatformError.InvalidRequest.Code"/> every
    /// driver uses for a verb its platform cannot perform (§4 of the
    /// Epic 31 plan). Exact-match only — anything else is a real
    /// failure, never "unsupported".
    /// </summary>
    public const string CapabilityUnsupportedCode = "capability_unsupported";

    /// <summary>True iff <paramref name="error"/> is the typed
    /// capability refusal (exact code match).</summary>
    public static bool IsCapabilityUnsupported(PlatformError error) =>
        error is PlatformError.InvalidRequest ir
        && string.Equals(ir.Code, CapabilityUnsupportedCode, StringComparison.Ordinal);

    /// <summary>
    /// Epic 31 review (F-medium) — the message prefix an Actions driver's
    /// <c>DispatchWorkflowAsync</c> uses when the platform ACCEPTED the
    /// dispatch (204) but the created run could not be correlated within
    /// the probe window. The run is (very likely) starting — a caller that
    /// treats this answer as a trigger FAILURE re-dispatches a run that is
    /// already executing (duplicate CI/agent runs) or escalates spuriously.
    /// Both mediation planes (agent-dispatch and CI) special-case it as
    /// success-without-a-correlated-run via
    /// <see cref="IsDispatchAcceptedCorrelationMiss(PlatformError)"/>.
    /// </summary>
    public const string DispatchAcceptedPrefix = "dispatch accepted (204)";

    /// <summary>True iff <paramref name="error"/> is the driver's typed
    /// "accepted but not correlated" answer (prefix match on the
    /// <see cref="PlatformError.Unknown"/> reason).</summary>
    public static bool IsDispatchAcceptedCorrelationMiss(PlatformError error) =>
        error is PlatformError.Unknown u
        && u.Reason.StartsWith(DispatchAcceptedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Project a typed error into the legacy status-prefixed string.
    /// </summary>
    public static string ToLegacyString(PlatformError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            PlatformError.AuthExpired => "401: authentication token expired or revoked",
            PlatformError.PermissionDenied => "403: permission denied",
            PlatformError.NotFound => "404: not found",
            PlatformError.RateLimited => "429: rate limit exceeded",
            // Upstream 5xx after the driver tried — same coarse class the
            // live path's "503: ..." produced (transient / PLATFORM_ERROR).
            PlatformError.ServiceUnavailable => "503: platform unavailable",
            PlatformError.InvalidRequest ir => FormatInvalidRequest(ir),
            PlatformError.Unknown u => u.Reason,
            _ => error.ToString() ?? "unknown platform error",
        };
    }

    private static string FormatInvalidRequest(PlatformError.InvalidRequest error)
    {
        var hint = string.IsNullOrWhiteSpace(error.Hint) ? error.Code : error.Hint;

        // Numeric codes carry their own status identity (the mapper's
        // "other 4xx" arm) — "400: {hint}".
        if (int.TryParse(error.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            && status is >= 100 and < 600)
        {
            return $"{status}: {hint}";
        }

        // Known driver codes map back onto the HTTP status the live path
        // would have prefixed, so downstream Contains()-classifiers land in
        // the same coarse class.
        return error.Code switch
        {
            "not_mergeable" => $"405: {hint}",
            "merge_conflict" or "conflict" => $"409: {hint}",
            "already_exists" or "validation_failed" => $"422: {hint}",
            // capability_unsupported (and any future non-numeric code) keeps
            // its code as the head token — no fake status prefix; mediation
            // special-cases the capability code BEFORE this projection.
            _ => $"{error.Code}: {hint}",
        };
    }
}
