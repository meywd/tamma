using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Services.Secrets.Reveal;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets.Reveal;

/// <summary>
/// Unit tests for <see cref="SecretRevealService"/> using the
/// <see cref="InMemorySecretStoreBackend"/> and EF InMemory contexts.
/// Pins the reveal-once contract:
///
/// <list type="bullet">
///   <item><description>Tokens are 256-bit, base64url-encoded, unique
///     per issue.</description></item>
///   <item><description>First consume returns plaintext + emits
///     <c>SECRET.REVEAL</c>.</description></item>
///   <item><description>Second consume returns
///     <see cref="RevealTokenConsumeOutcome.AlreadyConsumed"/> (410
///     Gone).</description></item>
///   <item><description>Expired token returns
///     <see cref="RevealTokenConsumeOutcome.Expired"/>.</description></item>
///   <item><description>Rotation issues a fresh token; old version
///     is not revealable via a new reveal request.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SecretRevealServiceTests
{
    private const byte PrimaryKekId = 7;
    private static readonly Guid OwnerUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private TestKekProvider _kekProvider = null!;
    private RecordingAuditor _auditor = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private RevealDbFactoryDouble _revealFactory = null!;
    private SecretsDbFactoryDouble _secretsFactory = null!;
    private FakeClockProvider _time = null!;
    private SecretRevealService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var kek = RandomNumberGenerator.GetBytes(32);
        _kekProvider = new TestKekProvider(PrimaryKekId, kek);
        _auditor = new RecordingAuditor();
        _backend = new InMemorySecretStoreBackend();
        _revealFactory = new RevealDbFactoryDouble(Guid.NewGuid().ToString());
        _secretsFactory = new SecretsDbFactoryDouble(Guid.NewGuid().ToString());
        _time = new FakeClockProvider(new DateTimeOffset(
            2026, 04, 23, 10, 00, 00, TimeSpan.Zero));
        _service = new SecretRevealService(
            _revealFactory, _secretsFactory, _backend,
            _auditor, _kekProvider, _time,
            NullLogger<SecretRevealService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _revealFactory.Dispose();
        _secretsFactory.Dispose();
    }

    // ── Token format ─────────────────────────────────────────────────

    [Test]
    public async Task IssueCreate_ReturnsBase64UrlToken_43Chars()
    {
        var result = await IssueCreate("db/app-role", "hunter2secret");

        result.RevealToken.Should().NotBeNullOrWhiteSpace();
        // 32 bytes base64url = 43 chars (no padding).
        result.RevealToken.Length.Should().Be(43);
        // base64url alphabet only.
        result.RevealToken.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Test]
    public async Task IssueCreate_SetsExpiry60SecondsFromNow()
    {
        var result = await IssueCreate("db/app-role", "hunter2secret");

        result.ExpiresAt.Should().Be(_time.GetUtcNow().AddSeconds(60));
    }

    [Test]
    public async Task IssueCreate_EachCallReturnsUniqueToken()
    {
        var a = await IssueCreate("db/role-a", "plaintext-a-value");
        var b = await IssueCreate("db/role-b", "plaintext-b-value");

        a.RevealToken.Should().NotBe(b.RevealToken);
    }

    // ── Consume — success path ──────────────────────────────────────

    [Test]
    public async Task Consume_FirstCall_ReturnsPlaintextOnce()
    {
        var issued = await IssueCreate("db/app-role", "hunter2secret");

        var consumed = await _service.ConsumeAsync(
            issued.RevealToken,
            new RevealCallerContext(UserAgent: "test-ua", RemoteIp: "127.0.0.1"));

        consumed.Outcome.Should().Be(RevealTokenConsumeOutcome.Success);
        consumed.Plaintext.Should().Be("hunter2secret");
        consumed.SecretId.Should().Be(issued.Metadata.Id);
        consumed.VersionNumber.Should().Be(1);
        consumed.SecretName.Should().Be("db/app-role");
    }

    [Test]
    public async Task Consume_FirstCall_EmitsSecretRevealAudit()
    {
        var issued = await IssueCreate("db/app-role", "hunter2secret");

        await _service.ConsumeAsync(
            issued.RevealToken,
            new RevealCallerContext(UserAgent: "ua", RemoteIp: "10.0.0.1"));

        _auditor.Events.Should().ContainSingle(
            e => e.EventType == SecretAuditEventTypes.Reveal
                && e.Outcome == SecretAuditOutcome.Success
                && e.VersionNumber == 1
                && e.ActorUserId == OwnerUserId);
    }

    // ── Consume — idempotence / already-consumed ─────────────────────

    [Test]
    public async Task Consume_SecondCall_ReturnsAlreadyConsumed()
    {
        var issued = await IssueCreate("db/app-role", "hunter2secret");
        var caller = new RevealCallerContext("ua", "127.0.0.1");

        var first = await _service.ConsumeAsync(issued.RevealToken, caller);
        var second = await _service.ConsumeAsync(issued.RevealToken, caller);

        first.Outcome.Should().Be(RevealTokenConsumeOutcome.Success);
        second.Outcome.Should().Be(RevealTokenConsumeOutcome.AlreadyConsumed);
        second.Plaintext.Should().BeNull();
    }

    [Test]
    public async Task Consume_SecondCall_DoesNotEmitSecondRevealAudit()
    {
        var issued = await IssueCreate("db/app-role", "hunter2secret");
        var caller = new RevealCallerContext("ua", "127.0.0.1");

        await _service.ConsumeAsync(issued.RevealToken, caller);
        var revealCountAfterFirst = _auditor.Events
            .Count(e => e.EventType == SecretAuditEventTypes.Reveal);

        await _service.ConsumeAsync(issued.RevealToken, caller);
        var revealCountAfterSecond = _auditor.Events
            .Count(e => e.EventType == SecretAuditEventTypes.Reveal);

        revealCountAfterFirst.Should().Be(1);
        revealCountAfterSecond.Should().Be(1, "second consume must not emit a second audit event (AC4)");
    }

    // ── Consume — expired ────────────────────────────────────────────

    [Test]
    public async Task Consume_AfterTtl_ReturnsExpired()
    {
        var issued = await IssueCreate("db/app-role", "hunter2secret");
        _time.Advance(TimeSpan.FromSeconds(61));

        var consumed = await _service.ConsumeAsync(
            issued.RevealToken,
            new RevealCallerContext("ua", "127.0.0.1"));

        consumed.Outcome.Should().Be(RevealTokenConsumeOutcome.Expired);
        consumed.Plaintext.Should().BeNull();
    }

    [Test]
    public async Task Consume_UnknownToken_ReturnsNotFound()
    {
        var fakeToken = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var consumed = await _service.ConsumeAsync(
            fakeToken,
            new RevealCallerContext("ua", "127.0.0.1"));

        consumed.Outcome.Should().Be(RevealTokenConsumeOutcome.NotFound);
    }

    [Test]
    public async Task Consume_MalformedToken_ReturnsNotFound()
    {
        var consumed = await _service.ConsumeAsync(
            "@@@invalid!!!",
            new RevealCallerContext("ua", "127.0.0.1"));

        consumed.Outcome.Should().Be(RevealTokenConsumeOutcome.NotFound);
    }

    // ── Rotation ─────────────────────────────────────────────────────

    [Test]
    public async Task Rotate_IssuesNewTokenForNewVersion()
    {
        var v1 = await IssueCreate("db/app-role", "hunter2secret");
        await _service.ConsumeAsync(
            v1.RevealToken, new RevealCallerContext("ua", "127.0.0.1"));

        var v2 = await _service.IssueRotateAsync(
            v1.Metadata.Id, "rotated-pw-v2", OwnerUserId);

        v2.RevealToken.Should().NotBe(v1.RevealToken);
        v2.Metadata.ActiveVersionNumber.Should().Be(2);

        var reveal = await _service.ConsumeAsync(
            v2.RevealToken, new RevealCallerContext("ua", "127.0.0.1"));

        reveal.Outcome.Should().Be(RevealTokenConsumeOutcome.Success);
        reveal.Plaintext.Should().Be("rotated-pw-v2");
        reveal.VersionNumber.Should().Be(2);
    }

    [Test]
    public async Task Rotate_OldTokenStillWorksOnceIfNotConsumed()
    {
        // AC6 clarification: old VERSIONS are not revealable via a new
        // token. But if the original create-token is still unconsumed,
        // it remains valid for the still-alive version. Documenting
        // this explicitly prevents a regression if the rotation flow
        // ever flips to invalidating outstanding tokens (which would
        // itself be a breaking change).
        var v1 = await IssueCreate("db/app-role", "original-value");

        await _service.IssueRotateAsync(
            v1.Metadata.Id, "rotated-value", OwnerUserId);

        // Old token still unconsumed — it burns once against version 1.
        var consumed = await _service.ConsumeAsync(
            v1.RevealToken, new RevealCallerContext("ua", "127.0.0.1"));
        consumed.Outcome.Should().Be(RevealTokenConsumeOutcome.Success);
        consumed.VersionNumber.Should().Be(1);
    }

    [Test]
    public async Task Rotate_EmitsStartedAndSucceededAuditEvents()
    {
        var v1 = await IssueCreate("db/app-role", "first-value");
        _auditor.Events.Clear();

        await _service.IssueRotateAsync(
            v1.Metadata.Id, "second-value", OwnerUserId);

        _auditor.Events.Should().Contain(
            e => e.EventType == SecretAuditEventTypes.RotateStarted);
        _auditor.Events.Should().Contain(
            e => e.EventType == SecretAuditEventTypes.RotateSucceeded);
    }

    [Test]
    public void Rotate_UnknownSecret_Throws()
    {
        var unknown = Guid.NewGuid();
        Func<Task> act = () => _service.IssueRotateAsync(
            unknown, "new-value", OwnerUserId);

        act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── Sweep ────────────────────────────────────────────────────────

    [Test]
    public async Task SweepExpired_FlipsExpiredRowsToExpiredStatus()
    {
        var issued = await IssueCreate("db/app-role", "plaintext-value");
        _time.Advance(TimeSpan.FromSeconds(61));

        var flipped = await _service.SweepExpiredAsync();

        flipped.Should().Be(1);

        await using var ctx = await _revealFactory.CreateDbContextAsync();
        var row = await ctx.RevealTokens.FirstAsync();
        row.Status.Should().Be("expired");
    }

    [Test]
    public async Task SweepExpired_LeavesUnexpiredRowsAlone()
    {
        await IssueCreate("db/app-role", "plaintext-value");
        // Only 10 seconds elapsed — not yet expired.
        _time.Advance(TimeSpan.FromSeconds(10));

        var flipped = await _service.SweepExpiredAsync();

        flipped.Should().Be(0);
    }

    [Test]
    public async Task SweepExpired_IsIdempotent()
    {
        await IssueCreate("db/app-role", "plaintext-value");
        _time.Advance(TimeSpan.FromSeconds(61));

        var first = await _service.SweepExpiredAsync();
        var second = await _service.SweepExpiredAsync();

        first.Should().Be(1);
        second.Should().Be(0, "second sweep finds no unused rows to flip");
    }

    // ── HMAC / KEK ───────────────────────────────────────────────────

    [Test]
    public async Task TokenHash_IsStoredNotPlaintext()
    {
        var issued = await IssueCreate("db/app-role", "plaintext-value");

        await using var ctx = await _revealFactory.CreateDbContextAsync();
        var row = await ctx.RevealTokens.FirstAsync();
        row.TokenHash.Should().NotBeNullOrEmpty();
        row.TokenHash.Length.Should().Be(32); // HMAC-SHA256 output size
        // Storage does NOT contain the raw token value.
        var asString = System.Text.Encoding.UTF8.GetString(row.TokenHash);
        asString.Should().NotContain(issued.RevealToken);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<RevealTokenIssueResult> IssueCreate(
        string name, string plaintext) =>
        await _service.IssueCreateAsync(
            name: name,
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            initialPlaintext: plaintext,
            consumerRefs: null,
            ownerUserId: OwnerUserId,
            rotationSchedule: null);

    // ─── Test doubles ────────────────────────────────────────────────

    private sealed class FakeClockProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClockProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class RecordingAuditor : ISecretAccessAuditor
    {
        public List<SecretAuditEvent> Events { get; } = new();

        public Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

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
            if (kekId != _slot) { key = null; return false; }
            key = (byte[])_key.Clone();
            return true;
        }
    }

    private sealed class RevealDbFactoryDouble
        : IDbContextFactory<SecretRevealDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretRevealDbContext? _tracking;

        public RevealDbFactoryDouble(string dbName)
        {
            _dbName = dbName;
            _tracking = CreateDbContext();
            _tracking.Database.EnsureCreated();
        }

        public SecretRevealDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SecretRevealDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics
                        .InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SecretRevealDbContext(options);
        }

        public Task<SecretRevealDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _tracking?.Dispose();
            _tracking = null;
        }
    }

    private sealed class SecretsDbFactoryDouble
        : IDbContextFactory<SecretsDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretsDbContext? _tracking;

        public SecretsDbFactoryDouble(string dbName)
        {
            _dbName = dbName;
            _tracking = CreateDbContext();
            _tracking.Database.EnsureCreated();
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
            _tracking?.Dispose();
            _tracking = null;
        }
    }
}
