using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets.Postgres;

/// <summary>
/// Unit tests for <see cref="PostgresSecretStoreBackend"/> using the
/// EF InMemory provider. Pins the
/// <see cref="ISecretStoreBackend"/> contract that the production
/// driver shares with the in-memory placeholder shipped by Story
/// 29-1; full Postgres-flavoured behaviour (RLS, bytea round-trip,
/// CHECK constraints) is left to the integration suite when one
/// exists.
///
/// <para>InMemory caveat: byte arrays round-trip via reference, not
/// value. That's fine for these tests because the encrypt /decrypt
/// path runs OUTSIDE the EF tracking layer — the backend hands
/// already-encrypted bytes to the context.</para>
/// </summary>
[TestFixture]
public class PostgresSecretStoreBackendTests
{
    private static readonly Guid SecretA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecretB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const byte PrimaryKekId = 1;

    private SecretsDbContextFactoryDouble _contextFactory = null!;
    private TestKekProvider _kekProvider = null!;
    private PostgresSecretStoreBackend _backend = null!;

    [SetUp]
    public void SetUp()
    {
        _contextFactory = new SecretsDbContextFactoryDouble(
            Guid.NewGuid().ToString());
        var kek = RandomNumberGenerator.GetBytes(SecretEnvelope.KeySize);
        _kekProvider = new TestKekProvider(PrimaryKekId, kek);
        _backend = new PostgresSecretStoreBackend(
            _contextFactory,
            _kekProvider,
            NullLogger<PostgresSecretStoreBackend>.Instance);
    }

    [TearDown]
    public void TearDown() => _contextFactory.Dispose();

