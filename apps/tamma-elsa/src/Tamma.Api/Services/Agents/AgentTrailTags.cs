using System.Globalization;
using System.Text.Json;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-6 (AC3) — the SINGLE shared builder for an action-trail event's
/// <c>Tags</c> JSONB. Every emission site routes through here so the flat-string
/// tag contract is identical across the whole trail: <c>agentId</c>,
/// <c>agentVersion</c>, <c>role</c>, <c>provider</c>, <c>model</c>, <c>promptRef</c>,
/// <c>issueId</c>, <c>iteration</c>, <c>correlationId</c>, <c>credentialSource</c>.
///
/// <para>Tags are FLAT strings (DCB tag convention) used for cross-aggregate
/// queries (the <c>agentId</c> predicate that backs the per-agent read). Richer
/// metrics + blob refs live in <c>Data</c>, not here. Prompt bodies / tool
/// payloads are NEVER tagged — only their references (AC6).</para>
/// </summary>
public static class AgentTrailTags
{
    /// <summary>
    /// Build the flat-string <c>Tags</c> JSONB for <paramref name="c"/>. Extra
    /// tags (e.g. <c>bugType</c> for <c>REVIEW.BUG.RECORDED</c>) are merged in
    /// via <paramref name="extra"/> and win over the base keys.
    /// </summary>
    public static string Build(AgentTrailContext c, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["agentId"] = c.AgentId.ToString(),
            ["agentVersion"] = c.AgentVersion.ToString(CultureInfo.InvariantCulture),
            ["role"] = c.Role,
            ["provider"] = c.Provider,
            ["model"] = c.Model,
            ["promptRef"] = c.PromptRef,
            ["issueId"] = c.IssueId,
            ["iteration"] = c.Iteration.ToString(CultureInfo.InvariantCulture),
            ["correlationId"] = c.CorrelationId,
            ["credentialSource"] = c.CredentialSource,
        };

        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        return JsonSerializer.Serialize(dict);
    }
}
