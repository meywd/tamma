using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-6 (Design Decision D5/D6) — the ACCEPT-stage publish step. Deserializes
/// the <see cref="AcceptanceRequest"/> the lifecycle built and hands it to the
/// registered <see cref="IAcceptanceRequestPublisher"/> (resolved via
/// <c>context.GetService&lt;T&gt;()</c> — the <c>EventPersistenceMiddleware</c>
/// service-resolution pattern, no captive dependency).
///
/// <para><b>Fail-loud ONLY on a missing publisher.</b> If no
/// <see cref="IAcceptanceRequestPublisher"/> is registered the activity throws
/// <c>DOCUMENT.ACCEPT.PUBLISH_FAILED</c> — that is a wiring bug. A publisher
/// TRANSPORT error is logged at ERROR and swallowed: the 39-8 gate still suspends
/// the lifecycle, and delivery is 39-18's outbox job (the request "waits, never
/// defaulted"). This activity registers NO bookmark and takes NO decision.</para>
/// </summary>
[Activity(
    "Tamma.Documents",
    "Publish Acceptance Request",
    "Publish the AcceptanceRequest on the workflow↔orchestrator channel before the decision gate suspends",
    Kind = ActivityKind.Task
)]
public class PublishAcceptanceRequestActivity : Activity
{
    private readonly ILogger<PublishAcceptanceRequestActivity>? _logger;

    [Input(Description = "The serialized AcceptanceRequest (DocumentJson.Options)")]
    public Input<string> RequestJson { get; set; } = default!;

    [JsonConstructor]
    public PublishAcceptanceRequestActivity() { }

    public PublishAcceptanceRequestActivity(ILogger<PublishAcceptanceRequestActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var publisher = context.GetService<IAcceptanceRequestPublisher>();
        if (publisher is null)
        {
            throw new TammaError(
                "DOCUMENT.ACCEPT.PUBLISH_FAILED",
                "No IAcceptanceRequestPublisher is registered — the ACCEPT stage cannot publish the " +
                "acceptance request. Register LoggingAcceptanceRequestPublisher (or the 39-18 delivery " +
                "implementation) in the engine's service collection.",
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        var requestJson = RequestJson.Get(context);
        AcceptanceRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AcceptanceRequest>(requestJson, DocumentJson.Options);
        }
        catch (JsonException ex)
        {
            throw new TammaError(
                "DOCUMENT.ACCEPT.PUBLISH_FAILED",
                $"The acceptance-request payload could not be deserialized: {ex.Message}",
                new Dictionary<string, object?> { ["jsonLength"] = requestJson?.Length ?? 0 },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        if (request is null)
        {
            throw new TammaError(
                "DOCUMENT.ACCEPT.PUBLISH_FAILED",
                "The acceptance-request payload deserialized to null.",
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        try
        {
            await publisher.PublishAsync(request, context.CancellationToken);
            _logger?.LogInformation(
                "Published AcceptanceRequest for session {Session} document {Document}",
                request.DecisionSessionId, request.Document.Id);
        }
        catch (Exception ex)
        {
            // Transport failure — the gate still suspends; 39-18's outbox retries delivery.
            _logger?.LogError(ex,
                "AcceptanceRequest publish transport failed for session {Session}; the gate still " +
                "suspends (delivery is 39-18's outbox job), continuing.",
                request.DecisionSessionId);
        }
    }
}
