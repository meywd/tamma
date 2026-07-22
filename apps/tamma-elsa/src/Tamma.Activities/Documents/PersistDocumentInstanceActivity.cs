using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-11 (Design Decision D6) — persists a document instance to the tenant's
/// <c>document_instances</c> store through the fail-loud engine→API hop
/// (<c>TammaApiClient.PersistDocumentAsync</c> → <c>POST /api/engine/documents</c>).
///
/// <para>The lifecycle runs in <c>Tamma.ElsaServer</c>, which registers no
/// repository, so the sanctioned engine→store path is HTTP to <c>Tamma.Api</c>.
/// Unlike the best-effort event drain, a persist failure FAULTS this activity
/// (<c>TammaError DOCUMENT.STORE.PERSIST_FAILED</c>) — the document is the
/// lifecycle's product, not telemetry, so it is NEVER swallowed.</para>
///
/// <para><b>Scope note (deferred to 39-12).</b> This activity ships the engine seam;
/// wiring persist+emit adjacently per transition into
/// <c>DocumentLifecycleWorkflow</c>'s graph is deferred to 39-12 (the
/// workflow-rewire pilot). The AC7 linkage — passing the pre-minted transition
/// event Guid to BOTH the emit site (<c>EmitDocumentEventActivity.EventId</c>) and
/// this activity's <see cref="CorrelatingEventId"/> — is the agreed seam.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Persist Document Instance",
    "Persist a document instance to the tenant document_instances store (fail-loud engine→API hop)",
    Kind = ActivityKind.Task
)]
public class PersistDocumentInstanceActivity : Activity
{
    private readonly ILogger<PersistDocumentInstanceActivity>? _logger;

    [Input(Description = "Serialized DocumentEnvelope JSON (DocumentJson.Serialize output)")]
    public Input<string> EnvelopeJson { get; set; } = default!;

    [Input(Description = "Pre-minted correlating DOCUMENT.* event id (Guid string) — the AC7 linkage")]
    public Input<string?> CorrelatingEventId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (X-Tenant-Id) the store row is scoped to")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [JsonConstructor]
    public PersistDocumentInstanceActivity() { }

    public PersistDocumentInstanceActivity(ILogger<PersistDocumentInstanceActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var envelopeJson = EnvelopeJson.Get(context);
        if (string.IsNullOrWhiteSpace(envelopeJson))
            throw Failed("PersistDocumentInstanceActivity requires a non-empty EnvelopeJson.", null);

        var client = context.GetService<TammaApiClient>()
            ?? throw Failed(
                "TammaApiClient is not registered — the engine cannot reach the document store.", null);

        Guid? correlatingEventId = Guid.TryParse(CorrelatingEventId.Get(context), out var g) ? g : null;
        var tenantId = TenantId.Get(context);

        try
        {
            await client
                .PersistDocumentAsync(
                    new PersistDocumentRequest(envelopeJson, correlatingEventId),
                    tenantId,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (TammaError)
        {
            throw; // already the fail-loud shape — never re-wrap/swallow.
        }
        catch (Exception ex)
        {
            throw Failed($"Failed to persist document instance: {ex.Message}", ex);
        }

        _logger?.LogInformation("Persisted document instance (tenant {Tenant})", tenantId);
    }

    private static TammaError Failed(string message, Exception? inner) => new(
        "DOCUMENT.STORE.PERSIST_FAILED",
        inner is null ? message : $"{message} ({inner.GetType().Name})",
        new Dictionary<string, object?> { ["inner"] = inner?.Message },
        retryable: true,
        severity: TammaErrorSeverity.High);
}
