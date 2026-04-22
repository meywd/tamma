using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="InMemorySecretStoreBackend"/>. The backend is
/// the placeholder shipped by Story 29-1 so subsequent stories
/// (rotation workflows, admin endpoints) can exercise the
/// <see cref="ISecretStoreBackend"/> contract without a Postgres
/// container until Story 29-2 lands the real driver.
///
/// <para>The test set is also the contract suite that the Story 29-2
/// Postgres backend must satisfy — anything that passes here should
/// pass against the real driver too.</para>
/// </summary>
[TestFixture]
public class InMemorySecretStoreBackendTests
{
    private InMemorySecretStoreBackend _backend = null!;
    private static readonly Guid SecretA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecretB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [SetUp]
    public void SetUp() => _backend = new InMemorySecretStoreBackend();

    [Test]
    public async Task PutAndGet_RoundTripsPlaintext()
    {
        await _backend.PutVersionAsync(SecretA, 1, "hunter2");
        var fetched = await _backend.GetVersionPlaintextAsync(SecretA, 1);
        fetched.Should().Be("hunter2");
    }

    [Test]
    public async Task Put_DifferentVersions_AreIsolated()
    {
        await _backend.PutVersionAsync(SecretA, 1, "v1");
        await _backend.PutVersionAsync(SecretA, 2, "v2");
        (await _backend.GetVersionPlaintextAsync(SecretA, 1)).Should().Be("v1");
        (await _backend.GetVersionPlaintextAsync(SecretA, 2)).Should().Be("v2");
    }

    [Test]
    public async Task Put_DifferentSecrets_AreIsolated()
    {
        await _backend.PutVersionAsync(SecretA, 1, "a-v1");
        await _backend.PutVersionAsync(SecretB, 1, "b-v1");
        (await _backend.GetVersionPlaintextAsync(SecretA, 1)).Should().Be("a-v1");
        (await _backend.GetVersionPlaintextAsync(SecretB, 1)).Should().Be("b-v1");
    }

    [Test]
    public async Task Put_SameKeyTwice_OverwritesValue()
    {
        // Backend rotation flips the existing row; not a real-world
        // mode (Story 29-2 inserts a fresh row per version) but the
        // contract is "last write wins" if a caller does this.
        await _backend.PutVersionAsync(SecretA, 1, "first");
        await _backend.PutVersionAsync(SecretA, 1, "second");
        (await _backend.GetVersionPlaintextAsync(SecretA, 1)).Should().Be("second");
    }

    [Test]
    public void Get_MissingRow_Throws()
    {
        Func<Task> act = () => _backend.GetVersionPlaintextAsync(SecretA, 999);
        act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Test]
    public async Task Delete_ScrubsButLeavesRow()
    {
        await _backend.PutVersionAsync(SecretA, 1, "secret");
        await _backend.DeleteVersionAsync(SecretA, 1);

        // Row still exists — Get returns null (not throws) — matches
        // the Story 29-2 contract: revoked rows stay in the table for
        // audit history, only the ciphertext is zeroed.
        var fetched = await _backend.GetVersionPlaintextAsync(SecretA, 1);
        fetched.Should().BeNull();
    }

    [Test]
    public async Task Delete_IsIdempotent()
    {
        await _backend.PutVersionAsync(SecretA, 1, "secret");
        await _backend.DeleteVersionAsync(SecretA, 1);
        Func<Task> second = () => _backend.DeleteVersionAsync(SecretA, 1);
        await second.Should().NotThrowAsync();
    }

    [Test]
    public async Task Delete_OnAbsentRow_LeavesPlaceholder()
    {
        // A scrub of a row that never existed creates a tombstone-ish
        // entry. The Postgres backend will likely no-op this case
        // (DELETE...WHERE returns 0 rows and is fine); the in-memory
        // contract is "Delete is always safe to call" which matches
        // the AddOrUpdate semantics. Get on the resulting row returns
        // null.
        await _backend.DeleteVersionAsync(SecretA, 1);
        var fetched = await _backend.GetVersionPlaintextAsync(SecretA, 1);
        fetched.Should().BeNull();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Put_RejectsNonPositiveVersion(int version)
    {
        Func<Task> act = () => _backend.PutVersionAsync(SecretA, version, "x");
        act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Put_RejectsNullPlaintext()
    {
        Func<Task> act = () => _backend.PutVersionAsync(SecretA, 1, null!);
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task Snapshot_ReturnsEveryEntry()
    {
        await _backend.PutVersionAsync(SecretA, 1, "a-v1");
        await _backend.PutVersionAsync(SecretA, 2, "a-v2");
        await _backend.PutVersionAsync(SecretB, 1, "b-v1");
        await _backend.DeleteVersionAsync(SecretB, 1);

        var snap = _backend.Snapshot();
        snap.Should().HaveCount(3);
        snap[(SecretA, 1)].Should().Be("a-v1");
        snap[(SecretA, 2)].Should().Be("a-v2");
        snap[(SecretB, 1)].Should().BeNull();
    }

    [Test]
    public async Task Clear_DropsEverything()
    {
        await _backend.PutVersionAsync(SecretA, 1, "x");
        _backend.Clear();
        _backend.Snapshot().Should().BeEmpty();
    }
}
