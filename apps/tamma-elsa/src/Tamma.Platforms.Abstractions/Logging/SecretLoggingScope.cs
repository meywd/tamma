namespace Tamma.Platforms.Abstractions.Logging;

/// <summary>
/// Story 31-8 — utility helpers for redacting secret values before
/// they reach a logger / exception message / structured-event payload.
///
/// <para>Three helpers:</para>
/// <list type="bullet">
///   <item><see cref="Redact(string?)"/> — collapse a string to
///         <c>[redacted:N chars]</c>.</item>
///   <item><see cref="RedactSubstring"/> — replace every occurrence
///         of a known secret value inside a larger blob (HTTP body,
///         exception message). Used in the provisioner exception
///         handlers so a platform's verbose error surface can't echo
///         the value back.</item>
///   <item><see cref="EnsureNoLeak"/> — assertion-style throw
///         (callers MAY guard with a debug check; release builds
///         pay only the search cost). Used in tests + as a paranoia
///         step before emitting a structured log argument.</item>
/// </list>
/// </summary>
public static class SecretLoggingScope
{
    /// <summary>
    /// Standard redaction marker. Same format as
    /// <see cref="RedactedSecret.ToString"/> emits so logs from the
    /// two paths look identical.
    /// </summary>
    public static string Redact(string? value) =>
        $"[redacted:{(value?.Length ?? 0)} chars]";

    /// <summary>
    /// Replace every occurrence of <paramref name="secretValue"/>
    /// inside <paramref name="haystack"/> with the redaction marker.
    /// Returns <paramref name="haystack"/> unchanged when the secret
    /// value is empty or not present.
    /// </summary>
    public static string RedactSubstring(string haystack, string secretValue)
    {
        if (string.IsNullOrEmpty(haystack)) return haystack;
        if (string.IsNullOrEmpty(secretValue)) return haystack;
        return haystack.Replace(secretValue, Redact(secretValue));
    }

    /// <summary>
    /// Assertion-style guard — throws if <paramref name="haystack"/>
    /// contains <paramref name="secretValue"/>. Use in tests +
    /// debug-only code paths to catch a missed redaction at the
    /// source rather than after it ships.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the secret is found in the haystack.
    /// </exception>
    public static void EnsureNoLeak(string haystack, string secretValue)
    {
        if (string.IsNullOrEmpty(secretValue)) return;
        if (string.IsNullOrEmpty(haystack)) return;
        if (haystack.Contains(secretValue, StringComparison.Ordinal))
        {
            // Throw a redacted message — the throw itself shouldn't
            // leak the secret either.
            throw new InvalidOperationException(
                $"Secret leak detected: a {secretValue.Length}-char " +
                $"secret value appeared in a {haystack.Length}-char " +
                $"log/exception payload. Redact at the source.");
        }
    }
}
