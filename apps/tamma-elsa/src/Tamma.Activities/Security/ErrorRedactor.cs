using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Security;

/// <summary>
/// Redacts sensitive information from error bodies before logging or storage.
///
/// Detects and replaces:
/// - Database connection-string DSNs: <c>postgres(ql)?://user:pass@host/db</c>,
///   <c>mysql://...</c>, <c>mongodb(+srv)?://...</c>, <c>redis://...</c>,
///   <c>amqp(s)?://...</c> — must run BEFORE the URL/IP scrub so the more
///   specific pattern wins (PF-S7).
/// - Bearer tokens: <c>Bearer [A-Za-z0-9._-]+</c>
/// - OpenAI API keys: <c>sk-[A-Za-z0-9]{20,}</c>
/// - Anthropic API keys: <c>sk-ant-[A-Za-z0-9-]+</c>
/// - Generic API keys: <c>key-[A-Za-z0-9]+</c>
/// - Base64 blobs (40+ chars): long base64 sequences that may be encoded credentials
/// - Internal/private URLs: localhost, 127.0.0.1, 10.x, 172.16-31.x, 192.168.x
/// - .NET stack traces: multi-line <c>at Namespace.Class.Method</c> blocks
///
/// All methods are thread-safe (no mutable state, compiled regexes are thread-safe).
/// Never throws exceptions.
///
/// IMPORTANT: Anthropic key regex must be applied before OpenAI key regex
/// to prevent partial matches (sk-ant- starts with sk-). The DSN regex must
/// run before the URL/IP regex so a Postgres DSN with an internal host gets
/// the [REDACTED-DSN] marker (with credentials) instead of just the hostname.
/// </summary>
public sealed class ErrorRedactor : IErrorRedactor
{
    private readonly ILogger<ErrorRedactor>? _logger;

    /// <summary>
    /// Database / message-broker connection-string DSNs that embed
    /// <c>user:password@host</c>. Schemes covered: postgres / postgresql,
    /// mysql, mongodb, mongodb+srv, redis, amqp, amqps. The username
    /// segment forbids <c>:</c>/<c>@</c>/whitespace and the password
    /// forbids <c>@</c>/whitespace, which lets URL-encoded special chars
    /// (<c>%40</c>, <c>%3A</c>) and common punctuation in the password
    /// pass through the matcher. PF-S7.
    /// </summary>
    private static readonly Regex DatabaseDsnRe = new(
        @"\b(?:postgres|postgresql|mysql|mongodb\+srv|mongodb|redis|amqps|amqp)://[^:@\s/]+:[^@\s]+@\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Bearer tokens: <c>Bearer [token]</c>
    /// </summary>
    private static readonly Regex BearerTokenRe = new(
        @"Bearer\s+[A-Za-z0-9._\-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Anthropic API keys: <c>sk-ant-[chars]</c>
    /// Must be checked BEFORE OpenAI key regex to avoid partial match.
    /// </summary>
    private static readonly Regex AnthropicKeyRe = new(
        @"sk-ant-[A-Za-z0-9\-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// OpenAI API keys: <c>sk-[20+ chars]</c>
    /// </summary>
    private static readonly Regex OpenAiKeyRe = new(
        @"sk-[A-Za-z0-9]{20,}",
        RegexOptions.Compiled);

    /// <summary>
    /// Generic API keys: <c>key-[chars]</c>
    /// </summary>
    private static readonly Regex GenericKeyRe = new(
        @"key-[A-Za-z0-9]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Base64-encoded blobs (40+ chars) that may be encoded credentials.
    /// </summary>
    private static readonly Regex Base64BlobRe = new(
        @"[A-Za-z0-9+/]{40,}={0,2}",
        RegexOptions.Compiled);

    /// <summary>
    /// Internal/private network URLs: localhost, 127.0.0.1, 10.x.x.x, 172.16-31.x.x, 192.168.x.x
    /// </summary>
    private static readonly Regex InternalUrlRe = new(
        @"https?://(?:localhost|127\.0\.0\.1|10\.\d+\.\d+\.\d+|172\.(?:1[6-9]|2\d|3[01])\.\d+\.\d+|192\.168\.\d+\.\d+)[^\s]*",
        RegexOptions.Compiled);

    /// <summary>
    /// .NET stack traces: lines starting with "   at Namespace.Class.Method(...) in ... :line N"
    /// </summary>
    private static readonly Regex StackTraceRe = new(
        @"(\s+at\s+[\w.<>+\[\],]+\(.*?\)(\s+in\s+.+:\s*line\s+\d+)?(\r?\n)?)+",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Replacement text for redacted content.
    /// </summary>
    private const string Redacted = "[REDACTED]";

    /// <summary>
    /// Distinct marker for redacted DSNs so log readers can grep for
    /// "leaked connection string" candidates without running the regex
    /// themselves. PF-S7.
    /// </summary>
    private const string DsnRedacted = "[REDACTED-DSN]";

    /// <summary>
    /// Replacement text for redacted stack traces (distinct for clarity).
    /// </summary>
    private const string StackTraceRedacted = "[STACK TRACE REDACTED]\n";

    /// <summary>
    /// Ordered list of redaction rules. Order matters:
    /// 1. <c>DatabaseDsn</c> first — more specific than <c>InternalUrl</c>;
    ///    a Postgres DSN against <c>10.x</c> would otherwise lose the host
    ///    but keep the credentials.
    /// 2. Anthropic keys before OpenAI keys (sk-ant- starts with sk-).
    /// </summary>
    private static readonly IReadOnlyList<(string Name, Regex Pattern, string Replacement)> RedactionRules =
        new List<(string, Regex, string)>
        {
            ("DatabaseDsn", DatabaseDsnRe, DsnRedacted),
            ("AnthropicKey", AnthropicKeyRe, Redacted),
            ("OpenAiKey", OpenAiKeyRe, Redacted),
            ("BearerToken", BearerTokenRe, Redacted),
            ("GenericKey", GenericKeyRe, Redacted),
            ("Base64Blob", Base64BlobRe, Redacted),
            ("InternalUrl", InternalUrlRe, Redacted),
            ("StackTrace", StackTraceRe, StackTraceRedacted),
        };

    /// <summary>
    /// Creates a new <see cref="ErrorRedactor"/> instance.
    /// </summary>
    /// <param name="logger">Optional logger. Logs at INFO when redaction occurs (pattern names only, never values).</param>
    public ErrorRedactor(ILogger<ErrorRedactor>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Redact(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody))
            return errorBody;

        try
        {
            var result = errorBody;
            var matchedPatterns = new List<string>();

            foreach (var (name, pattern, replacement) in RedactionRules)
            {
                var before = result;
                result = pattern.Replace(result, replacement);
                if (result != before)
                {
                    matchedPatterns.Add(name);
                }
            }

            if (matchedPatterns.Count > 0)
            {
                _logger?.LogInformation(
                    "Error redaction performed: {RedactionCount} patterns matched: {RedactedPatterns}",
                    matchedPatterns.Count, string.Join(", ", matchedPatterns));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                "Error during redaction: {ExceptionMessage}",
                ex.Message);
            return "[Error during redaction]";
        }
    }
}
