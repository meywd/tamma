using System.Text.Json;
using Tamma.Api.Services.Documents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 39-11 — the document store's HTTP surface: two tenant-facing lineage
/// reads + a single-document fetch (<c>MemberAccess</c>, D9), plus the engine→API
/// persist/status write seam (<c>EngineServiceOnly</c>, D6). Handlers follow the
/// <see cref="ReposRunsEndpoints"/> posture: each opens with the verbatim
/// fail-closed null-tenant guard, and the bare-id fetch re-checks entity-level
/// tenant ownership after load (defence-in-depth against id guessing, AC5/AC6).
///
/// <para>Responses serialize through <see cref="DocumentJson.Options"/> so the
/// wire contract (millisecond ISO timestamps, explicit property names) is
/// deliberate and matches the shared <c>Tamma.Core</c> lineage DTOs.</para>
/// </summary>
public static class DocumentEndpoints
{
    // ─── GET /api/documents/issues/{issueId}/lineage (AC3) ────────────────────

    /// <summary>
    /// The full document trail for an issue, grouped by type in first-produced
    /// order with reviews attached to their subject and a terminal outcome. An
    /// issue with no documents is a valid state → 200 with empty <c>types</c>, NOT
    /// 404. <paramref name="audience"/> (Story 41-1c AC3, query string) optionally
    /// filters to rows carrying that audience tag; an out-of-vocabulary value is a
    /// 400 (<c>unknown_audience</c>), never an empty 200 — silence would read as
    /// "no documents" when the truth is "no such audience".
    /// </summary>
    public static async Task<IResult> GetIssueLineage(
        string issueId,
        IDocumentInstanceRepository repo,
        ITenantContext tc,
        CancellationToken ct,
        string? audience = null)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return Results.NotFound(new { error = "no_active_tenant" });

        if (audience is not null && !ProseAudienceExtensions.TryParse(audience, out _))
            return Results.BadRequest(new
            {
                error = "unknown_audience",
                detail = $"'{audience}' is not a prose audience. Valid values: " +
                    string.Join(", ", Enum.GetValues<ProseAudience>().Select(a => a.ToWire())) + ".",
            });

        var rows = await repo.ListByIssueAsync(tenantId, issueId, audience, ct).ConfigureAwait(false);
        var lineage = LineageAssembler.Assemble(issueId, rows);
        return Results.Json(lineage, DocumentJson.Options, statusCode: StatusCodes.Status200OK);
    }

    // ─── GET /api/documents/issues/{issueId}/latest (AC4) ─────────────────────

    /// <summary>
    /// The latest accepted instance per document type for an issue — exactly the
    /// read 39-10's re-entry consumes in-process. Superseded and draft revisions
    /// never appear.
    /// </summary>
    public static async Task<IResult> GetLatestAccepted(
        string issueId,
        IDocumentInstanceRepository repo,
        ITenantContext tc,
        CancellationToken ct)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return Results.NotFound(new { error = "no_active_tenant" });

        var rows = await repo.GetLatestAcceptedAsync(tenantId, issueId, ct).ConfigureAwait(false);
        var latest = LineageAssembler.AssembleLatest(issueId, rows);
        return Results.Json(latest, DocumentJson.Options, statusCode: StatusCodes.Status200OK);
    }

    // ─── GET /api/documents/{documentId} (AC5) ────────────────────────────────

    /// <summary>
    /// A single document instance by id, with a defence-in-depth entity-level
    /// tenant re-check after load (a guessed cross-tenant id reads nothing → 404).
    /// </summary>
    public static async Task<IResult> GetDocument(
        Guid documentId,
        IDocumentInstanceRepository repo,
        ITenantContext tc,
        CancellationToken ct)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return Results.NotFound(new { error = "no_active_tenant" });

        var row = await repo.GetByIdAsync(tenantId, documentId, ct).ConfigureAwait(false);
        if (row is null || row.TenantId != tenantId)
            return Results.NotFound(new { error = "document_not_found" });

        return Results.Json(LineageAssembler.AssembleDocument(row), DocumentJson.Options, statusCode: StatusCodes.Status200OK);
    }

    // ─── POST /api/engine/documents (D6) ──────────────────────────────────────

    /// <summary>
    /// Engine→API persist callback: deserialize the envelope, validate + write
    /// through the repository (the SOLE writer). Fail-loud — a validation /
    /// registry error surfaces as a 400 with the <see cref="TammaError.Code"/> so
    /// the engine's persist activity faults (the document is the product, not
    /// telemetry).
    /// </summary>
    public static async Task<IResult> PersistFromEngine(
        PersistDocumentRequest req,
        IDocumentInstanceRepository repo,
        ITenantContext tc,
        CancellationToken ct)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "no_active_tenant" });
        if (string.IsNullOrWhiteSpace(req.EnvelopeJson))
            return Results.BadRequest(new { error = "envelopeJson is required" });

        DocumentEnvelope envelope;
        try
        {
            envelope = DocumentJson.Deserialize(req.EnvelopeJson);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_envelope", detail = ex.Message });
        }

        try
        {
            var row = await repo.InsertAsync(tenantId, envelope, req.CorrelatingEventId, ct)
                .ConfigureAwait(false);
            return Results.Created(
                $"/api/documents/{row.Id}",
                new { id = row.Id, revision = row.Revision, status = row.Status });
        }
        catch (TammaError err)
        {
            return Results.BadRequest(new { error = err.Code, detail = err.Message });
        }
    }

    // ─── POST /api/engine/documents/{documentId}/status (D6) ──────────────────

    /// <summary>
    /// Engine→API status-transition callback. Parses the wire status, transitions
    /// through the repository, maps a <see cref="TammaError"/> (unknown status /
    /// illegal 'superseded' / not-found) to a 400 with its code.
    /// </summary>
    public static async Task<IResult> SetStatusFromEngine(
        Guid documentId,
        SetDocumentStatusRequest req,
        IDocumentInstanceRepository repo,
        ITenantContext tc,
        CancellationToken ct)
    {
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "no_active_tenant" });

        try
        {
            var status = DocumentInstanceStatusExtensions.Parse(req.Status);
            var row = await repo.SetStatusAsync(tenantId, documentId, status, req.CorrelatingEventId, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { id = row.Id, status = row.Status });
        }
        catch (TammaError err)
        {
            return Results.BadRequest(new { error = err.Code, detail = err.Message });
        }
    }
}

/// <summary>
/// Engine→API persist request (D6). The envelope rides as a JSON string
/// (serialized via <see cref="DocumentJson"/>) so the API re-deserializes through
/// the same canonical options; the tenant is asserted by the <c>X-Tenant-Id</c>
/// header (never the body).
/// </summary>
public sealed record PersistDocumentRequest(string EnvelopeJson, Guid? CorrelatingEventId);

/// <summary>Engine→API status-transition request (D6).</summary>
public sealed record SetDocumentStatusRequest(string Status, Guid? CorrelatingEventId);
