using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.Security;

/// <summary>
/// Content sanitizer implementing defense-in-depth against prompt injection,
/// HTML injection, invisible Unicode character attacks (including CVE-2021-42574
/// bidi overrides), and null byte injection.
///
/// Ported from TypeScript: packages/shared/src/security/content-sanitizer.ts
///
/// Pipeline for <see cref="SanitizeInput"/>:
///   1. Null byte removal (always, even when disabled)
///   2. If enabled: stripHtml -> removeZeroWidthChars -> detectPromptInjection (NFKD-normalized)
///
/// Pipeline for <see cref="SanitizeOutput"/>:
///   1. Null byte removal (always, even when disabled)
///   2. If enabled: removeZeroWidthChars -> stripHtmlPreserveCode
///
/// All methods are thread-safe (no mutable instance state, compiled regexes are thread-safe).
/// </summary>
public sealed class ContentSanitizer : IContentSanitizer
{
    private readonly ILogger<ContentSanitizer>? _logger;
    private readonly bool _enabled;
    private readonly IReadOnlyList<string> _extraInjectionPatterns;

    /// <summary>
    /// Performance warning threshold in milliseconds.
    /// If sanitization exceeds this, a warning is logged.
    /// </summary>
    private const double PerformanceThresholdMs = 5.0;

    /// <summary>
    /// Pre-compiled regex for null byte removal.
    /// Applied unconditionally, even when sanitizer is disabled (hard safety requirement).
    /// </summary>
    private static readonly Regex NullByteRe = new(
        "\0",
        RegexOptions.Compiled);

    /// <summary>
    /// Pre-compiled regex for zero-width and invisible Unicode characters.
    /// Covers 20+ code points matching the TypeScript ZERO_WIDTH_CHARS_RE:
    /// U+0000 (null), U+00AD (soft hyphen), U+034F (combining grapheme joiner),
    /// U+200B-U+200F (zero-width space/joiner/marks), U+202A-U+202E (bidi overrides,
    /// including CVE-2021-42574), U+2028-U+2029 (line/paragraph separators),
    /// U+2060-U+2064 (word joiner, invisible operators), U+2066-U+2069 (bidi isolates),
    /// U+FEFF (BOM), U+FFFC (object replacement character).
    /// </summary>
    private static readonly Regex ZeroWidthCharsRe = new(
        "[\u0000\u00AD\u034F\u200B-\u200F\u202A-\u202E\u2028\u2029\u2060-\u2064\u2066-\u2069\uFEFF\uFFFC]",
        RegexOptions.Compiled);

    /// <summary>
    /// Built-in prompt injection detection patterns organized by category.
    ///
    /// Each entry is (category, pattern_string) where pattern_string is matched
    /// as a case-insensitive substring against NFKD-normalized input.
    ///
    /// IMPORTANT: These are heuristic patterns for defense-in-depth.
    /// They will produce false positives on benign input that coincidentally matches.
    /// Warnings inform the caller; they do not block execution.
    /// </summary>
    private static readonly IReadOnlyList<(string Category, string Pattern)> BuiltinInjectionPatterns =
        new List<(string, string)>
        {
            // Category 1: Instruction override
            ("instruction_override", "ignore previous instructions"),
            ("instruction_override", "ignore all previous instructions"),
            ("instruction_override", "ignore the above"),
            ("instruction_override", "disregard above"),
            ("instruction_override", "disregard previous"),
            ("instruction_override", "forget your instructions"),
            ("instruction_override", "forget all instructions"),
            ("instruction_override", "override your instructions"),
            ("instruction_override", "new instructions:"),
            ("instruction_override", "ignore prior instructions"),

            // Category 2: Role hijacking
            ("role_hijacking", "you are now"),
            ("role_hijacking", "act as"),
            ("role_hijacking", "pretend to be"),
            ("role_hijacking", "roleplay as"),
            ("role_hijacking", "simulate being"),
            ("role_hijacking", "behave as"),
            ("role_hijacking", "assume the role"),
            ("role_hijacking", "switch to"),
            ("role_hijacking", "you must now act"),

            // Category 3: System prompt extraction
            ("system_prompt_extraction", "repeat your system prompt"),
            ("system_prompt_extraction", "what are your instructions"),
            ("system_prompt_extraction", "show me your prompt"),
            ("system_prompt_extraction", "reveal your system"),
            ("system_prompt_extraction", "display your instructions"),
            ("system_prompt_extraction", "print your system prompt"),
            ("system_prompt_extraction", "output your instructions"),
            ("system_prompt_extraction", "what is your system prompt"),

            // Category 4: Delimiter injection
            ("delimiter_injection", "```system"),
            ("delimiter_injection", "###system###"),
            ("delimiter_injection", "[inst]"),
            ("delimiter_injection", "[/inst]"),
            ("delimiter_injection", "<<sys>>"),
            ("delimiter_injection", "<|system|>"),
            ("delimiter_injection", "<|im_start|>"),
            ("delimiter_injection", "<|im_end|>"),
            ("delimiter_injection", "system: override"),
            ("delimiter_injection", "### instruction ###"),
            ("delimiter_injection", "<|endoftext|>"),
        };