    // ── PutVersion / GetVersionPlaintext round-trip ──────────────────────────────

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
        await _backend.PutVersionAsync(SecretA, 1, "first");
        await _backend.PutVersionAsync(SecretA, 1, "second");
        (await _backend.GetVersionPlaintextAsync(SecretA, 1)).Should().Be("second");
    }

    [Test]
    public async Task Put_StoresEnvelopeNotPlaintext()
    {
        // Verify the stored bytes are the encrypted envelope, not the
        // raw plaintext. Pins the "plaintext never enters EF" contract.
        await _backend.PutVersionAsync(SecretA, 1, "hunter2");

        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var row = await ctx.SecretVersions
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync();

        row.Ciphertext.Should().NotBeNull();
        var asString = System.Text.Encoding.UTF8.GetString(row.Ciphertext!);
        asString.Should().NotContain("hunter2",
            "the stored bytes must be encrypted, not the raw plaintext");
        row.KekId.Should().Be(PrimaryKekId);
        row.FormatVersion.Should().Be(SecretEnvelope.CurrentFormatVersion);
        row.Status.Should().Be("pending");
    }

    [Test]
    public async Task Put_AssignsCreatedAtAndCreatedByUser()
    {
        await _backend.PutVersionAsync(SecretA, 1, "x");

        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var row = await ctx.SecretVersions
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync();

        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        row.CreatedByUserId.Should().Be(Guid.Empty,
            "system-actor row when no facade-supplied user id");
        row.ActivatedAt.Should().BeNull();
        row.RetiredAt.Should().BeNull();
    }

    [Test]
    public async Task Put_OverwriteRotatesEnvelopeBytes()
    {
        // Confirms a second Put on the same (secretId, versionNumber)
        // rotates the envelope bytes (fresh DEK + nonces) rather than
        // re-using the original ciphertext.
        await _backend.PutVersionAsync(SecretA, 1, "x");
        await using var ctxA = await _contextFactory.CreateDbContextAsync();
        var first = (await ctxA.SecretVersions.AsNoTracking()
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync()).Ciphertext;
        await ctxA.DisposeAsync();

        await _backend.PutVersionAsync(SecretA, 1, "x");
        await using var ctxB = await _contextFactory.CreateDbContextAsync();
        var second = (await ctxB.SecretVersions.AsNoTracking()
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync()).Ciphertext;

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotEqual(second,
            "fresh DEK + nonces means identical plaintext yields different envelope bytes");
    }

    // ── Get error paths ──────────────────────────────────────────────────────────

    [Test]
    public void Get_MissingRow_Throws()
    {
        Func<Task> act = () => _backend.GetVersionPlaintextAsync(SecretA, 999);
        act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Test]
    public async Task Get_AfterDelete_ReturnsNull()
    {
        await _backend.PutVersionAsync(SecretA, 1, "secret");
        await _backend.DeleteVersionAsync(SecretA, 1);

        // Row still exists — Get returns null (not throws) — matches
        // the in-memory backend contract.
        var fetched = await _backend.GetVersionPlaintextAsync(SecretA, 1);
        fetched.Should().BeNull();
    }

    // ── Delete semantics ────────────────────────────────────────────────────────

    [Test]
    public async Task Delete_ScrubsCiphertextAndFlipsStatus()
    {
        await _backend.PutVersionAsync(SecretA, 1, "secret");
        await _backend.DeleteVersionAsync(SecretA, 1);

        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var row = await ctx.SecretVersions
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync();

        row.Ciphertext.Should().BeNull("scrubbed");
        row.Status.Should().Be("revoked");
        row.RetiredAt.Should().NotBeNull();
        row.RetiredAt!.Value.Should()
            .BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
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
    public async Task Delete_OnAbsentRow_IsNoOp()
    {
        // The Postgres backend treats "delete a row that never
        // existed" as a no-op — matches the in-memory contract's
        // "Delete is always safe to call". A subsequent Get sees
        // KeyNotFound (not null, because no row ever existed).
        await _backend.DeleteVersionAsync(SecretA, 1);
        Func<Task> get = () => _backend.GetVersionPlaintextAsync(SecretA, 1);
        await get.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── Argument validation ─────────────────────────────────────────────────────

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

    // ── KEK behaviour ───────────────────────────────────────────────────────────

    [Test]
    public async Task Put_RecordsPrimaryKekIdOnRow()
    {
        await _backend.PutVersionAsync(SecretA, 1, "x");
        await using var ctx = await _contextFactory.CreateDbContextAsync();
        var row = await ctx.SecretVersions
            .Where(v => v.SecretId == SecretA && v.VersionNumber == 1)
            .FirstAsync();
        row.KekId.Should().Be(PrimaryKekId,
            "the row's KekId column lets a rewrap pass filter without trial-decrypt");
    }

    [Test]
    public async Task Get_FailsWhenKekProviderHasWrongKey()
    {
        // Stage: write under one KEK, then re-instantiate the backend
        // with a provider that returns a DIFFERENT 32-byte key for
        // the same slot id. AES-GCM tag check must fail on read.
        await _backend.PutVersionAsync(SecretA, 1, "secret");

        var differentKek = RandomNumberGenerator.GetBytes(SecretEnvelope.KeySize);
        var wrongProvider = new TestKekProvider(PrimaryKekId, differentKek);
        var backendWithWrongKek = new PostgresSecretStoreBackend(
            _contextFactory,
            wrongProvider,
            NullLogger<PostgresSecretStoreBackend>.Instance);

        Func<Task> act = () => backendWithWrongKek.GetVersionPlaintextAsync(SecretA, 1);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test double for <see cref="IDbContextFactory{TContext}"/> that
    /// hands out InMemory <see cref="SecretsDbContext"/> instances
    /// against a single backing database name.
    /// </summary>
    private sealed class SecretsDbContextFactoryDouble
        : IDbContextFactory<SecretsDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretsDbContext? _trackingHandle;

        public SecretsDbContextFactoryDouble(string dbName)
        {
            _dbName = dbName;
            // Ensure the InMemory database is materialised once so
            // subsequent contexts see the same backing store.
            _trackingHandle = CreateDbContext();
            _trackingHandle.Database.EnsureCreated();
        }

        public SecretsDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SecretsDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics
                        .InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SecretsDbContext(options);
        }

        public Task<SecretsDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _trackingHandle?.Dispose();
            _trackingHandle = null;
        }
    }

    /// <summary>
    /// Minimal <see cref="IKekProvider"/> for tests — single slot id,
    /// single key.
    /// </summary>
    private sealed class TestKekProvider : IKekProvider
    {
        private readonly byte _slot;
        private readonly byte[] _key;

        public TestKekProvider(byte slot, byte[] key)
        {
            _slot = slot;
            _key = key;
        }

        public byte PrimaryKekId => _slot;

        public byte[] GetKek(byte kekId)
        {
            if (kekId != _slot) throw new KekNotAvailableException(kekId);
            return (byte[])_key.Clone();
        }

        public bool TryGetKek(byte kekId, out byte[]? key)
        {
            if (kekId != _slot)
            {
                key = null;
                return false;
            }
            key = (byte[])_key.Clone();
            return true;
        }
    }
}
