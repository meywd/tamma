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

    /// <summary>
    /// Templates keyed by the CANONICAL <c>"&lt;role&gt;:&lt;action&gt;"</c> wire
    /// form (Epic 27). <see cref="TryRead(JsonElement)"/> canonicalizes every
    /// stored key (legacy aliases + case variants → canonical wire) so a key
    /// written as <c>"implementer:CODE_GENERATION"</c> is stored — and looked up —
    /// as <c>"developer:implement-feature"</c>. The resolver builds its lookup key
    /// from the already-normalized <c>role</c>/<c>action</c>, so STORE and LOOKUP
    /// agree under <see cref="StringComparer.Ordinal"/>.
    /// </summary>
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
                var value = entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString() ?? string.Empty
                    : string.Empty;

                // Canonicalize the key so STORE (here) and LOOKUP (the resolver,
                // which builds its key from the already-normalized role/action)
                // agree under StringComparer.Ordinal. A key written with a legacy
                // alias or case variant (e.g. "implementer:CODE_GENERATION") is
                // stored as its canonical wire form ("developer:implement-feature").
                // A key that does NOT parse as a valid "<role>:<action>" pair is
                // preserved verbatim so the write-time validator can still reject
                // it (PROMPTS_INVALID_KEY); TryRead itself never throws.
                var canonicalKey = CanonicalizeRoleActionKey(entry.Name);

                // Last-wins on a post-canonicalization collision (two raw keys that
                // canonicalize to the same cell). The validator does NOT currently
                // reject such collisions — the resolver only needs ONE template per
                // cell, and last-wins is deterministic over JSON property order.
                byRoleAction[canonicalKey] = value;
            }
        }

        return new AgentPromptSet { System = system, ByRoleAction = byRoleAction };
    }

    /// <summary>
    /// Canonicalize a raw <c>byRoleAction</c> key to the
    /// <c>AgentRoleExtensions.Parse(role).ToWire():AgentActionExtensions.Parse(action).ToWire()</c>
    /// form — the SAME canonical form the resolver looks up. Legacy aliases and
    /// case variants are normalized; an unparseable key (no colon, unknown token)
    /// is returned VERBATIM so the write-time validator can reject it
    /// (PROMPTS_INVALID_KEY). Never throws.
    /// </summary>
    private static string CanonicalizeRoleActionKey(string rawKey)
    {
        var sep = rawKey.IndexOf(':');
        if (sep <= 0 || sep >= rawKey.Length - 1)
        {
            return rawKey;
        }

        var rolePart = rawKey[..sep];
        var actionPart = rawKey[(sep + 1)..];

        try
        {
            var roleWire = AgentRoleExtensions.Parse(rolePart).ToWire();
            var actionWire = AgentActionExtensions.Parse(actionPart).ToWire();
            return $"{roleWire}:{actionWire}";
        }
        catch (ArgumentException)
        {
            // Unknown role/action token — preserve verbatim for the validator.
            return rawKey;
        }
    }
}
