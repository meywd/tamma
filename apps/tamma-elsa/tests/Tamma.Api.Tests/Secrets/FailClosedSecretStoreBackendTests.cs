using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="FailClosedSecretStoreBackend"/> — the guard
/// registered when a real secret-store database is configured but the
/// envelope KEK is missing. The contract: WRITES fail loud (never persist
/// plaintext to volatile memory); reads return <c>null</c> (absent) and
/// never throw, and deletes are no-ops, so ambient BYOK probes fall through
/// to the platform path instead of crashing — and a future caller that does
/// not catch <see cref="KeyNotFoundException"/> cannot 500 off a read.
/// </summary>
[TestFixture]
public class FailClosedSecretStoreBackendTests
{
    private static readonly Guid SecretId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private FailClosedSecretStoreBackend _backend = null!;

    [SetUp]
    public void SetUp() =>
        _backend = new FailClosedSecretStoreBackend(
            NullLogger<FailClosedSecretStoreBackend>.Instance);

    [Test]
    public async Task PutVersion_FailsLoud_WithReasonCode()
    {
        Func<Task> act = () => _backend.PutVersionAsync(SecretId, 1, "byok-plaintext");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain(FailClosedSecretStoreBackend.ReasonCode)
                .And.Contain(Tamma.Api.Services.Secrets.Postgres.EnvKekProvider.PrimaryEnvVar);
    }

    [Test]
    public async Task PutVersion_NeverPersists_SoSubsequentGetIsAbsent()
    {
        // The write threw; the byte must NOT have landed anywhere. A read
        // therefore reports the version as absent — returning null (never a
        // silently-retained volatile value, and never a throw a future caller
        // could 500 on).
        Func<Task> write = () => _backend.PutVersionAsync(SecretId, 1, "byok-plaintext");
        await write.Should().ThrowAsync<InvalidOperationException>();

        var read = await _backend.GetVersionPlaintextAsync(SecretId, 1);
        read.Should().BeNull("nothing was ever persisted, so the version reads as absent");
    }

    [Test]
    public async Task GetVersion_ReturnsNull_AndNeverThrows()
    {
        // A fail-closed backend never persists anything, so every read is
        // absent → null, and it must NOT throw KeyNotFoundException.
        Func<Task<string?>> read = () => _backend.GetVersionPlaintextAsync(SecretId, 99);
        (await read.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Test]
    public async Task DeleteVersion_OnAbsentRow_IsNoOp()
    {
        Func<Task> act = () => _backend.DeleteVersionAsync(SecretId, 1);
        await act.Should().NotThrowAsync();
    }
}
