using System.Text.Json.Serialization;

namespace Tamma.Data.Entities;

/// <summary>
/// A single sanitization rule — a named regex that replaces its matches with
/// a fixed replacement string (typically <c>[REDACTED]</c>).
///
/// <para>
/// Stored inside the <see cref="SanitizationRule.Rules"/> JSONB column as part
/// of an array of rule definitions, one array per tenant row. This is the
/// persisted shape; <c>Tamma.Api.Services.Sanitization</c> exposes the same
/// record through a type alias for the service and endpoint layer.
/// </para>
/// </summary>
/// <param name="Name">
/// Stable identifier. Used as the merge key when a tenant override replaces a
/// system default and as part of the compiled-regex cache key.
/// </param>
/// <param name="Pattern">
/// .NET regex pattern. Compiled once and cached by the service. Compilation
/// failures cause the rule to be skipped at runtime (never throws).
/// </param>
/// <param name="Replacement">
/// Fixed replacement string. <c>[REDACTED]</c> by convention.
/// </param>
/// <param name="CaseSensitive">
/// When <c>false</c>, the compiled regex uses <c>RegexOptions.IgnoreCase</c>.
/// </param>
/// <param name="Priority">
/// Lower integers run first. Rules that match earlier consume their matched
/// substrings, so later rules see already-redacted text.
/// </param>
/// <param name="Enabled">
/// When <c>false</c>, the rule is skipped entirely.
/// </param>
public sealed record SanitizationRuleDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("replacement")] string Replacement,
    [property: JsonPropertyName("caseSensitive")] bool CaseSensitive,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("enabled")] bool Enabled);
