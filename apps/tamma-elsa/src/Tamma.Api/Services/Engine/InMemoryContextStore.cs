using System.Collections.Concurrent;
using System.Text.Json;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Thread-safe in-memory <see cref="IContextStore"/> port of the deleted TS
/// engine-context store. Suitable for self-hosted single-instance use; not
/// suitable for horizontally-scaled deployments (state is per-process).
/// Replace with the real RAG pipeline (<c>@tamma/intelligence</c>) once
/// it ports to C#.
/// </summary>
public sealed class InMemoryContextStore : IContextStore
{
    private readonly ConcurrentDictionary<string, ContextEntry> _entries = new();

    private static string MakeKey(string repository, int issueNumber)
        => $"{repository.ToLowerInvariant()}:{issueNumber}";

    public Task StoreAsync(
        string repository, int issueNumber, JsonElement findings, CancellationToken ct = default)
    {
        var key = MakeKey(repository, issueNumber);
        // Clone the JsonElement so the caller can dispose its source.
        var clonedFindings = findings.Clone();
        var entry = new ContextEntry(repository, issueNumber, clonedFindings, DateTime.UtcNow);
        _entries[key] = entry;
        return Task.CompletedTask;
    }

    public Task<ContextEntry?> GetAsync(string? repository, int issueNumber, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(repository))
        {
            var key = MakeKey(repository, issueNumber);
            _entries.TryGetValue(key, out var exact);
            return Task.FromResult<ContextEntry?>(exact);
        }

        // Fallback scan: most recent entry for the issue across any repo.
        var match = _entries.Values
            .Where(e => e.IssueNumber == issueNumber)
            .OrderByDescending(e => e.StoredAt)
            .FirstOrDefault();
        return Task.FromResult<ContextEntry?>(match);
    }

    public Task<(IReadOnlyList<ContextChunk> Chunks, int TotalTokens)> QueryAsync(
        string? repository,
        int? issueNumber,
        string query,
        string? role,
        int? maxTokens,
        CancellationToken ct = default)
    {
        var queryLower = (query ?? string.Empty).ToLowerInvariant();
        var queryTerms = queryLower
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Source pool: when both repo + issue specified, look up the exact
        // entry; otherwise scan all matching entries.
        IEnumerable<ContextEntry> candidates;
        if (!string.IsNullOrEmpty(repository) && issueNumber.HasValue)
        {
            _entries.TryGetValue(MakeKey(repository, issueNumber.Value), out var exact);
            candidates = exact is null ? Array.Empty<ContextEntry>() : new[] { exact };
        }
        else if (issueNumber.HasValue)
        {
            candidates = _entries.Values.Where(e => e.IssueNumber == issueNumber.Value);
        }
        else
        {
            candidates = _entries.Values;
        }

        var scored = new List<ContextChunk>();
        foreach (var entry in candidates)
        {
            // Findings are typically a JSON object keyed by role:
            // {"dev": "...", "security": "...", ...}. When the value is not
            // a string we fall back to its raw JSON.
            if (entry.Findings.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in entry.Findings.EnumerateObject())
            {
                var findingRole = prop.Name;
                if (!string.IsNullOrEmpty(role) &&
                    !string.Equals(findingRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var content = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    _ => prop.Value.GetRawText()
                };
                if (string.IsNullOrEmpty(content)) continue;

                var contentLower = content.ToLowerInvariant();
                var matchCount = queryTerms.Count(t => contentLower.Contains(t));
                var score = queryTerms.Length > 0 ? (double)matchCount / queryTerms.Length : 0;
                scored.Add(new ContextChunk(content, findingRole, score));
            }
        }

        // Highest-score first; truncate to the token budget at 4 chars/token.
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        var budgetTokens = maxTokens ?? int.MaxValue;
        var totalTokens = 0;
        var emitted = new List<ContextChunk>();
        foreach (var chunk in scored)
        {
            var chunkTokens = (chunk.Content.Length + 3) / 4;
            if (totalTokens + chunkTokens > budgetTokens) break;
            emitted.Add(chunk);
            totalTokens += chunkTokens;
        }

        return Task.FromResult<(IReadOnlyList<ContextChunk>, int)>((emitted, totalTokens));
    }
}
