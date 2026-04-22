using System.Text.Json;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Stored findings for a single (repository, issueNumber) pair.
/// </summary>
public sealed record ContextEntry(
    string Repository,
    int IssueNumber,
    JsonElement Findings,
    DateTime StoredAt);

/// <summary>
/// One scored chunk returned by <see cref="IContextStore.QueryAsync"/>.
/// </summary>
public sealed record ContextChunk(string Content, string Role, double Score);

/// <summary>
/// In-memory context store for the engine context endpoints.
///
/// <para>Audit finding 004 — port of the deleted TS in-memory store from
/// <c>packages/api/src/routes/engine/engine-context-routes.ts</c>. The
/// long-term replacement is the real RAG pipeline under
/// <c>@tamma/intelligence</c>; this implementation is the MVP that
/// unblocks the deployed Elsa <c>StoreFindings</c> /
/// <c>StoreRoleFinding</c> activities and the role-filtered query path
/// they consume.</para>
/// </summary>
public interface IContextStore
{
    /// <summary>
    /// Persist findings for an issue. Subsequent calls with the same
    /// (repository, issueNumber) replace the prior entry.
    /// </summary>
    Task StoreAsync(string repository, int issueNumber, JsonElement findings, CancellationToken ct = default);

    /// <summary>
    /// Get the stored entry for an exact (repository, issueNumber). When
    /// repository is null, returns the most recent entry for the issue
    /// number across any repository.
    /// </summary>
    Task<ContextEntry?> GetAsync(string? repository, int issueNumber, CancellationToken ct = default);

    /// <summary>
    /// Score stored findings for an issue against a query. Mirrors the TS
    /// term-match scoring loop with an optional role filter and 4-char-per-token
    /// budget.
    /// </summary>
    Task<(IReadOnlyList<ContextChunk> Chunks, int TotalTokens)> QueryAsync(
        string? repository,
        int? issueNumber,
        string query,
        string? role,
        int? maxTokens,
        CancellationToken ct = default);
}
