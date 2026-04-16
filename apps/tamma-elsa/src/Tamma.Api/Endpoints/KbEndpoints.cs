namespace Tamma.Api.Endpoints;

public static class KbEndpoints
{
    // Index endpoints (6)
    public static Task<IResult> GetIndexStatus() => Stub(new { status = "idle", indexed = 0, pending = 0 });
    public static Task<IResult> TriggerIndex() => Stub(new { message = "Indexing triggered (stub)" });
    public static Task<IResult> GetIndexConfig() => Stub(new { configured = false });
    public static Task<IResult> UpdateIndexConfig() => Stub(new { message = "Index config updated (stub)" });
    public static Task<IResult> GetIndexStats() => Stub(new { documents = 0, chunks = 0, lastIndexed = (DateTime?)null });
    public static Task<IResult> ClearIndex() => Stub(new { message = "Index cleared (stub)" });

    // Vector DB endpoints (6)
    public static Task<IResult> GetVectorDbStatus() => Stub(new { status = "not_configured" });
    public static Task<IResult> SearchVectors() => Stub(new { results = Array.Empty<object>() });
    public static Task<IResult> UpsertVectors() => Stub(new { message = "Vectors upserted (stub)", count = 0 });
    public static Task<IResult> DeleteVectors() => Stub(new { message = "Vectors deleted (stub)" });
    public static Task<IResult> GetVectorCollections() => Stub(Array.Empty<object>());
    public static Task<IResult> GetVectorStats() => Stub(new { totalVectors = 0, dimensions = 0 });

    // RAG endpoints (4)
    public static Task<IResult> GetRagConfig() => Stub(new { enabled = false });
    public static Task<IResult> UpdateRagConfig() => Stub(new { message = "RAG config updated (stub)" });
    public static Task<IResult> QueryRag() => Stub(new { answer = "", sources = Array.Empty<object>() });
    public static Task<IResult> GetRagMetrics() => Stub(new { queries = 0, avgLatencyMs = 0 });

    // MCP endpoints (8)
    public static Task<IResult> ListMcpServers() => Stub(Array.Empty<object>());
    public static Task<IResult> GetMcpServer(string id) => Stub(new { id, status = "not_found" });
    public static Task<IResult> StartMcpServer(string id) => Stub(new { message = $"MCP server {id} start requested (stub)" });
    public static Task<IResult> StopMcpServer(string id) => Stub(new { message = $"MCP server {id} stop requested (stub)" });
    public static Task<IResult> GetMcpConfig() => Stub(new { servers = Array.Empty<object>() });
    public static Task<IResult> UpdateMcpConfig() => Stub(new { message = "MCP config updated (stub)" });
    public static Task<IResult> ListMcpTools() => Stub(Array.Empty<object>());
    public static Task<IResult> InvokeMcpTool() => Stub(new { message = "Tool invoked (stub)" });

    // Context endpoints (3)
    public static Task<IResult> GetContextHistory() => Stub(new { history = Array.Empty<object>() });
    public static Task<IResult> PostContextFeedback() => Stub(new { message = "Feedback recorded (stub)" });
    public static Task<IResult> GetContextConfig() => Stub(new { maxTokens = 100000, strategy = "sliding_window" });

    // Analytics endpoints (3)
    public static Task<IResult> GetKbAnalytics() => Stub(new { queries = 0, indexedDocs = 0, hitRate = 0.0 });
    public static Task<IResult> GetKbUsage() => Stub(new { daily = Array.Empty<object>() });
    public static Task<IResult> GetKbCosts() => Stub(new { totalCost = 0.0, breakdown = Array.Empty<object>() });

    private static Task<IResult> Stub(object response) =>
        Task.FromResult(Results.Ok(response));
}
