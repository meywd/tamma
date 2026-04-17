using Tamma.Api.Dtos.KnowledgeBase;

namespace Tamma.Api.Services.KnowledgeBase;

/// <summary>
/// Typed HTTP client used by <c>KbEndpoints</c> to delegate knowledge-base
/// operations to the TypeScript <c>@tamma/intelligence-server</c> sidecar.
///
/// <para>
/// Each method maps 1-to-1 to one of the 30 KB routes exposed by the sidecar
/// under <c>/kb/*</c>. Methods are allowed to return <see cref="object"/>
/// (the dashboard's contract is any JSON response with a documented shape),
/// but typed request DTOs keep outbound payloads well-formed.
/// </para>
///
/// <para>
/// On 5xx or network failures the implementation falls back to an empty /
/// degraded payload and logs the incident. This preserves the dashboard UX
/// when the sidecar is temporarily unavailable — a "circuit-breaker-light"
/// policy documented in the layer-4 implementation plan.
/// </para>
/// </summary>
public interface IIntelligenceHttpClient
{
    // ── Index (6) ────────────────────────────────────────────────────────
    Task<object> GetIndexStatusAsync(CancellationToken ct = default);
    Task<object> TriggerIndexAsync(TriggerIndexRequest? body, CancellationToken ct = default);
    Task<object> GetIndexConfigAsync(CancellationToken ct = default);
    Task<object> UpdateIndexConfigAsync(UpdateIndexConfigRequest body, CancellationToken ct = default);
    Task<object> GetIndexStatsAsync(CancellationToken ct = default);
    Task<object> ClearIndexAsync(CancellationToken ct = default);

    // ── Vector DB (6) ────────────────────────────────────────────────────
    Task<object> GetVectorDbStatusAsync(CancellationToken ct = default);
    Task<object> SearchVectorsAsync(VectorSearchRequest body, CancellationToken ct = default);
    Task<object> UpsertVectorsAsync(VectorUpsertRequest body, CancellationToken ct = default);
    Task<object> DeleteVectorsAsync(VectorDeleteRequest body, CancellationToken ct = default);
    Task<object> GetVectorCollectionsAsync(CancellationToken ct = default);
    Task<object> GetVectorStatsAsync(CancellationToken ct = default);

    // ── RAG (4) ──────────────────────────────────────────────────────────
    Task<object> GetRagConfigAsync(CancellationToken ct = default);
    Task<object> UpdateRagConfigAsync(UpdateRagConfigRequest body, CancellationToken ct = default);
    Task<object> QueryRagAsync(RagQueryRequest body, CancellationToken ct = default);
    Task<object> GetRagMetricsAsync(CancellationToken ct = default);

    // ── MCP (8) ──────────────────────────────────────────────────────────
    Task<object> ListMcpServersAsync(CancellationToken ct = default);
    Task<object> GetMcpServerAsync(string id, CancellationToken ct = default);
    Task<object> StartMcpServerAsync(string id, CancellationToken ct = default);
    Task<object> StopMcpServerAsync(string id, CancellationToken ct = default);
    Task<object> GetMcpConfigAsync(CancellationToken ct = default);
    Task<object> UpdateMcpConfigAsync(UpdateMcpConfigRequest body, CancellationToken ct = default);
    Task<object> ListMcpToolsAsync(string? serverName = null, CancellationToken ct = default);
    Task<object> InvokeMcpToolAsync(McpInvokeRequest body, CancellationToken ct = default);

    // ── Context (3) ──────────────────────────────────────────────────────
    Task<object> GetContextHistoryAsync(int? limit = null, CancellationToken ct = default);
    Task<object> PostContextFeedbackAsync(ContextFeedbackRequest body, CancellationToken ct = default);
    Task<object> GetContextConfigAsync(CancellationToken ct = default);

    // ── Analytics (3) ────────────────────────────────────────────────────
    Task<object> GetAnalyticsAsync(string? start = null, string? end = null, CancellationToken ct = default);
    Task<object> GetUsageAsync(string? start = null, string? end = null, CancellationToken ct = default);
    Task<object> GetCostsAsync(string? start = null, string? end = null, CancellationToken ct = default);
}
