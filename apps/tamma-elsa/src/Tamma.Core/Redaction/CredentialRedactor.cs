using System.Text.RegularExpressions;

namespace Tamma.Core.Redaction;

/// <summary>
/// Wave C.4 — scrubs common credential patterns from error strings
/// before they land in the DCB event store. Complements
/// <c>Tamma.Core.Logging.LogSanitizer</c> (which only guards against
/// log-injection CRLF/tab control characters) with a proper
/// secret-stripping pass for Bearer tokens, API keys, Basic-auth in URLs,
/// and Postgres-style connection strings.
///
/// <para>The emitted event's <c>data.lastError</c> / <c>data.finalError</c>
/// field goes through here before being serialised. A lost stack trace
/// is strictly preferable to a leaked production credential.</para>
///
/// <para>Non-goals: full DLP. We cover the credential patterns commonly
/// surfaced by <see cref="System.Net.Http.HttpRequestException"/> and
/// <c>Npgsql.NpgsqlException</c> message text. Tenant-configurable
/// redaction rules (Story 28-X sanitisation pipeline) remain
/// orthogonal.</para>
/// </summary>
public static class CredentialRedactor
{
    /// <summary>Cap on emitted string length. Keeps JSON payloads tidy.</summary>
    public const int MaxLength = 1024;

    /// <summary>Placeholder token replacing matched credentials.</summary>
    public const string Placeholder = "[REDACTED]";

    // Bearer / bearer <value> — GitHub App tokens, OAuth access tokens,
    // API-key header bodies. The value is any 8+ char blob of non-space
    // non-quote characters.
    private static readonly Regex BearerToken = new(
        @"(?<prefix>[Bb]earer\s+)(?<value>[A-Za-z0-9\-_\.=+/~]{8,})",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // key=value / KEY=VALUE assignments where the key looks credential-ish.
    // Covers api_key, apikey, password, pwd, secret, token,
    // x-api-key, authorization. Accepts quoted + unquoted values AND
    // accepts quoted keys (JSON form like "password":"…").
    private static readonly Regex KeyValuePairAssignment = new(
        @"(?<key>[""']?(?i:api[_-]?key|apikey|password|passwd|pwd|secret|token|x[_-]?api[_-]?key)[""']?\s*[=:]\s*)" +
        @"(?<q>[""']?)(?<value>[^""'\s,;)\]}]+)\k<q>",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // Tamma API key / session prefix tokens — ship with a recognisable
    // prefix so we can redact them even when the surrounding key name
    // doesn't match the credential heuristic.
    private static readonly Regex TammaSecretPrefix = new(
        @"\b(?:tamma_sk_|sk_live_|sk_test_|ghp_|github_pat_|xoxb-|xoxp-|AKIA)[A-Za-z0-9_\-]{6,}",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // Basic-auth userinfo inside URL: scheme://user:pass@host
    private static readonly Regex UrlBasicAuth = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+\-\.]*://)(?<userinfo>[^/\s@]+:[^/\s@]+)@",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // Control characters (CR/LF/TAB) — log-injection vector. Replace
    // with single-char escapes so the payload stays single-line.
    private static readonly Regex ControlChars = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Redact credential-shaped substrings and return a JSON-safe,
    /// length-bounded string. Null/empty inputs return the empty string.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var s = value;

        // Order matters: URL userinfo first (before KV-assignment patterns
        // could eat the :pass@ fragment), then Bearer, then generic KV.
        s = UrlBasicAuth.Replace(s, m => $"{m.Groups["scheme"].Value}{Placeholder}@");
        s = BearerToken.Replace(s, m => $"{m.Groups["prefix"].Value}{Placeholder}");
        s = KeyValuePairAssignment.Replace(s, m =>
        {
            var q = m.Groups["q"].Value;
            return $"{m.Groups["key"].Value}{q}{Placeholder}{q}";
        });
        // Last pass: redact any known-shape secret prefix the KV pattern
        // missed (e.g. when the token appears bare in a message body).
        s = TammaSecretPrefix.Replace(s, Placeholder);

        // Strip control chars last so we don't break regex anchors above.
        s = ControlChars.Replace(s, " ");

        // Collapse CR/LF explicitly — they survive the control-char regex
        // as \r(\x0D)/\n(\x0A) are the exact values we want removed from
        // JSON-embedded strings.
        s = s.Replace("\r", " ").Replace("\n", " ");

        if (s.Length > MaxLength)
        {
            s = s[..MaxLength];
        }

        return s;
    }
}
