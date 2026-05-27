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
///
/// <para><b>Thread safety</b>: <see cref="EmitAsync"/> is guarded by a
/// private lock so concurrent calls from async continuations on different
/// thread-pool threads cannot corrupt the backing list. Each test fixture
/// that uses this double must create a fresh instance in its
/// <c>[SetUp]</c> method — the list is never shared across fixture
/// instances.</para>
/// </summary>
internal sealed class RecordingSecretAccessAuditor : ISecretAccessAuditor
{
    private readonly object _lock = new();

    public List<SecretAuditEvent> Events { get; } = new();

    public Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Events.Add(auditEvent);
        }
        return Task.CompletedTask;
    }
}
