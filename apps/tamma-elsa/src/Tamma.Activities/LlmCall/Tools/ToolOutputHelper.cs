using System.Text;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Shared utility for truncating tool output to the 50KB maximum and redacting sensitive content.
/// </summary>
public static class ToolOutputHelper
{
    /// <summary>Maximum output size in bytes (50KB).</summary>
    public const int MaxOutputBytes = 50 * 1024;

    /// <summary>
    /// Truncate output string to MaxOutputBytes UTF-8 size. If truncated, appends a suffix
    /// indicating the total size.
    /// </summary>
    /// <param name="output">Raw tool output.</param>
    /// <param name="logger">Optional logger to emit truncation warning.</param>
    /// <param name="toolName">Tool name for log context.</param>
    /// <param name="toolCallId">Tool call ID for log context.</param>
    /// <returns>Truncated output string.</returns>
    public static string Truncate(
        string output,
        ILogger? logger = null,
        string? toolName = null,
        string? toolCallId = null)
    {
        if (string.IsNullOrEmpty(output))
            return output ?? string.Empty;

        // Always redact secrets from output before returning (truncated or not)
        output = RedactSecrets(output);

        var totalBytes = Encoding.UTF8.GetByteCount(output);
        if (totalBytes <= MaxOutputBytes)
            return output;

        // Reserve space for the suffix
        const int suffixReserve = 120;
        var targetBytes = MaxOutputBytes - suffixReserve;

        // Binary-search-like approach: scale down char count proportionally
        var charCount = (int)((long)output.Length * targetBytes / totalBytes);

        // Fine-tune to ensure we fit
        while (charCount > 0 && Encoding.UTF8.GetByteCount(output.AsSpan(0, charCount)) > targetBytes)
        {
            charCount = (int)(charCount * 0.95);
        }

        // Ensure we don't cut in the middle of a surrogate pair
        if (charCount > 0 && char.IsHighSurrogate(output[charCount - 1]))
            charCount--;

        var truncatedBytes = Encoding.UTF8.GetByteCount(output.AsSpan(0, charCount));

        logger?.LogWarning(
            "Tool output truncated: {ToolName} {ToolCallId} original={OriginalSizeBytes}B, truncated={TruncatedSizeBytes}B",
            toolName, toolCallId, totalBytes, truncatedBytes);

        return string.Concat(
            output.AsSpan(0, charCount),
            $"\n[truncated: {totalBytes} bytes total, showing first {truncatedBytes} bytes]");
    }

    /// <summary>
    /// Redact common secret patterns from output (API keys, tokens, passwords, PEM keys, JWTs, etc.).
    /// </summary>
    /// <param name="output">Raw output that may contain secrets.</param>
    /// <returns>Output with sensitive values replaced by [REDACTED].</returns>
    public static string RedactSecrets(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output ?? string.Empty;

        var redacted = output;

        // OpenAI keys: sk-proj-..., sk-...
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"sk-[A-Za-z0-9_\-]{20,}", "[REDACTED]");

        // AWS Access Key IDs: AKIA...
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"AKIA[0-9A-Z]{16}", "[REDACTED]");

        // GitHub tokens: ghp_, gho_, ghu_, ghs_, ghr_
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"gh[pousr]_[A-Za-z0-9_]{36,}", "[REDACTED]");

        // GitLab personal access tokens: glpat-...
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"glpat-[A-Za-z0-9\-_]{20,}", "[REDACTED]");

        // Slack tokens: xoxb-..., xoxp-...
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"xox[bp]-[A-Za-z0-9\-]{24,}", "[REDACTED]");

        // JWT tokens (three base64url segments separated by dots)
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_\-]+", "[REDACTED]");

        // PEM private keys (multi-line)
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----[\s\S]*?-----END\s+(RSA\s+)?PRIVATE\s+KEY-----",
            "[REDACTED:PEM_PRIVATE_KEY]");

        // Connection string passwords: Password=...; or Pwd=...;
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"(?i)(Password|Pwd)\s*=\s*[^;]+", "$1=[REDACTED]");

        // Bearer tokens in headers: Authorization: Bearer ...
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted, @"(?i)(Authorization:\s*Bearer\s+)\S+", "$1[REDACTED]");

        // Generic key=value patterns: api_key, token, secret, credential followed by = or :
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"(?i)(api[_-]?key|token|password|secret|credential)\s*[=:]\s*\S+",
            "$1=[REDACTED]");

        return redacted;
    }
}
