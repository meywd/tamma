using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;

namespace Tamma.Activities.Documents;

/// <summary>
/// 2026-08-13 (engine-driven E2E follow-up) — the ENGINE-HOST implementation of
/// <see cref="ILifecycleReEntryService"/>: a latest-accepted read over the API's
/// 39-11 HTTP surface (<c>GET /api/documents/issues/{issueId}/latest</c> +
/// <c>GET /api/documents/{documentId}</c>).
///
/// <para><b>Why it exists.</b> The REAL <see cref="LifecycleReEntryService"/> reads
/// the document store in-process (<c>IDocumentInstanceRepository</c> +
/// <c>IEventRepository</c>) — dependencies only the API host composes; the engine
/// host has no tenant-routed store connection at all, so registering the real
/// service there is a landmine that detonates at first resolution. Until this
/// service existed the engine shipped with the Null seam permanently selected,
/// which made <c>FetchLatestAcceptedDocumentActivity</c> structurally blind: the
/// <c>plan-review</c> shim could NEVER see the plan the lifecycle had just
/// accepted, so every engine-driven cycle terminated needs-human.</para>
///
/// <para><b>Deliberately coarser than the real service.</b> The API surface exposes
/// the latest-ACCEPTED read, not the DCB event fold — so this implementation
/// reconstructs only the two positions that read can prove:
/// <see cref="LifecycleResumeStage.Complete"/> (an accepted document of the type
/// exists) and <see cref="LifecycleResumeStage.Produce"/> (none does). Mid-flight
/// positions (<c>Review</c>/<c>Accept</c>) degrade to a fresh Produce — exactly the
/// pre-re-entry behaviour, and strictly better than the Null seam (which cannot
/// even see Complete). The consumers in the engine's binding graph
/// (<see cref="FetchLatestAcceptedDocumentActivity"/>, the plan-review shim, the
/// plan-generation consumes-read) only branch on Complete, so they lose
/// nothing.</para>
///
/// <para><b>Fail-closed.</b> A transport failure or non-success status (other than
/// the document fetch's 404) THROWS a retryable <see cref="TammaError"/> — never a
/// silent "Fresh", which would read as "no accepted document" and re-produce work
/// that already exists. Callers already treat a throw as not-found-fail-closed
/// (<c>FetchLatestAcceptedDocumentActivity</c>'s catch) or surface it
/// (<c>ComputeReEntryPositionActivity</c>).</para>
///
/// <para><b>Not a mediation-surface change.</b> This is a READ of Tamma's own API
/// over the engine→API internal hop (the <c>ReportCycleResultActivity</c> callback
/// precedent), not an external effect — it deliberately does NOT extend
/// <c>TammaApiClient</c>, whose public surface is exactly pinned by the 43-8
/// mediation sweep (adding read methods there would mean re-seeding a shrink-only
/// ratchet for a plain internal read).</para>
/// </summary>
public sealed class HttpLifecycleReEntryService : ILifecycleReEntryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string? _token;
    private readonly ILogger<HttpLifecycleReEntryService> _logger;

    public HttpLifecycleReEntryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<HttpLifecycleReEntryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _baseUrl = (configuration?["Tamma:ApiUrl"]
                    ?? Environment.GetEnvironmentVariable("TAMMA_API_URL")
                    ?? "http://localhost:3000").TrimEnd('/');
        _token = configuration?["Tamma:ApiToken"]
                 ?? Environment.GetEnvironmentVariable("TAMMA_API_TOKEN");
    }

    public async Task<LifecycleResumePosition> ReconstructAsync(
        Guid? tenantId, string issueId, string documentTypeKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            return LifecycleResumePosition.Fresh(documentTypeKey, "No issue id supplied; running fresh.");

        var url = $"{_baseUrl}/api/documents/issues/{Uri.EscapeDataString(issueId)}/latest";
        using var response = await SendAsync(url, tenantId, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw ReadFailed(url, response.StatusCode);

        var latest = await response.Content
            .ReadFromJsonAsync<LatestAcceptedDocuments>(DocumentJson.Options, ct)
            .ConfigureAwait(false);

        // `documents` is optional in the response body — a 200 that omits it
        // must read as "nothing accepted yet", not NRE.
        var entry = latest?.Documents?
            .FirstOrDefault(d => string.Equals(d.DocumentType, documentTypeKey, StringComparison.Ordinal));
        if (entry is null)
            return LifecycleResumePosition.Fresh(
                documentTypeKey,
                $"No accepted '{documentTypeKey}' for issue '{issueId}' (HTTP latest-accepted read); running fresh.");

        return new LifecycleResumePosition
        {
            DocumentTypeKey = documentTypeKey,
            ResumeAt = LifecycleResumeStage.Complete,
            ExistingDocumentId = entry.Id,
            ExistingRevision = entry.Revision,
            Basis = $"'{documentTypeKey}' revision {entry.Revision} accepted (HTTP latest-accepted read).",
        };
    }

    public async Task<DocumentEnvelope?> GetDocumentBodyAsync(
        Guid? tenantId, Guid documentId, CancellationToken ct)
    {
        if (documentId == Guid.Empty) return null;

        var url = $"{_baseUrl}/api/documents/{documentId}";
        using var response = await SendAsync(url, tenantId, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw ReadFailed(url, response.StatusCode);

        var entry = await response.Content
            .ReadFromJsonAsync<LineageDocumentEntry>(DocumentJson.Options, ct)
            .ConfigureAwait(false);
        return entry is null ? null : MapEnvelope(entry);
    }

    // ── transport ─────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(string url, Guid? tenantId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(HttpLifecycleReEntryService));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            // Single-user gates dispatch tenantless — the API's service-plane
            // binding resolves the sole user's personal tenant; SaaS calls carry
            // the explicit scope (the TammaApiClient X-Tenant-Id convention).
            if (tenantId is Guid t && t != Guid.Empty)
                request.Headers.TryAddWithoutValidation("X-Tenant-Id", t.ToString());
            return await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Re-entry HTTP read transport failure");
            throw new TammaError(
                "DOCUMENT.REENTRY.HTTP_READ_FAILED",
                $"Re-entry read failed in transport: {ex.GetType().Name}.",
                retryable: true,
                severity: TammaErrorSeverity.Medium);
        }
    }

    private TammaError ReadFailed(string url, HttpStatusCode status)
    {
        // URL omitted from the log line (TammaApiClient F-note: interpolated ids
        // do not belong on the rotating warn log; the status is the triage signal).
        _logger.LogWarning("Re-entry HTTP read returned {Status}", (int)status);
        return new TammaError(
            "DOCUMENT.REENTRY.HTTP_READ_FAILED",
            $"Re-entry read returned HTTP {(int)status}.",
            retryable: true,
            severity: TammaErrorSeverity.Medium);
    }

    // ── wire entry → envelope (the LifecycleReEntryService.MapEnvelope shape) ──

    private static DocumentEnvelope MapEnvelope(LineageDocumentEntry entry)
    {
        JsonElement payload;
        if (entry.Body.ValueKind is JsonValueKind.Undefined)
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        else
        {
            payload = entry.Body.Clone();
        }

        return new DocumentEnvelope
        {
            Id = entry.Id,
            Type = entry.DocumentType,
            // The lineage wire entry carries no schemaVersion/correlationId — the
            // consumers of this read (FetchLatestAcceptedDocumentActivity) use only
            // Id/Type/Payload; the anchors below are honest reconstructions.
            SchemaVersion = 1,
            IssueId = entry.IssueId,
            CorrelationId = entry.IssueId,
            ParentDocumentId = entry.ParentDocumentId,
            SupersedesDocumentId = entry.SupersedesDocumentId,
            Audience = entry.Audience,
            ProducedBy = new DocumentProducer
            {
                Role = entry.ProducedByRole,
                Action = entry.ProducedByAction,
                WorkflowDefinitionId = "llm-call",
            },
            State = MapState(entry.Status),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            Payload = payload,
        };
    }

    /// <summary>Store status wire → envelope state (the
    /// <see cref="LifecycleReEntryService"/> guard-path mapping, verbatim).</summary>
    private static DocumentState MapState(string? status) => status switch
    {
        "draft" => DocumentState.Draft,
        "validated" => DocumentState.Validated,
        "in_review" => DocumentState.Reviewed,
        "accepted" => DocumentState.Accepted,
        "rejected" => DocumentState.Rejected,
        "escalated" => DocumentState.Escalated,
        // 'superseded' has no envelope state of its own; treat as a validated draft
        // for reconstruction purposes (the guard only re-enters non-superseded rows).
        "superseded" => DocumentState.Validated,
        _ => DocumentState.Draft,
    };
}
