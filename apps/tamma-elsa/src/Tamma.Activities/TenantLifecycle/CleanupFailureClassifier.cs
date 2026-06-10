using Tamma.Activities.Security;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Re-port of the per-step failure classifier originally shipped on
/// <c>CleanUpFailedTenantActivity</c> and lost when that activity was
/// decomposed into the H6 / Story 28-5 per-step Sequence.
///
/// <para>The decomposed <see cref="CleanupStepActivity"/> base now uses
/// <c>ex.GetType().Name</c> as the failure code (a regression from the
/// rich, fixed-vocabulary codes the dashboard + alerting groups on).
/// This classifier maps <c>(stepName, exception)</c> back to the
/// structured codes — <c>drop_schema_failed</c>,
/// <c>drop_role_failed</c>, <c>network_error</c>,
/// <c>permission_denied</c>, <c>evict_pool_failed</c>,
/// <c>cancelled</c>, <c>step_failed</c> — and produces a redacted,
/// length-bounded snippet that's safe to land in long-lived storage
/// (event payloads, <c>tenants.ProvisioningDetail</c>) and visible to
/// platform admins via SSE.</para>
///
/// <para><b>Stability contract:</b> failure codes are stable across
/// releases — dashboards and alerts group on these strings. New step
/// names require a new switch arm; new exception shapes within a step
/// fall through to the step-specific default (<c>drop_schema_failed</c>,
/// <c>drop_role_failed</c>) or the generic <c>step_failed</c>.</para>
///
/// <para><b>Snippet contract:</b> redacted via the supplied
/// <see cref="IErrorRedactor"/> (Bearer tokens, API keys, internal
/// URLs, DSNs scrubbed) and bounded to 200 chars. The full text always
/// lives in <c>ILogger</c>; this snippet exists for operator triage on
/// the dashboard, not for forensic detail. A <c>null</c> redactor
/// passes the raw message through (still bounded to 200 chars) — used
/// in unit tests; production wires the redactor.</para>
/// </summary>
public static class CleanupFailureClassifier
{
    /// <summary>
    /// Maximum length of the redacted snippet stored in event payloads
    /// + <c>tenants.ProvisioningDetail</c>. Keeps long-lived storage
    /// from accumulating verbose Postgres / network diagnostics.
    /// </summary>
    public const int MaxSnippetChars = 200;

    /// <summary>
    /// Classify a per-step cleanup-workflow failure into a stable code +
    /// a redacted, bounded snippet.
    ///
    /// <para>Step-name keyed primary classification: each step has its
    /// own well-known failure shapes so the operator UX matches what
    /// actually went wrong (e.g. a <c>drop-tenant-schema</c> permission
    /// denied is <c>permission_denied</c>, not the generic
    /// <c>drop_schema_failed</c>). Within a step, secondary
    /// classification by exception-type then by message-keyword catches
    /// the network-error and permission-denied cases.</para>
    /// </summary>
    /// <param name="stepName">The kebab-case step name as defined on
    /// <see cref="CleanupSteps"/> (<c>evict-pool</c>,
    /// <c>drop-tenant-schema</c>, <c>drop-tenant-role</c>,
    /// <c>soft-delete-cp-row</c>). Unknown step names fall through to
    /// the generic classifier.</param>
    /// <param name="ex">The exception thrown by the step body.</param>
    /// <param name="redactor">Optional redactor applied to the
    /// exception message before the snippet is bounded. Pass
    /// <c>null</c> in tests where redaction isn't under test;
    /// production wires <see cref="ErrorRedactor"/>.</param>
    /// <returns>A tuple <c>(FailureCode, RedactedSnippet)</c>.
    /// FailureCode is one of the fixed-vocabulary codes documented on
    /// the type; RedactedSnippet is at most
    /// <see cref="MaxSnippetChars"/> chars.</returns>
    public static (string FailureCode, string RedactedSnippet) ClassifyFailure(
        string stepName,
        Exception ex,
        IErrorRedactor? redactor)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var typeName = ex.GetType().Name;
        var rawMessage = ex.Message ?? string.Empty;

