using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Core.Documents.Resume;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-14 (Design Decision D8) — the engine-side READ SEAM both planning-family bindings
/// need: the <c>plan-generation</c> binding reads the latest accepted <c>decomposition</c>
/// (the consumes side, D4); the <c>plan-review</c> shim reads the latest accepted <c>plan</c>
/// plus its round lineage (D1). It wraps 39-10's <see cref="ILifecycleReEntryService"/>,
/// resolved via <c>context.GetService&lt;T&gt;()</c> (the <c>ComputeReEntryPositionActivity</c>
/// service-resolution pattern).
///
/// <para><b>Compose path (no interface extension).</b> 39-10's
/// <see cref="ILifecycleReEntryService.ReconstructAsync"/> already returns a
/// <see cref="LifecycleResumeStage.Complete"/> position carrying the accepted revision's
/// <see cref="LifecycleResumePosition.ExistingDocumentId"/> when a document of the type is
/// accepted for the issue; this activity composes that with
/// <see cref="ILifecycleReEntryService.GetDocumentBodyAsync"/> to surface the accepted body —
/// so it reuses the 39-10/39-11 read machinery without extending the interface.</para>
///
/// <para><b>Fail-closed.</b> A null/absent service, no accepted document, or ANY exception out
/// of the read (a store/stream disagreement <c>ReconstructAsync</c> throws, a missing tenant)
/// yields <see cref="Found"/> = <c>false</c> — the read never throws out of the binding graph.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Fetch Latest Accepted Document",
    "Read the latest accepted document body + round lineage for an issue+type from the 39-11 store (39-14 read seam)",
    Kind = ActivityKind.Task
)]
public class FetchLatestAcceptedDocumentActivity : Activity
{
    private readonly ILogger<FetchLatestAcceptedDocumentActivity>? _logger;

    [Input(Description = "Issue / requirement id (the lineage anchor)")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Document type key to read (e.g. decomposition, plan)")]
    public Input<string?> DocumentTypeKey { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → ambient tenant)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>Whether an accepted document of the type exists for the issue.</summary>
    [Output(Description = "True when an accepted document of the type was found")]
    public Output<bool> Found { get; set; } = default!;

    /// <summary>The accepted document's store id (empty when not found).</summary>
    [Output(Description = "Accepted document id (empty when not found)")]
    public Output<string> DocumentId { get; set; } = default!;

    /// <summary>The accepted document's payload body JSON (<c>"{}"</c> when not found).</summary>
    [Output(Description = "Accepted document body JSON")]
    public Output<string> DocumentJson { get; set; } = default!;

    /// <summary>A minimal round-lineage projection (documentId + revision + rounds), <c>"{}"</c> when not found.</summary>
    [Output(Description = "Round-lineage projection JSON")]
    public Output<string> LineageJson { get; set; } = default!;

    [System.Text.Json.Serialization.JsonConstructor]
    public FetchLatestAcceptedDocumentActivity() { }

    public FetchLatestAcceptedDocumentActivity(ILogger<FetchLatestAcceptedDocumentActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // Default (fail-closed) outputs — overwritten only on a genuine accepted read.
        context.Set(Found, false);
        context.Set(DocumentId, string.Empty);
        context.Set(DocumentJson, "{}");
        context.Set(LineageJson, "{}");

        var service = context.GetService<ILifecycleReEntryService>();
        if (service is null)
        {
            _logger?.LogWarning(
                "No ILifecycleReEntryService registered; FetchLatestAcceptedDocument reports not-found (fail-closed).");
            await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete
            return;
        }

        var issueId = IssueId.GetOrDefault(context) ?? string.Empty;
        var documentType = DocumentTypeKey.GetOrDefault(context) ?? string.Empty;
        var tenantId = DocumentEvents.ParseTenantId(TenantId.GetOrDefault(context));

        if (string.IsNullOrWhiteSpace(issueId) || string.IsNullOrWhiteSpace(documentType))
        {
            await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete
            return;
        }

        try
        {
            var position = await service
                .ReconstructAsync(tenantId, issueId, documentType, context.CancellationToken)
                .ConfigureAwait(false);

            // Only a Complete position carries an ACCEPTED revision (D8 compose path).
            if (position.ResumeAt != LifecycleResumeStage.Complete ||
                position.ExistingDocumentId is not Guid docId)
            {
                await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete
                return;
            }

            var envelope = await service
                .GetDocumentBodyAsync(tenantId, docId, context.CancellationToken)
                .ConfigureAwait(false);
            if (envelope is null)
            {
                await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete
                return;
            }

            var revision = position.ExistingRevision ?? 1;
            var rounds = Enumerable.Range(1, Math.Max(revision, 1)).ToArray();

            context.Set(Found, true);
            context.Set(DocumentId, docId.ToString());
            context.Set(DocumentJson, envelope.Payload.GetRawText());
            context.Set(LineageJson, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["documentId"] = docId.ToString(),
                ["revision"] = revision,
                ["rounds"] = rounds,
            }));
        }
        catch (Exception ex)
        {
            // A read failure (store/stream disagreement, missing tenant, …) is a not-found —
            // never a throw out of the binding's read (D8 fail-closed).
            _logger?.LogWarning(ex,
                "FetchLatestAcceptedDocument read failed for issue {IssueId} type {DocumentType}; reporting not-found.",
                issueId, documentType);
        }
        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
    }
}
