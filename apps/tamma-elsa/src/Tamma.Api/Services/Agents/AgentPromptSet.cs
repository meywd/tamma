using System.Text.Json;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — the optional embedded prompt set carried by a CUSTOM (private)
/// agent inside <c>AgentVersion.ConfigJson["prompts"]</c> (Epic 32 rule 5:
/// custom prompts ⇔ custom agent).
///
/// <para>Public personas are prompt-free by contract and MUST leave this
/// null/empty — their prompts come from the Epic 27 store (resolved by sibling
/// story 32-15's <see cref="IPersonaPromptResolver"/>). A private agent MAY
/// carry a populated set (committing it to the custom prompt branch) or leave it
/// absent/empty (then it behaves persona-like and resolves via 32-15).</para>
/// </summary>
public sealed record AgentPromptSet
{
    /// <summary>Fallback system prompt used when no role:action template matches.</summary>
    public string? System { get; init; }

    /// <summary>Templates keyed by "&lt;role&gt;:&lt;action&gt;" (Epic 27 wire forms).</summary>
    public IReadOnlyDictionary<string, string>? ByRoleAction { get; init; }

    /// <summary>
    /// True when neither a non-blank <see cref="System"/> prompt nor any
    /// <see cref="ByRoleAction"/> entry is present. An empty set is treated as
    /// "absent" — a private agent with an empty prompts block does NOT enter the
    /// custom branch (it delegates to the persona branch, 32-15).
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(System) && (ByRoleAction is null || ByRoleAction.Count == 0);

    /// <summary>
    /// Parse the optional <c>prompts</c> sub-object out of an
    /// <c>AgentVersion.ConfigJson</c> string. Returns <c>null</c> when the
    /// <c>prompts</c> key is absent or not an object (a malformed/wrong-typed
    /// block is treated as absent here — the write-time validator is the gate
    /// for rejecting bad shapes, AC2/AC3). NEVER throws — resolution is fail-loud
    /// at the resolver, not here.
    /// </summary>
    public static AgentPromptSet? TryRead(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return TryRead(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse the optional <c>prompts</c> sub-object out of an already-parsed
    /// <c>ConfigJson</c> root element. Returns <c>null</c> when the <c>prompts</c>
    /// key is absent or not an object.
    /// </summary>
    public static AgentPromptSet? TryRead(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("prompts", out var prompts) ||
            prompts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? system = null;
        if (prompts.TryGetProperty("system", out var sys) && sys.ValueKind == JsonValueKind.String)
        {
            system = sys.GetString();
        }

        Dictionary<string, string>? byRoleAction = null;
        if (prompts.TryGetProperty("byRoleAction", out var bra) &&
            bra.ValueKind == JsonValueKind.Object)
        {
            byRoleAction = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in bra.EnumerateObject())
            {
                // A non-string template value is preserved as an empty string so
                // the validator can reject it (PROMPTS_EMPTY_TEMPLATE) rather than
                // silently dropping the key. The resolver never sees an
                // unvalidated block (writes are gated).
                byRoleAction[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return new AgentPromptSet { System = system, ByRoleAction = byRoleAction };
    }
}
