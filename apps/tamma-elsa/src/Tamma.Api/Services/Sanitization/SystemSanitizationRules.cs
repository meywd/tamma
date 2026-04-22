using Tamma.Data.Entities;

namespace Tamma.Api.Services.Sanitization;

/// <summary>
/// System-default sanitization rule set. These are baked into the binary and
/// always applied unless a tenant supplies an override with the same
/// <see cref="SanitizationRuleDefinition.Name"/>.
///
/// Priority buckets:
/// <list type="bullet">
///   <item><description>1-9: high-value secret patterns (API keys, tokens).</description></item>
///   <item><description>10-19: credential shapes (AWS, GitHub, Slack).</description></item>
///   <item><description>20-29: JWT and bearer tokens.</description></item>
///   <item><description>30-49: PII (SSN, credit cards, email).</description></item>
/// </list>
///
/// All patterns use <c>[REDACTED]</c> as the replacement. All patterns were
/// authored to avoid nested quantifiers (e.g. <c>(x+)+</c>) that trigger
/// catastrophic backtracking — the engine still enforces a 100 ms MatchTimeout
/// as a belt-and-braces guard.
///
/// Ported from the deleted TypeScript sanitization store
/// (<c>packages/api/src/services/sanitization-store.ts</c> @ commit 9e9a57c~1)
/// and extended with canonical PII / secret shapes.
/// </summary>
public static class SystemSanitizationRules
{
    private const string Redacted = "[REDACTED]";

    /// <summary>
    /// The immutable default rule set. Order here is informational only — the
    /// sanitization engine sorts by <see cref="SanitizationRuleDefinition.Priority"/>.
    /// </summary>
    public static IReadOnlyList<SanitizationRuleDefinition> DefaultRules { get; } =
        new List<SanitizationRuleDefinition>
        {
            // ── API keys (provider-specific shapes) ─────────────────────────
            new(
                Name: "anthropic-api-key",
                // sk-ant-api{digits}-{40+ base64url-ish chars}
                Pattern: @"sk-ant-(?:api|admin)\d{2}-[A-Za-z0-9_\-]{8,}",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 1,
                Enabled: true),

            new(
                Name: "openai-api-key",
                // sk- or sk-proj- followed by an alphanum run (length varies across 2024+ rotations)
                Pattern: @"sk-(?:proj-)?[A-Za-z0-9_\-]{20,}",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 2,
                Enabled: true),

            new(
                Name: "google-api-key",
                // AIza + 35 url-safe chars (Google API key spec)
                Pattern: @"AIza[0-9A-Za-z_\-]{35}",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 3,
                Enabled: true),

            // ── Cloud-provider keys ─────────────────────────────────────────
            new(
                Name: "aws-access-key",
                // AKIA/ASIA/AGPA/AIDA/AROA/AIPA/ANPA/ANVA/ASCA followed by 16 [A-Z0-9]
                Pattern: @"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|ASCA)[A-Z0-9]{16}\b",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 10,
                Enabled: true),

            new(
                Name: "aws-secret-access-key",
                // Heuristic: label then 40 base64 chars. Intentionally narrow to avoid
                // false-matching random base64 strings.
                Pattern: @"(?i)aws(?:.{0,20})?(?:secret|private)[^=:\n]{0,20}[=:][\s""']*[A-Za-z0-9/+]{40}",
                Replacement: Redacted,
                CaseSensitive: false,
                Priority: 11,
                Enabled: true),

            new(
                Name: "github-token",
                // ghp_, gho_, ghu_, ghs_, ghr_ followed by 36 alphanum
                Pattern: @"\bgh[opusr]_[A-Za-z0-9]{36,}\b",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 12,
                Enabled: true),

            new(
                Name: "slack-token",
                // xox[aboprs]-\d+-\d+-\d+-hex
                Pattern: @"\bxox[abporsu]-[A-Za-z0-9\-]{10,}\b",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 13,
                Enabled: true),

            new(
                Name: "private-key-block",
                // PEM header/footer. Multi-line patterns use RegexOptions.Singleline in
                // compiled form; here we just match the opening header, which is
                // sufficient to flag the presence of a private key.
                Pattern: @"-----BEGIN (?:RSA |DSA |EC |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 14,
                Enabled: true),

            // ── JWT / bearer tokens ─────────────────────────────────────────
            new(
                Name: "jwt-token",
                // header.payload.signature — three base64url segments separated by dots.
                Pattern: @"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 20,
                Enabled: true),

            // ── PII: identifiers ────────────────────────────────────────────
            new(
                Name: "ssn",
                // US Social Security Number in standard dashed form.
                Pattern: @"\b\d{3}-\d{2}-\d{4}\b",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 30,
                Enabled: true),

            new(
                Name: "credit-card",
                // 13-19 digit sequences grouped by 4 with optional spaces/hyphens,
                // plus bare 13-19 digit runs.
                Pattern: @"\b(?:\d[ \-]*?){13,19}\b",
                Replacement: Redacted,
                CaseSensitive: true,
                Priority: 31,
                Enabled: true),

            new(
                Name: "email",
                // Simple but sufficient email pattern. Not RFC 5322-strict on purpose —
                // we want to catch obvious emails, not validate them.
                Pattern: @"\b[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+\b",
                Replacement: Redacted,
                CaseSensitive: false,
                Priority: 40,
                Enabled: true),
        };
}
