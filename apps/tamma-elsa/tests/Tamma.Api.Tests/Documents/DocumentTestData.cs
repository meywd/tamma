using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Core.Documents;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 — shared builders for the document store suites: valid typed bodies
/// (from the registered 39-3/39-4 type examples), <see cref="DocumentInstance"/>
/// rows for the pure assembler/guard tests, and <see cref="DocumentEnvelope"/>s for
/// the repository integration tests.
/// </summary>
internal static class DocumentTestData
{
    public const string DecompositionType = "decomposition";
    public const string ReviewType = "review";

    /// <summary>A valid decomposition body (39-3's valid two-task-chain example).</summary>
    public const string ValidDecompositionBody =
        """
        {
          "summary": "Split rate limiting into middleware then config, preserving per-tenant protection.",
          "subtasks": [
            { "id": "ST-1", "title": "Token-bucket middleware", "description": "Limiter keyed by tenant id", "acceptanceCriteria": "over-limit requests get 429", "estimateHours": 6, "complexity": "medium", "dependsOn": [] },
            { "id": "ST-2", "title": "Per-tenant config", "description": "Read the limit from tenant config", "acceptanceCriteria": "limit is configurable", "estimateHours": 4, "complexity": "low", "dependsOn": ["ST-1"] }
          ]
        }
        """;

    /// <summary>A valid review body whose subject documentId points at <paramref name="subjectDocumentId"/>.</summary>
    public static string ValidReviewBody(Guid subjectDocumentId) =>
        $$"""
        {
          "subject": { "kind": "document", "documentId": "{{subjectDocumentId:D}}", "documentType": "decomposition" },
          "decision": "request-changes",
          "summary": "The plan is sound but omits migration ordering.",
          "issues": [
            { "severity": "critical", "category": "correctness", "description": "Migration runs before the table exists", "suggestedFix": "Reorder task ST-2 before ST-1" }
          ]
        }
        """;

    public static JsonElement Payload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Semantic JSON equality. The <c>body</c> column is <c>jsonb</c>, which Postgres
    /// re-serializes on read (whitespace stripped, object keys reordered), so a row
    /// read back never matches the raw <c>GetRawText()</c> text of the in-memory
    /// insert result byte-for-byte. Compare the parsed values instead — the store's
    /// contract is that the body is preserved as a document, not as exact bytes.
    /// </summary>
    public static bool SameJson(string a, string b) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(a),
            System.Text.Json.Nodes.JsonNode.Parse(b));

    /// <summary>Build a stored row (assembler/guard tests — no DB).</summary>
    public static DocumentInstance Row(
        Guid id, string issueId, string type, string status, int revision,
        string body, Guid tenantId,
        Guid? supersedesDocumentId = null, Guid? parentDocumentId = null,
        Guid? correlatingEventId = null, DateTime? createdAt = null,
        string role = "senior_developer", string action = "decompose-issue")
    {
        var created = createdAt ?? DateTime.UtcNow;
        return new DocumentInstance
        {
            Id = id,
            IssueId = issueId,
            DocumentType = type,
            Status = status,
            Revision = revision,
            BodyJson = body,
            TenantId = tenantId,
            SupersedesDocumentId = supersedesDocumentId,
            ParentDocumentId = parentDocumentId,
            CorrelatingEventId = correlatingEventId,
            ProducedByRole = role,
            ProducedByAction = action,
            ProducedByWorkflow = "issue-decomposition",
            SchemaVersion = 1,
            CorrelationId = "corr-1",
            CreatedAt = created,
            UpdatedAt = created,
        };
    }

    /// <summary>Build a decomposition envelope in a chosen state (repository tests).</summary>
    public static DocumentEnvelope DecompositionEnvelope(
        string issueId, DocumentState state = DocumentState.Draft,
        Guid? supersedesDocumentId = null, string body = ValidDecompositionBody,
        DateTimeOffset? now = null)
    {
        var draft = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Decomposition, 1, issueId, "corr-1",
            DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition"),
            Payload(body),
            supersedesDocumentId: supersedesDocumentId,
            now: now);
        return state == DocumentState.Draft ? draft : draft with { State = state };
    }

    /// <summary>A fake tenant context bound to a fixed (or absent) tenant id.</summary>
    public sealed class FakeTenantContext(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    /// <summary>Routes each tenant id to its own search-path schema connection.</summary>
    public sealed class SchemaRoutingFactory(string baseCs) : ITenantDbContextFactory
    {
        private readonly Dictionary<Guid, string> _schemas = new();

        public SchemaRoutingFactory Map(Guid tenantId, string schema)
        {
            _schemas[tenantId] = schema;
            return this;
        }

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            if (!_schemas.TryGetValue(tenantId, out var schema))
                throw new InvalidOperationException($"Tenant {tenantId} not reachable.");

            var cs = new Npgsql.NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema }.ConnectionString;
            var opts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TenantDbContext>()
                .UseNpgsql(cs).Options;
            return new ValueTask<TenantDbContext>(new TenantDbContext(opts, tenantId));
        }
    }
}
