using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Core;
using Tamma.Core.Documents.Resume;
using CoreDocumentJson = Tamma.Core.Documents.DocumentJson;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-10 (Design Decision D6) — the ONE re-entry node the generic lifecycle's
/// Init gains. Resolves <see cref="ILifecycleReEntryService"/> via
/// <c>context.GetService&lt;T&gt;()</c> (the <c>EventPersistenceMiddleware</c>
/// activity-service pattern), reconstructs the typed
/// <see cref="LifecycleResumePosition"/> for the issue+type from durable truth, and
/// surfaces it as <see cref="PositionJson"/> for the workflow's guard
/// <c>FlowDecision</c>s. When the position skips produce it also surfaces the existing
/// revision body (<see cref="ExistingDocumentJson"/>) so the guarded stage reviews /
/// accepts the stored revision rather than re-producing.
///
/// <para>Emits <c>DOCUMENT.REENTERED</c> ONLY when <c>ResumeAt != Produce</c> — a
/// fresh run is not a re-entry. A missing service is a fail-loud
/// <c>DOCUMENT.REENTRY.SERVICE_UNREGISTERED</c> (D6) rather than a silent skip.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Compute Re-Entry Position",
    "Reconstruct the lifecycle resume position for an issue+type from the document store + DCB events (39-10 crash re-entry)",
    Kind = ActivityKind.Task
)]
public class ComputeReEntryPositionActivity : Activity
{
    private readonly ILogger<ComputeReEntryPositionActivity>? _logger;

    [Input(Description = "Issue / requirement id (the re-entry lineage anchor)")]
    public Input<string?> IssueId { get; set; } = new((string?)null);

    [Input(Description = "Document type key being (re)produced")]
    public Input<string?> DocumentType { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (empty / single-user → ambient tenant)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Input(Description = "Correlation id (tag threading on DOCUMENT.REENTERED)")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    /// <summary>The serialized <see cref="LifecycleResumePosition"/> the guard FlowDecisions read.</summary>
    [Output(Description = "Serialized LifecycleResumePosition (39-10)")]
    public Output<string> PositionJson { get; set; } = default!;

    /// <summary>The existing revision body (serialized DocumentEnvelope) when the position skips produce; else empty.</summary>
    [Output(Description = "Existing document envelope JSON when skipping produce (else empty)")]
    public Output<string> ExistingDocumentJson { get; set; } = default!;

    [JsonConstructor]
    public ComputeReEntryPositionActivity() { }

    public ComputeReEntryPositionActivity(ILogger<ComputeReEntryPositionActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetService<ILifecycleReEntryService>()
            ?? throw new TammaError(
                "DOCUMENT.REENTRY.SERVICE_UNREGISTERED",
                "No ILifecycleReEntryService is registered in the engine service provider; the lifecycle " +
                "cannot compute a crash re-entry position. Register LifecycleReEntryService (or the Null seam).",
                retryable: false,
                severity: TammaErrorSeverity.High);

        var issueId = IssueId.GetOrDefault(context) ?? "";
        var documentType = DocumentType.GetOrDefault(context) ?? "";
        var tenantId = DocumentEvents.ParseTenantId(TenantId.GetOrDefault(context));
        var correlationId = CorrelationId.GetOrDefault(context);

        var position = await service
            .ReconstructAsync(tenantId, issueId, documentType, context.CancellationToken)
            .ConfigureAwait(false);

        context.Set(PositionJson, JsonSerializer.Serialize(position, CoreDocumentJson.Options));

        // Thread the existing body only when the position skips produce (Review/Accept/Complete).
        var existingJson = string.Empty;
        if (position.ResumeAt != LifecycleResumeStage.Produce && position.ExistingDocumentId is Guid docId)
        {
            var envelope = await service
                .GetDocumentBodyAsync(tenantId, docId, context.CancellationToken)
                .ConfigureAwait(false);
            if (envelope is not null)
                existingJson = CoreDocumentJson.Serialize(envelope);
        }
        context.Set(ExistingDocumentJson, existingJson);

        // DOCUMENT.REENTERED — re-entry is an operation; operations emit events (D9).
        // Emitted ONLY on an actual re-entry (a fresh Produce is not a re-entry).
        if (position.ResumeAt != LifecycleResumeStage.Produce)
        {
            TammaEventEmitter.Emit(context, this, _logger,
                BuildReenteredEvent(position, issueId, documentType, correlationId, tenantId));
            _logger?.LogInformation(
                "Re-entering {DocumentType} for issue {IssueId} at {ResumeAt}: {Basis}",
                documentType, issueId, position.ResumeAt, position.Basis);
        }
        await context.CompleteActivityAsync(); // 2026-08-13 — bare Activity does NOT auto-complete (see EmitEscalationEventActivity precedent); without this the workflow hangs here forever
    }

    /// <summary>
    /// Map a re-entry position onto its <c>DOCUMENT.REENTERED</c> event. Pure (no Elsa
    /// context); exposed for unit testing the tag/data mapping.
    /// </summary>
    public static TammaEvent BuildReenteredEvent(
        LifecycleResumePosition position,
        string? issueId,
        string? documentType,
        string? correlationId,
        Guid? tenantId)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(issueId)) tags["issueId"] = issueId;
        if (!string.IsNullOrWhiteSpace(documentType)) tags["documentType"] = documentType;
        if (!string.IsNullOrWhiteSpace(correlationId)) tags["correlationId"] = correlationId;
        if (tenantId is Guid t) tags["tenantId"] = t.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["resumeAt"] = position.ResumeAt.ToString(),
            ["skippedStages"] = SkippedStages(position.ResumeAt),
            ["basis"] = position.Basis,
        };
        if (position.ExistingDocumentId is Guid docId) data["existingDocumentId"] = docId.ToString();
        if (position.ExistingRevision is int rev) data["revision"] = rev;

        return new TammaEvent
        {
            EventType = DocumentEvents.Reentered,
            Status = DocumentEvents.StatusForEvent(DocumentEvents.Reentered),
            Tags = tags,
            Data = data,
        };
    }

    /// <summary>The stages a given re-entry skips (audit payload).</summary>
    public static string[] SkippedStages(LifecycleResumeStage stage) => stage switch
    {
        LifecycleResumeStage.Review => new[] { "produce", "validate" },
        LifecycleResumeStage.Accept => new[] { "produce", "validate", "review" },
        LifecycleResumeStage.Complete => new[] { "produce", "validate", "review", "accept" },
        _ => Array.Empty<string>(),
    };
}
