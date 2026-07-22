using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The single canonical <see cref="JsonSerializerOptions"/> for acceptance-rules
/// payloads (mirrors 39-2's <c>DocumentJson</c> style). Every wire property
/// carries an explicit <c>[JsonPropertyName]</c> and every closed enum a
/// <c>[JsonConverter(WireEnumJsonConverter&lt;T&gt;)]</c>, so the options only
/// need to (a) not infer a naming policy and (b) never write nulls implicitly.
/// The polymorphic <see cref="AcceptanceDecision"/> / <see cref="AcceptanceRouting"/>
/// hierarchies serialize via their <c>[JsonPolymorphic]</c> attributes.
///
/// <para>Persisted rows and the <c>get_acceptance_rules</c> tool output MUST go
/// through this single serializer so the resolver payload and the embedded
/// request payload are byte-identical (AC3).</para>
/// </summary>
public static class AcceptanceRulesJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // Explicit [JsonPropertyName] everywhere; enums carry their own
            // [JsonConverter]. Nothing to infer.
            PropertyNamingPolicy = null,
        };
        return options;
    }

    /// <summary>Serialize an <see cref="AcceptanceRules"/> body with the canonical options.</summary>
    public static string Serialize(AcceptanceRules rules) => JsonSerializer.Serialize(rules, Options);

    /// <summary>
    /// Deserialize an <see cref="AcceptanceRules"/> body and VALIDATE it
    /// defensively (Design Decision D3 — a corrupt row throws, never silently
    /// degrades).
    /// </summary>
    /// <exception cref="JsonException">Malformed JSON or a bad wire token.</exception>
    /// <exception cref="Tamma.Core.TammaError">
    /// Code <c>ACCEPTANCE_RULES.INVALID</c> for an out-of-range / unknown-key body.
    /// </exception>
    public static AcceptanceRules Deserialize(string json)
    {
        var rules = JsonSerializer.Deserialize<AcceptanceRules>(json, Options)
            ?? throw new JsonException("Acceptance-rules JSON deserialized to null.");
        return rules.Validate();
    }

    /// <summary>Serialize a <see cref="ResolvedAcceptanceRules"/> with the canonical options.</summary>
    public static string SerializeResolved(ResolvedAcceptanceRules resolved) =>
        JsonSerializer.Serialize(resolved, Options);
}
