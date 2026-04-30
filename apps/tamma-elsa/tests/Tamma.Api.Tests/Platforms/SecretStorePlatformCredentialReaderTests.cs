using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Platforms;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Platforms;

/// <summary>
/// Story 31-2 + the Wave-B review's audit-bypass fix — pin the
/// behaviour of <see cref="SecretStorePlatformCredentialReader"/> as
/// the one production seam every webhook / dispatcher reaches when it
/// needs an installation credential. This is the highest-volume
/// secret-read path so its audit emission is the primary signal a
/// compliance reviewer relies on (Story 29-1 AC5 — every read
/// auditable).
///
/// <para>Two contracts are pinned here:</para>
/// <list type="number">
///   <item><description><b>Argument validation</b> — bad scope /
///         scope-tenant invariants must throw <see cref="ArgumentException"/>
///         BEFORE touching the DB or backend. Defensive against bad
///         installation rows feeding the resolver.</description></item>
///   <item><description><b>Audit emission on EVERY exit</b> — five
///         distinct outcomes (success, row_not_found, no_active_version,
///         version_plaintext_missing, version_scrubbed) each emit a
///         <see cref="SecretAuditEventTypes.Read"/> event. A regression
///         that silently drops emission on any branch would break the
///         compliance contract; encoding each branch as a test
///         prevents that.</description></item>
/// </list>
///
/// <para>Uses EF in-memory + <see cref="InMemorySecretStoreBackend"/> +
/// a recording auditor — no Postgres testcontainer needed; this is
/// purely behavioural pinning of the reader's own code paths.</para>
/// </summary>
[TestFixture]
public class SecretStorePlatformCredentialReaderTests
{
    private RecordingSecretAccessAuditor _auditor = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private DbContextOptions<SecretsDbContext> _options = null!;
    private TimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _auditor = new RecordingSecretAccessAuditor();
        _backend = new InMemorySecretStoreBackend();
        _time = TimeProvider.System;
        _options = new DbContextOptionsBuilder<SecretsDbContext>()
            .UseInMemoryDatabase($"reader-{Guid.NewGuid():N}")
            .Options;
    }

    private SecretStorePlatformCredentialReader CreateReader() =>
        new SecretStorePlatformCredentialReader(
            new SingleContextFactory(_options),
            _backend,
            _auditor,
            _time);

    private async Task SeedSecretAsync(
        Guid id, string scope, Guid? tenantId, string name,
        int activeVersion, string? plaintext)
    {
        await using var ctx = new SecretsDbContext(_options);
        ctx.Secrets.Add(new SecretRow
        {
            Id = id,
            Name = name,
            Scope = scope,
            TenantId = tenantId,
            Purpose = "generic",
            ActiveVersionNumber = activeVersion,
        });
        await ctx.SaveChangesAsync();

        if (plaintext is not null && activeVersion > 0)
        {
            await _backend.PutVersionAsync(id, activeVersion, plaintext);
        }
    }

    // ─── argument validation ────────────────────────────────────────────────

    [Test]
    public async Task ReadActivePlaintextAsync_TenantScope_NullTenantId_ThrowsArgumentException()
    {
        var reader = CreateReader();
        var act = async () => await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: null, name: "gh-token");
        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "tenantId");
        _auditor.Events.Should().BeEmpty(
            "scope-validation throws BEFORE the DB hit, so no audit yet");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_PlatformScope_NonNullTenantId_ThrowsArgumentException()
    {
        var reader = CreateReader();
        var act = async () => await reader.ReadActivePlaintextAsync(
            scope: "platform", tenantId: Guid.NewGuid(), name: "gh-app-key");
        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "tenantId");
        _auditor.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ReadActivePlaintextAsync_UnknownScope_ThrowsArgumentException()
    {
        var reader = CreateReader();
        var act = async () => await reader.ReadActivePlaintextAsync(
            scope: "system", tenantId: null, name: "gh-token");
        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "scope");
        _auditor.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ReadActivePlaintextAsync_WhitespaceScope_Throws()
    {
        var reader = CreateReader();
        var act = async () => await reader.ReadActivePlaintextAsync(
            scope: "   ", tenantId: null, name: "gh-token");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ReadActivePlaintextAsync_WhitespaceName_Throws()
    {
        var reader = CreateReader();
        var act = async () => await reader.ReadActivePlaintextAsync(
            scope: "platform", tenantId: null, name: "   ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ─── audit emission paths ───────────────────────────────────────────────

    [Test]
    public async Task ReadActivePlaintextAsync_HappyPath_EmitsReadSuccessAudit()
    {
        var tenantId = Guid.NewGuid();
        var secretId = Guid.NewGuid();
        await SeedSecretAsync(secretId, "tenant", tenantId, "gh-1001",
            activeVersion: 3, plaintext: "ghp_secret_value");

        var reader = CreateReader();
        var result = await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: tenantId, name: "gh-1001");

        result.Should().Be("ghp_secret_value");
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Success &&
            e.VersionNumber == 3 &&
            e.Detail == null &&
            e.Reference.TenantId == tenantId &&
            e.Reference.Name == "gh-1001" &&
            e.Reference.Scope == SecretScope.Tenant &&
            e.ActorUserId == Guid.Empty,
            "system-triggered reads use Guid.Empty as the actor sentinel");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_RowNotFound_ReturnsNullAndEmitsReadFailure()
    {
        var reader = CreateReader();
        var result = await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: Guid.NewGuid(), name: "missing");

        result.Should().BeNull();
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Failure &&
            e.VersionNumber == null &&
            e.Detail == "row_not_found");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_NoActiveVersion_ReturnsNullAndEmitsReadFailure()
    {
        var tenantId = Guid.NewGuid();
        await SeedSecretAsync(Guid.NewGuid(), "tenant", tenantId, "rotated-out",
            activeVersion: 0, plaintext: null);

        var reader = CreateReader();
        var result = await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: tenantId, name: "rotated-out");

        result.Should().BeNull();
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Failure &&
            e.Detail == "no_active_version");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_BackendThrowsKeyNotFound_ReturnsNullAndEmitsScrubbed()
    {
        var tenantId = Guid.NewGuid();
        var secretId = Guid.NewGuid();
        // Row says active version 7, but backend has no plaintext for
        // (id, 7) — simulates a scrubbed/expired version where the row
        // metadata wasn't updated.
        await SeedSecretAsync(secretId, "tenant", tenantId, "gh-2002",
            activeVersion: 7, plaintext: null);

        var reader = new SecretStorePlatformCredentialReader(
            new SingleContextFactory(_options),
            new ThrowingBackend(throwKeyNotFound: true),
            _auditor,
            _time);

        var result = await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: tenantId, name: "gh-2002");

        result.Should().BeNull();
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Failure &&
            e.VersionNumber == 7 &&
            e.Detail == "version_scrubbed");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_BackendReturnsNull_EmitsPlaintextMissing()
    {
        var tenantId = Guid.NewGuid();
        var secretId = Guid.NewGuid();
        await SeedSecretAsync(secretId, "tenant", tenantId, "gh-3003",
            activeVersion: 1, plaintext: null);

        var reader = new SecretStorePlatformCredentialReader(
            new SingleContextFactory(_options),
            new ThrowingBackend(returnNullPlaintext: true),
            _auditor,
            _time);

        var result = await reader.ReadActivePlaintextAsync(
            scope: "tenant", tenantId: tenantId, name: "gh-3003");

        result.Should().BeNull();
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Failure &&
            e.VersionNumber == 1 &&
            e.Detail == "version_plaintext_missing");
    }

    [Test]
    public async Task ReadActivePlaintextAsync_PlatformScope_NullTenantId_HappyPathSucceeds()
    {
        // The platform-scoped path mirrors tenant-scoped but with
        // tenantId=null — ensures the scope-validator's xor branch
        // doesn't accidentally reject the legitimate platform-scope
        // read.
        var secretId = Guid.NewGuid();
        await SeedSecretAsync(secretId, "platform", null, "platform-jwt",
            activeVersion: 1, plaintext: "platform-plaintext");

        var reader = CreateReader();
        var result = await reader.ReadActivePlaintextAsync(
            scope: "platform", tenantId: null, name: "platform-jwt");

        result.Should().Be("platform-plaintext");
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Read &&
            e.Outcome == SecretAuditOutcome.Success &&
            e.Reference.Scope == SecretScope.Platform &&
            e.Reference.TenantId == null);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private sealed class RecordingSecretAccessAuditor : ISecretAccessAuditor
    {
        public List<SecretAuditEvent> Events { get; } = new();
        public Task EmitAsync(SecretAuditEvent auditEvent, CancellationToken ct = default)
        {
            lock (Events) Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleContextFactory : IDbContextFactory<SecretsDbContext>
    {
        private readonly DbContextOptions<SecretsDbContext> _options;
        public SingleContextFactory(DbContextOptions<SecretsDbContext> options) => _options = options;
        public SecretsDbContext CreateDbContext() => new(_options);
        public Task<SecretsDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SecretsDbContext(_options));
    }

    /// <summary>
    /// Backend test double for the two error paths the in-memory
    /// backend can't naturally produce: an explicit
    /// <c>KeyNotFoundException</c> (version-scrubbed) and an explicit
    /// null plaintext return.
    /// </summary>
    private sealed class ThrowingBackend : ISecretStoreBackend
    {
        private readonly bool _throwKeyNotFound;
        private readonly bool _returnNullPlaintext;
        public ThrowingBackend(bool throwKeyNotFound = false, bool returnNullPlaintext = false)
        {
            _throwKeyNotFound = throwKeyNotFound;
            _returnNullPlaintext = returnNullPlaintext;
        }
        public Task PutVersionAsync(Guid secretId, int versionNumber, string plaintext, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetVersionPlaintextAsync(Guid secretId, int versionNumber, CancellationToken ct = default)
        {
            if (_throwKeyNotFound) throw new KeyNotFoundException();
            if (_returnNullPlaintext) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>("unused");
        }
        public Task DeleteVersionAsync(Guid secretId, int versionNumber, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