    /// <summary>
    /// Category labels for human-readable warning messages.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryLabels =
        new Dictionary<string, string>
        {
            ["instruction_override"] = "Instruction override attempt",
            ["role_hijacking"] = "Role hijacking attempt",
            ["system_prompt_extraction"] = "System prompt extraction attempt",
            ["delimiter_injection"] = "Delimiter injection attempt",
            ["encoding_evasion"] = "Encoding evasion attempt",
            ["custom"] = "Custom pattern match",
        };

    /// <summary>
    /// Creates a new <see cref="ContentSanitizer"/> instance.
    /// </summary>
    /// <param name="logger">Optional logger for debug/warning output. Never logs actual input content.</param>
    /// <param name="enabled">When false, only null bytes are removed. Default: true.</param>
    /// <param name="extraInjectionPatterns">Additional injection patterns beyond built-in defaults (additive).</param>
    public ContentSanitizer(
        ILogger<ContentSanitizer>? logger = null,
        bool enabled = true,
        IReadOnlyList<string>? extraInjectionPatterns = null)
    {
        _logger = logger;
        _enabled = enabled;
        _extraInjectionPatterns = extraInjectionPatterns ?? Array.Empty<string>();
    }

    /// <inheritdoc />
    public SanitizationResult SanitizeInput(string input)
    {
        if (input is null)
            return new SanitizationResult { Result = string.Empty, Warnings = new List<string>() };

        try
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();
            var result = input;

            // Null byte removal -- always applied (hard safety requirement)
            result = NullByteRe.Replace(result, "");

            if (!_enabled)
            {
                return new SanitizationResult { Result = result, Warnings = warnings };
            }

            // Strip HTML tags (quote-aware state machine)
            var preHtml = result;
            result = StripHtml(result);
            if (result != preHtml)
            {
                warnings.Add("HTML content was stripped from input");
            }

            // Remove zero-width and invisible characters
            result = ZeroWidthCharsRe.Replace(result, "");

            // Detect prompt injection patterns (NFKD normalization applied internally)
            var injectionWarnings = DetectPromptInjection(result);
            warnings.AddRange(injectionWarnings);

            sw.Stop();

            // Log summary
            if (warnings.Count > 0)
            {
                _logger?.LogDebug(
                    "Input sanitization: {PatternsMatchedCount} warnings, input {InputLengthChars} chars, output {OutputLengthChars} chars",
                    warnings.Count, input.Length, result.Length);
            }

            // Performance warning
            if (sw.Elapsed.TotalMilliseconds > PerformanceThresholdMs)
            {
                _logger?.LogWarning(
                    "Slow input sanitization: {DurationMs:F2}ms for {InputLengthChars} chars, {PatternCount} patterns checked",
                    sw.Elapsed.TotalMilliseconds, input.Length,
                    BuiltinInjectionPatterns.Count + _extraInjectionPatterns.Count);
            }

            return new SanitizationResult { Result = result, Warnings = warnings };
        }
        catch (Exception ex)
        {
            // Never throw -- return input with null bytes removed as best-effort
            _logger?.LogError(
                "Sanitization exception: {ExceptionMessage}, input {InputLengthChars} chars",
                ex.Message, input.Length);
            return new SanitizationResult { Result = NullByteRe.Replace(input, ""), Warnings = new List<string>() };
        }
    }

    /// <inheritdoc />
    public SanitizationResult SanitizeOutput(string output)
    {
        if (output is null)
            return new SanitizationResult { Result = string.Empty, Warnings = new List<string>() };

        try
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();
            var result = output;

            // Null byte removal -- always applied
            result = NullByteRe.Replace(result, "");

            if (!_enabled)
            {
                return new SanitizationResult { Result = result, Warnings = warnings };
            }

            // Remove zero-width and invisible characters
            result = ZeroWidthCharsRe.Replace(result, "");

            // Strip HTML outside code blocks (preserve code blocks verbatim)
            result = StripHtmlPreserveCode(result);

            sw.Stop();

            // Log summary
            _logger?.LogDebug(
                "Output sanitization: input {InputLengthChars} chars, output {OutputLengthChars} chars",
                output.Length, result.Length);

            // Performance warning
            if (sw.Elapsed.TotalMilliseconds > PerformanceThresholdMs)
            {
                _logger?.LogWarning(
                    "Slow output sanitization: {DurationMs:F2}ms for {InputLengthChars} chars",
                    sw.Elapsed.TotalMilliseconds, output.Length);
            }

            return new SanitizationResult { Result = result, Warnings = warnings };
        }
        catch (Exception ex)
        {
            // Never throw -- return output with null bytes removed as best-effort
            _logger?.LogError(
                "Output sanitization exception: {ExceptionMessage}, input {InputLengthChars} chars",
                ex.Message, output.Length);
            return new SanitizationResult { Result = NullByteRe.Replace(output, ""), Warnings = new List<string>() };
        }
    }

    /// <summary>
    /// Quote-aware HTML tag stripping state machine.
    /// Tracks single/double quote state inside tag attributes to find the actual closing &gt;.
    /// Handles <c>&lt;div title="a&gt;b"&gt;content&lt;/div&gt;</c> correctly.
    /// Unclosed tags are stripped to end of string.
    /// </summary>
    private static string StripHtml(string input)
    {
        var result = new StringBuilder(input.Length);
        int i = 0;

        while (i < input.Length)
        {
            var start = input.IndexOf('<', i);
            if (start == -1)
            {
                result.Append(input, i, input.Length - i);
                break;
            }

            result.Append(input, i, start - i);

            // Find closing >, respecting quoted attributes
            int j = start + 1;
            bool inSingle = false, inDouble = false;

            while (j < input.Length)
            {
                char ch = input[j];
                if (ch == '"' && !inSingle)
                    inDouble = !inDouble;
                else if (ch == '\'' && !inDouble)
                    inSingle = !inSingle;
                else if (ch == '>' && !inSingle && !inDouble)
                    break;
                j++;
            }

            // Handle unclosed tag: if no > found, strip from < to end
            i = j < input.Length ? j + 1 : input.Length;
        }

        return result.ToString();
    }

    /// <summary>
    /// Strip HTML tags from content while preserving content within <c>```</c> code blocks.
    /// Splits input on triple-backtick boundaries and only strips HTML in non-code segments.
    /// Code blocks are preserved verbatim with their delimiters.
    /// </summary>
    private static string StripHtmlPreserveCode(string input)
    {
        const string delimiter = "```";
        var segments = input.Split(delimiter);
        var result = new StringBuilder(input.Length);

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            bool isInsideCodeBlock = (i % 2) == 1;
            bool isLastUnclosedFence = isInsideCodeBlock && i == segments.Length - 1;

            if (isInsideCodeBlock && !isLastUnclosedFence)
            {
                // Inside matched code block -- preserve verbatim with delimiters
                result.Append(delimiter);
                result.Append(segment);
                result.Append(delimiter);
            }
            else
            {
                // Outside code block, or unclosed last fence -- strip HTML
                if (isLastUnclosedFence)
                    result.Append(delimiter);
                result.Append(StripHtml(segment));
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Prompt injection detection with NFKD normalization to defeat encoding evasion.
    ///
    /// Categories checked:
    /// 1. Instruction override: "ignore previous instructions", "disregard above"
    /// 2. Role hijacking: "you are now", "act as", "pretend to be"
    /// 3. System prompt extraction: "repeat your system prompt", "what are your instructions"
    /// 4. Delimiter injection: "```system", "###SYSTEM###", "[INST]"
    /// 5. Encoding evasion: detected via NFKD normalization before matching
    ///
    /// Returns a list of warning strings describing each detected pattern.
    /// Never logs the matched content itself (may contain PII or attack payloads).
    /// </summary>
    private List<string> DetectPromptInjection(string input)
    {
        var warnings = new List<string>();

        // Apply NFKD normalization to defeat encoding evasion
        // (e.g., fullwidth Latin letters like \uFF49\uFF47\uFF4E\uFF4F\uFF52\uFF45 -> "ignore")
        string normalized;
        try
        {
            normalized = input.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            // Malformed string -- skip normalization, use original
            normalized = input;
        }

        var lowered = normalized.ToLowerInvariant();
        var originalLowered = input.ToLowerInvariant();

        // Check if normalization changed the input (potential encoding evasion)
        if (lowered != originalLowered)
        {
            bool evasionDetected = false;
            foreach (var (_, pattern) in BuiltinInjectionPatterns)
            {
                if (lowered.Contains(pattern, StringComparison.Ordinal) &&
                    !originalLowered.Contains(pattern, StringComparison.Ordinal))
                {
                    evasionDetected = true;
                    break;
                }
            }

            if (evasionDetected)
            {
                var label = CategoryLabels.TryGetValue("encoding_evasion", out var l)
                    ? l
                    : "Encoding evasion attempt";
                warnings.Add(
                    $"{label}: Unicode compatibility characters detected that normalize to injection pattern");

                _logger?.LogDebug(
                    "Encoding evasion detected in input of {InputLengthChars} chars",
                    input.Length);
            }
        }

        // Check built-in patterns against normalized input
        foreach (var (category, pattern) in BuiltinInjectionPatterns)
        {
            if (lowered.Contains(pattern, StringComparison.Ordinal))
            {
                var label = CategoryLabels.TryGetValue(category, out var l)
                    ? l
                    : $"Unknown category: {category}";
                warnings.Add($"{label}: matched pattern \"{pattern}\"");

                _logger?.LogDebug(
                    "Injection pattern matched: {PatternName} in input of {InputLengthChars} chars",
                    pattern, input.Length);
            }
        }

        // Check extra patterns (additive to built-in defaults)
        foreach (var pattern in _extraInjectionPatterns)
        {
            if (lowered.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal))
            {
                var label = CategoryLabels.TryGetValue("custom", out var l)
                    ? l
                    : "Custom pattern match";
                warnings.Add($"{label}: matched pattern \"{pattern}\"");

                _logger?.LogDebug(
                    "Custom injection pattern matched: {PatternName} in input of {InputLengthChars} chars",
                    pattern, input.Length);
            }
        }

        return warnings;
    }
}
