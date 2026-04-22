namespace Tamma.Api.Dtos.KnowledgeBase;

// ─────────────────────────────────────────────────────────────────────────────
// Request DTOs used by endpoints that accept bodies.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/kb/index/trigger.</summary>
public sealed record TriggerIndexRequest(
    bool? FullReindex,
    string? RepositoryPath,
    string[]? ChangedFiles);

/// <summary>Request body for PUT /api/kb/index/config.</summary>
public sealed record UpdateIndexConfigRequest(
    string[]? IncludePatterns,
    string[]? ExcludePatterns,
    object? ChunkingConfig,
    object? EmbeddingConfig,
    object? TriggerConfig);

/// <summary>Request body for POST /api/kb/vector-db/search.</summary>
public sealed record VectorSearchRequest(string Collection, string Query, int? TopK);

/// <summary>Single document inside a vector-upsert batch.</summary>
public sealed record VectorDocument(
    string Id,
    double[] Embedding,
    string? Content,
    object? Metadata);

/// <summary>Request body for POST /api/kb/vector-db/upsert.</summary>
public sealed record VectorUpsertRequest(string Collection, VectorDocument[] Documents);

/// <summary>Request body for DELETE /api/kb/vector-db/delete.</summary>
public sealed record VectorDeleteRequest(string Collection, string[] Ids);

/// <summary>Request body for PUT /api/kb/rag/config.</summary>
public sealed record UpdateRagConfigRequest(
    bool? Enabled,
    object? Sources,
    object? Ranking,
    object? Assembly,
    object? Caching);

/// <summary>Request body for POST /api/kb/rag/query.</summary>
public sealed record RagQueryRequest(
    string Query,
    int? TopK,
    string[]? Sources,
    int? MaxTokens);

/// <summary>Request body for PUT /api/kb/mcp/config.</summary>
public sealed record UpdateMcpConfigRequest(McpServerConfig[]? Servers);

public sealed record McpServerConfig(
    string Name,
    string Transport,
    bool Enabled,
    string? Url);

/// <summary>Request body for POST /api/kb/mcp/tools/invoke.</summary>
public sealed record McpInvokeRequest(
    string ServerName,
    string ToolName,
    object? Arguments);

/// <summary>Request body for POST /api/kb/context/feedback.</summary>
public sealed record ContextFeedbackRequest(
    string RequestId,
    bool Helpful,
    string? Notes);
