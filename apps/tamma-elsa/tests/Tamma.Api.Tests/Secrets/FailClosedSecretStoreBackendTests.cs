using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="FailClosedSecretStoreBackend"/> — the guard
/// registered when a real secret-store database is configured but the
/// envelope KEK is missing. The contract: WRITES fail loud (never persist
/// plaintext to volatile memory); reads / deletes degrade to "absent" so
/// ambient BYOK probes fall through to the platform path instead of
/// crashing.
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
        // therefore reports the version as absent (KeyNotFound), never a
        // silently-retained volatile value.
        Func<Task> write = () => _backend.PutVersionAsync(SecretId, 1, "byok-plaintext");
        await write.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> read = () => _backend.GetVersionPlaintextAsync(SecretId, 1);
        await read.Should().ThrowAsync<KeyNotFoundException>(
            "nothing was ever persisted, so the version row is absent");
    }

    [Test]
    public async Task DeleteVersion_OnAbsentRow_IsNoOp()
    {
        Func<Task> act = () => _backend.DeleteVersionAsync(SecretId, 1);
        await act.Should().NotThrowAsync();
    }
}
