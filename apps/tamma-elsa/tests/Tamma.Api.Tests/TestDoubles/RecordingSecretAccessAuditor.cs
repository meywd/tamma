using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.TestDoubles;

/// <summary>
/// Shared test double for <see cref="ISecretAccessAuditor"/>. Records
/// every emitted <see cref="SecretAuditEvent"/> so reveal/query/migrate
/// tests can assert audit-trail content.
///
/// <para>Consolidates the per-file <c>RecordingAuditor</c> stubs across
/// <c>SecretRevealServiceTests</c>, <c>SecretQueryServiceTests</c>, and
/// <c>StopgapSecretMigratorTests</c> (PF-C4 cleanup).</para>
/// </summary>
internal sealed class RecordingSecretAccessAuditor : ISecretAccessAuditor
{
    public List<SecretAuditEvent> Events { get; } = new();

    public Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
