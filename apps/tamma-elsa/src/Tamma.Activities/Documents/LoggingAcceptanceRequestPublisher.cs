using Microsoft.Extensions.Logging;
using Tamma.Core.Documents.Policy;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-6 (Design Decision D6) — the default <see cref="IAcceptanceRequestPublisher"/>
/// until Story 39-18 lands the real outbox + SignalR delivery. It LOGS the request
/// (decision-session id, document id, issue id, resolved-rules source/version) and
/// is otherwise a no-op: the 39-8 gate still suspends the lifecycle, so a supervised
/// deployment with no orchestrator connected sits waiting on the bookmark — never
/// short-circuited, never defaulted (39-18's "the request waits, never defaulted").
///
/// <para>Registered as the default in <c>Tamma.ElsaServer/Program.cs</c>; tests
/// substitute a capturing fake.</para>
/// </summary>
public sealed class LoggingAcceptanceRequestPublisher : IAcceptanceRequestPublisher
{
    private readonly ILogger<LoggingAcceptanceRequestPublisher> _logger;

    public LoggingAcceptanceRequestPublisher(ILogger<LoggingAcceptanceRequestPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(AcceptanceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation(
            "AcceptanceRequest published (no-op default publisher — 39-18 pending): " +
            "session={Session} document={Document} issue={Issue} rules={Source}@{Version} roundsUsed={Rounds}",
            request.DecisionSessionId,
            request.Document.Id,
            request.IssueId,
            request.Rules.Source.ToWire(),
            request.Rules.Version,
            request.RoundsUsed);

        return Task.CompletedTask;
    }
}
