using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Dtos.KnowledgeBase;
using Tamma.Api.Services.KnowledgeBase;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Minimal-API handlers for the 30 /api/kb/* routes.
///
/// <para>
/// Each handler is a method-group-compatible delegate invoked by Program.cs'
/// <c>MapGet/MapPost/...</c> calls. They take <see cref="IIntelligenceHttpClient"/>
/// from DI and forward to the TS sidecar, returning the sidecar's response
/// verbatim to the caller. This keeps Program.cs wiring unchanged — the only
/// edit needed on the parent side is calling <c>AddKnowledgeBaseServices()</c>
/// (see the extension class of the same name).
/// </para>
/// </summary>
public static class KbEndpoints
{
    // ── Index (6) ────────────────────────────────────────────────────────

    public static async Task<IResult> GetIndexStatus(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetIndexStatusAsync(ct));

    public static async Task<IResult> TriggerIndex(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] TriggerIndexRequest? body,
        CancellationToken ct)
        => Results.Ok(await client.TriggerIndexAsync(body, ct));

    public static async Task<IResult> GetIndexConfig(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetIndexConfigAsync(ct));

    public static async Task<IResult> UpdateIndexConfig(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] UpdateIndexConfigRequest body,
        CancellationToken ct)
        => Results.Ok(await client.UpdateIndexConfigAsync(body, ct));

    public static async Task<IResult> GetIndexStats(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetIndexStatsAsync(ct));

    public static async Task<IResult> ClearIndex(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.ClearIndexAsync(ct));

    // ── Vector DB (6) ────────────────────────────────────────────────────

    public static async Task<IResult> GetVectorDbStatus(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetVectorDbStatusAsync(ct));

    public static async Task<IResult> SearchVectors(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] VectorSearchRequest body,
        CancellationToken ct)
        => Results.Ok(await client.SearchVectorsAsync(body, ct));

    public static async Task<IResult> UpsertVectors(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] VectorUpsertRequest body,
        CancellationToken ct)
        => Results.Ok(await client.UpsertVectorsAsync(body, ct));

    public static async Task<IResult> DeleteVectors(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] VectorDeleteRequest body,
        CancellationToken ct)
        => Results.Ok(await client.DeleteVectorsAsync(body, ct));

    public static async Task<IResult> GetVectorCollections(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetVectorCollectionsAsync(ct));

    public static async Task<IResult> GetVectorStats(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetVectorStatsAsync(ct));

    // ── RAG (4) ──────────────────────────────────────────────────────────

    public static async Task<IResult> GetRagConfig(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetRagConfigAsync(ct));

    public static async Task<IResult> UpdateRagConfig(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] UpdateRagConfigRequest body,
        CancellationToken ct)
        => Results.Ok(await client.UpdateRagConfigAsync(body, ct));

    public static async Task<IResult> QueryRag(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] RagQueryRequest body,
        CancellationToken ct)
        => Results.Ok(await client.QueryRagAsync(body, ct));

    public static async Task<IResult> GetRagMetrics(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetRagMetricsAsync(ct));

    // ── MCP (8) ──────────────────────────────────────────────────────────

    public static async Task<IResult> ListMcpServers(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.ListMcpServersAsync(ct));

    public static async Task<IResult> GetMcpServer(
        [FromServices] IIntelligenceHttpClient client,
        string id,
        CancellationToken ct)
        => Results.Ok(await client.GetMcpServerAsync(id, ct));

    public static async Task<IResult> StartMcpServer(
        [FromServices] IIntelligenceHttpClient client,
        string id,
        CancellationToken ct)
        => Results.Ok(await client.StartMcpServerAsync(id, ct));

    public static async Task<IResult> StopMcpServer(
        [FromServices] IIntelligenceHttpClient client,
        string id,
        CancellationToken ct)
        => Results.Ok(await client.StopMcpServerAsync(id, ct));

    public static async Task<IResult> GetMcpConfig(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetMcpConfigAsync(ct));

    public static async Task<IResult> UpdateMcpConfig(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] UpdateMcpConfigRequest body,
        CancellationToken ct)
        => Results.Ok(await client.UpdateMcpConfigAsync(body, ct));

    public static async Task<IResult> ListMcpTools(
        [FromServices] IIntelligenceHttpClient client,
        [FromQuery(Name = "serverName")] string? serverName,
        CancellationToken ct)
        => Results.Ok(await client.ListMcpToolsAsync(serverName, ct));

    public static async Task<IResult> InvokeMcpTool(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] McpInvokeRequest body,
        CancellationToken ct)
        => Results.Ok(await client.InvokeMcpToolAsync(body, ct));

    // ── Context (3) ──────────────────────────────────────────────────────

    public static async Task<IResult> GetContextHistory(
        [FromServices] IIntelligenceHttpClient client,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
        => Results.Ok(await client.GetContextHistoryAsync(limit, ct));

    public static async Task<IResult> PostContextFeedback(
        [FromServices] IIntelligenceHttpClient client,
        [FromBody] ContextFeedbackRequest body,
        CancellationToken ct)
        => Results.Ok(await client.PostContextFeedbackAsync(body, ct));

    public static async Task<IResult> GetContextConfig(
        [FromServices] IIntelligenceHttpClient client,
        CancellationToken ct)
        => Results.Ok(await client.GetContextConfigAsync(ct));

    // ── Analytics (3) ────────────────────────────────────────────────────

    public static async Task<IResult> GetKbAnalytics(
        [FromServices] IIntelligenceHttpClient client,
        [FromQuery(Name = "start")] string? start,
        [FromQuery(Name = "end")] string? end,
        CancellationToken ct)
        => Results.Ok(await client.GetAnalyticsAsync(start, end, ct));

    public static async Task<IResult> GetKbUsage(
        [FromServices] IIntelligenceHttpClient client,
        [FromQuery(Name = "start")] string? start,
        [FromQuery(Name = "end")] string? end,
        CancellationToken ct)
        => Results.Ok(await client.GetUsageAsync(start, end, ct));

    public static async Task<IResult> GetKbCosts(
        [FromServices] IIntelligenceHttpClient client,
        [FromQuery(Name = "start")] string? start,
        [FromQuery(Name = "end")] string? end,
        CancellationToken ct)
        => Results.Ok(await client.GetCostsAsync(start, end, ct));
}
