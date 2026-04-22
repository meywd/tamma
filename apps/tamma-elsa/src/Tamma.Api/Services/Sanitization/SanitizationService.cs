using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Sanitization;

/// <summary>
/// Rule-based content sanitizer.
///
/// <para>
/// Rules come from <see cref="ISanitizationRepository.GetRulesAsync"/>, which
/// returns the tenant's effective rule set — system defaults merged with any
/// tenant-specific overrides (same <see cref="SanitizationRuleDefinition.Name"/>
/// replaces the default). Rules are applied in ascending
/// <see cref="SanitizationRuleDefinition.Priority"/> order; disabled rules and
/// rules that fail to compile are skipped with a log entry.
/// </para>
///
/// <para>
/// ReDoS defence: every rule is compiled with <c>RegexOptions.Compiled</c> and
/// a 100 ms <see cref="Regex.MatchTimeout"/>. A
/// <see cref="RegexMatchTimeoutException"/> from a pathological pattern is
/// caught per rule — the rule is skipped, subsequent rules still run.
/// </para>
///
/// <para>
/// Compiled regex instances are cached by
/// <c>(tenantId, ruleName, patternHash, options)</c>. Changing the pattern
/// text or options invalidates the cache entry for that rule automatically.
/// </para>
/// </summary>
public sealed class SanitizationService : ISanitizationService
{
    /// <summary>
    /// Hard ceiling on per-rule regex execution time. Set low enough that even
    /// a catastrophic-backtracking pattern cannot meaningfully stall a request,
    /// high enough that legitimate complex patterns still complete.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly ISanitizationRepository _repository;
    private readonly IContentSanitizer _contentSanitizer;
    private readonly ILogger<SanitizationService> _logger;

    /// <summary>
    /// Compiled-regex cache. Key contains the pattern hash and options so that
    /// an in-place update of a tenant's rule (same name, different pattern)
    /// transparently invalidates and recompiles.
    /// </summary>
    private readonly ConcurrentDictionary<CacheKey, Regex> _cache = new();

    public SanitizationService(
        ISanitizationRepository repository,
        ILogger<SanitizationService> logger)
        : this(repository, new ContentSanitizer(), logger) { }

    public SanitizationService(
        ISanitizationRepository repository,
        IContentSanitizer contentSanitizer,
        ILogger<SanitizationService> logger)
    {
        _repository = repository;
        _contentSanitizer = contentSanitizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SanitizeResult> SanitizeAsync(
        string input,
        Guid? tenantId,
        SanitizeDirection direction = SanitizeDirection.Input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new SanitizeResult(string.Empty, Array.Empty<SanitizationHit>(),
                Array.Empty<string>());
        }

        var rules = await _repository.GetRulesAsync(tenantId).ConfigureAwait(false);

        // Work on a sorted snapshot so caller ordering cannot affect us, and so
        // that disabled rules skip compilation entirely.
        var orderedRules = rules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var result = input;
        var hits = new List<SanitizationHit>(orderedRules.Count);

        foreach (var rule in orderedRules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var regex = TryGetRegex(tenantId, rule);
            if (regex is null)
            {
                // Compile failure was already logged inside TryGetRegex.
                continue;
            }

            try
            {
                // Count matches before replacing so the hit count is accurate
                // even when Replacement is the empty string.
                var matchCount = regex.Matches(result).Count;
                if (matchCount == 0) continue;

                result = regex.Replace(result, rule.Replacement);
                hits.Add(new SanitizationHit(rule.Name, matchCount));
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip the offending rule but keep processing later rules.
                _logger.LogWarning(
                    "Sanitization rule '{RuleName}' hit MatchTimeout ({TimeoutMs} ms) and was skipped",
                    rule.Name,
                    (int)MatchTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                // Never let a single bad rule poison the whole sanitization call.
                _logger.LogError(
                    ex,
                    "Sanitization rule '{RuleName}' threw {ExceptionType}; rule skipped",
                    rule.Name,
                    ex.GetType().Name);
            }
        }

        // Run the ContentSanitizer pipeline (HTML strip + zero-width strip +
        // injection detection on input; preserve-code-block strip on output)
        // AFTER the user's regex-replace rules so any rule-supplied
        // replacements still flow through. Warnings are surfaced verbatim
        // via SanitizeResult.Warnings (finding 006).
        var sanitised = direction == SanitizeDirection.Output
            ? _contentSanitizer.SanitizeOutput(result)
            : _contentSanitizer.Sanitize(result);

        return new SanitizeResult(sanitised.Result, hits,
            sanitised.Warnings.Count > 0 ? sanitised.Warnings : Array.Empty<string>());
    }

    /// <summary>
    /// Compile the rule's pattern (or fetch a cached copy). Returns <c>null</c>
    /// if the pattern is invalid — those rules are skipped at runtime.
    /// </summary>
    private Regex? TryGetRegex(Guid? tenantId, SanitizationRuleDefinition rule)
    {
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!rule.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var key = new CacheKey(
            tenantId,
            rule.Name,
            HashPattern(rule.Pattern),
            options);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var regex = new Regex(rule.Pattern, options, MatchTimeout);
            _cache[key] = regex;
            return regex;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                ex,
                "Sanitization rule '{RuleName}' has invalid regex pattern; rule skipped",
                rule.Name);
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of the pattern bytes, base64-encoded. Used as part of the cache
    /// key so pattern edits transparently invalidate the cached compiled regex.
    /// </summary>
    private static string HashPattern(string pattern)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(pattern), hash);
        return Convert.ToBase64String(hash);
    }

    private readonly record struct CacheKey(
        Guid? TenantId,
        string RuleName,
        string PatternHash,
        RegexOptions Options);
}