        // Trim the redacted message to a bounded snippet so the
        // long-lived store doesn't accumulate verbose Postgres /
        // network diagnostics. Full text stays in ILogger.
        var redacted = redactor?.Redact(rawMessage) ?? rawMessage;
        var snippet = redacted.Length > MaxSnippetChars
            ? redacted[..MaxSnippetChars]
            : redacted;

        // Step-specific classifiers — these dominate the operator UX
        // because the cleanup workflow has well-known failure shapes
        // per step. Unknown step names fall through to the generic
        // classifier so future steps still get a sensible code.
        var code = stepName switch
        {
            CleanupSteps.EvictPool => ClassifyEvictPoolFailure(typeName, rawMessage),
            CleanupSteps.DropSchema => ClassifySchemaFailure(typeName, rawMessage),
            CleanupSteps.DropRole => ClassifyRoleFailure(typeName, rawMessage),
            _ => ClassifyGeneric(typeName, rawMessage),
        };

        return (code, snippet);
    }

    /// <summary>
    /// Evict-pool step is intentionally simple — the resolver
    /// eviction's failure modes are coarse (pool-build race, NpgsqlDataSource
    /// dispose race, transient memory pressure). Network and
    /// permission shapes still take precedence so cross-step alerts
    /// fire on the same code.
    /// </summary>
    private static string ClassifyEvictPoolFailure(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeCancellation(typeName))
            return "cancelled";
        return "evict_pool_failed";
    }

    private static string ClassifySchemaFailure(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeAuth(rawMessage))
            return "permission_denied";
        if (LooksLikeCancellation(typeName))
            return "cancelled";
        return "drop_schema_failed";
    }

    private static string ClassifyRoleFailure(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeAuth(rawMessage))
            return "permission_denied";
        if (LooksLikeCancellation(typeName))
            return "cancelled";
        return "drop_role_failed";
    }

    private static string ClassifyGeneric(string typeName, string rawMessage)
    {
        if (LooksLikeNetwork(typeName, rawMessage))
            return "network_error";
        if (LooksLikeCancellation(typeName))
            return "cancelled";
        return "step_failed";
    }

    /// <summary>
    /// Network-shape detector — exception type names from the BCL +
    /// Npgsql plus message-keyword fallbacks for shapes that surface as
    /// <c>InvalidOperationException</c> with a connection-related
    /// message (Npgsql wraps a lot under that umbrella).
    /// </summary>
    private static bool LooksLikeNetwork(string typeName, string rawMessage)
    {
        if (string.Equals(typeName, "TimeoutException", StringComparison.Ordinal)
            || string.Equals(typeName, "SocketException", StringComparison.Ordinal)
            || string.Equals(typeName, "IOException", StringComparison.Ordinal)
            || string.Equals(typeName, "NpgsqlException", StringComparison.Ordinal))
        {
            return true;
        }

        // Match free-text shapes only when "connection" + a transport
        // verb co-occur — avoids false-positives on messages that
        // mention "connection" in passing (e.g. "connection pool full"
        // is not a network error).
        if (rawMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return true;
        return rawMessage.Contains("connection", StringComparison.OrdinalIgnoreCase)
            && (rawMessage.Contains("refused", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("reset", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Auth/permission-shape detector — Postgres surfaces "permission
    /// denied" via <c>InvalidOperationException</c>; "must be owner"
    /// is the canonical role-ownership message; "not allowed" catches
    /// generic 4xx-style refusals from libpq wrappers.
    /// </summary>
    private static bool LooksLikeAuth(string rawMessage) =>
        rawMessage.Contains("permission", StringComparison.OrdinalIgnoreCase)
        || rawMessage.Contains("must be owner", StringComparison.OrdinalIgnoreCase)
        || rawMessage.Contains("not allowed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cancellation detector. Step bodies should rethrow
    /// <see cref="OperationCanceledException"/> when the workflow's
    /// CT fires (cooperative shutdown), but if a step happens to
    /// surface one as a regular failure we still want it classified
    /// as <c>cancelled</c> rather than <c>step_failed</c> so dashboard
    /// queries don't bucket a clean shutdown as a real failure.
    /// </summary>
    private static bool LooksLikeCancellation(string typeName) =>
        string.Equals(typeName, "OperationCanceledException", StringComparison.Ordinal)
        || string.Equals(typeName, "TaskCanceledException", StringComparison.Ordinal);
}
