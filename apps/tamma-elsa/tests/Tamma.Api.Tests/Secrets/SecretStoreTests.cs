using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Unit tests for the Story 29-1 concrete <see cref="SecretStore"/>
/// facade. Exercised over the EF InMemory <see cref="SecretsDbContext"/>
/// provider + the <see cref="InMemorySecretStoreBackend"/> (the
/// facade-logic backend the task calls for — the other secret suites use
/// the same InMemory-EF pattern, no testcontainer).
///
/// <para>Pins the interface invariants:</para>
/// <list type="bullet">
///   <item><description>exactly ONE active version after a
///     create-with-plaintext;</description></item>
///   <item><description>rotation mints a PENDING successor + moves the
///     prior active version to RetiredGrace;</description></item>
///   <item><description>RetireVersion refuses the active version, then
///     scrubs + Revokes a non-active one;</description></item>
///   <item><description>plaintext is NEVER surfaced through the public
///     metadata / version signatures;</description></item>
///   <item><description>audit events fire for create / rotate /
///     retire.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class SecretStoreTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Owner = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private SecretsDbContextFactoryDouble _factory = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private RecordingSecretAccessAuditor _auditor = null!;
    private SecretStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecretsDbContextFactoryDouble(Guid.NewGuid().ToString());
        _backend = new InMemorySecretStoreBackend();
        _auditor = new RecordingSecretAccessAuditor();
        _store = new SecretStore(
            _factory, _backend, _auditor, TimeProvider.System,
            NullLogger<SecretStore>.Instance);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ── Create ───────────────────────────────────────────────────────

    [Test]
    public async Task Create_WithInitialPlaintext_MintsExactlyOneActiveVersion()
    {
        var meta = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));

        meta.ActiveVersionNumber.Should().Be(1);

        var versions = await VersionsAsync(meta.Id);
        versions.Should().HaveCount(1);
        versions[0].Status.Should().Be("active");
        versions.Count(v => v.Status == "active").Should().Be(1,
            "exactly one version is active after a create-with-plaintext");
    }

    [Test]
    public async Task Create_WithoutPlaintext_LeavesPlaceholderWithNoVersion()
    {
        var meta = await _store.CreateAsync(
            TenantCreate("db/app-role", initialPlaintext: null));

        meta.ActiveVersionNumber.Should().Be(0);
        (await VersionsAsync(meta.Id)).Should().BeEmpty();
    }

    [Test]
    public async Task Create_DuplicateRefWithinScope_Throws()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "value-one"));

        Func<Task> act = () => _store.CreateAsync(TenantCreate("db/app-role", "value-two"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Test]
    public async Task Create_SameNameDifferentTenant_IsAllowed()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "a", TenantA));
        Func<Task> act = () => _store.CreateAsync(TenantCreate("db/app-role", "b", TenantB));
        await act.Should().NotThrowAsync(
            "the (scope, tenantId, name) tuple is unique — two tenants may share a name");
    }

    [Test]
    public async Task Create_InvalidName_ThrowsArgumentException()
    {
        Func<Task> act = () => _store.CreateAsync(TenantCreate("Not A Slug", "value"));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task Create_EmitsWriteAudit()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));
        _auditor.Events.Should().ContainSingle(e =>
            e.EventType == SecretAuditEventTypes.Write
            && e.Outcome == SecretAuditOutcome.Success);
    }

    // ── Get / List ───────────────────────────────────────────────────

    [Test]
    public async Task Get_ByRef_ReturnsMetadata()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "value"));

        var fetched = await _store.GetAsync(SecretRef.ForTenant(TenantA, "db/app-role"));

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.ActiveVersionNumber.Should().Be(1);
    }

    [Test]
    public async Task Get_MissingRef_ReturnsNull()
    {
        var fetched = await _store.GetAsync(SecretRef.ForTenant(TenantA, "nope/missing"));
        fetched.Should().BeNull();
    }

    [Test]
    public async Task Get_CrossTenant_ReturnsNull()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "value", TenantA));

        var fetched = await _store.GetAsync(SecretRef.ForTenant(TenantB, "db/app-role"));
        fetched.Should().BeNull("a ref carrying a different tenant id never resolves");
    }

    [Test]
    public async Task List_FiltersByScopeAndTenant()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "v", TenantA));
        await _store.CreateAsync(TenantCreate("db/other", "v", TenantB));
        await _store.CreateAsync(PlatformCreate("platform/hmac", "v"));

        var tenantA = await _store.ListAsync(
            new SecretListFilter(SecretScope.Tenant, TenantA));
        tenantA.Should().ContainSingle().Which.Name.Should().Be("db/app-role");

        var platform = await _store.ListAsync(new SecretListFilter(SecretScope.Platform));
        platform.Should().ContainSingle().Which.Name.Should().Be("platform/hmac");
    }

    [Test]
    public async Task List_FiltersByNamePrefix()
    {
        await _store.CreateAsync(TenantCreate("db/app-role", "v"));
        await _store.CreateAsync(TenantCreate("api/key", "v"));

        var dbSecrets = await _store.ListAsync(
            new SecretListFilter(NamePrefix: "db/"));
        dbSecrets.Should().ContainSingle().Which.Name.Should().Be("db/app-role");
    }

    // ── Rotate ───────────────────────────────────────────────────────

    [Test]
    public async Task Rotate_MintsPendingSuccessor_AndRetiresPriorActiveToGrace()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));

        var rotated = await _store.RotateAsync(
            created.ToRef(), new RotateSecretRequest(NewPlaintext: "rotated-secret-value"));

        rotated.ActiveVersionNumber.Should().Be(2);
        rotated.LastRotatedAt.Should().NotBeNull();

        var versions = (await VersionsAsync(created.Id))
            .ToDictionary(v => v.VersionNumber, v => v.Status);

        versions[2].Should().Be("pending",
            "the successor is minted as pending — the saga's handler flips it to active");
        versions[1].Should().Be("retired_grace",
            "the prior active version moves into the grace window on rotation");
        versions.Values.Count(s => s == "active").Should().Be(0,
            "the facade never leaves two active versions; the successor awaits activation");
    }

    [Test]
    public async Task Rotate_WithGenerateLength_StoresGeneratedPlaintextInBackend()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));

        await _store.RotateAsync(
            created.ToRef(), new RotateSecretRequest(GenerateLength: 32));

        var generated = await _backend.GetVersionPlaintextAsync(created.Id, 2);
        generated.Should().NotBeNullOrEmpty(
            "the store generated a fresh value and persisted it via the backend");
    }

    [Test]
    public async Task Rotate_MissingSecret_ThrowsKeyNotFound()
    {
        Func<Task> act = () => _store.RotateAsync(
            SecretRef.ForTenant(TenantA, "nope/missing"),
            new RotateSecretRequest(NewPlaintext: "value"));
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [TestCase(true, true)]   // both supplied
    [TestCase(false, false)] // neither supplied
    public async Task Rotate_RequiresExactlyOnePlaintextSource(bool plaintext, bool generate)
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));

        var request = new RotateSecretRequest(
            NewPlaintext: plaintext ? "rotated-secret-value" : null,
            GenerateLength: generate ? 32 : null);

        Func<Task> act = () => _store.RotateAsync(created.ToRef(), request);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task Rotate_EmitsStartedAndSuccessAudit()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));
        _auditor.Events.Clear();

        await _store.RotateAsync(
            created.ToRef(), new RotateSecretRequest(NewPlaintext: "rotated-secret-value"));

        _auditor.Events.Should().Contain(e => e.EventType == SecretAuditEventTypes.RotateStarted);
        _auditor.Events.Should().Contain(e =>
            e.EventType == SecretAuditEventTypes.RotateSucceeded
            && e.Outcome == SecretAuditOutcome.Success);
    }

    // ── RetireVersion ────────────────────────────────────────────────

    [Test]
    public async Task Retire_ActiveVersion_Throws()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));

        Func<Task> act = () => _store.RetireVersionAsync(created.ToRef(), versionNumber: 1);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active version*");
    }

    [Test]
    public async Task Retire_NonActiveVersion_ScrubsAndRevokes()
    {
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));
        await _store.RotateAsync(
            created.ToRef(), new RotateSecretRequest(NewPlaintext: "rotated-secret-value"));

        // After rotate: ActiveVersionNumber = 2, v1 = retired_grace.
        // v1 is not the active pointer, so it can be retired.
        await _store.RetireVersionAsync(created.ToRef(), versionNumber: 1);

        var v1 = (await VersionsAsync(created.Id)).Single(v => v.VersionNumber == 1);
        v1.Status.Should().Be("revoked");
        v1.Ciphertext.Should().BeNull("the ciphertext row is scrubbed on revoke");

        (await _backend.GetVersionPlaintextAsync(created.Id, 1))
            .Should().BeNull("the backend bytes are scrubbed on retire");

        _auditor.Events.Should().Contain(e =>
            e.EventType == SecretAuditEventTypes.VersionRevoked
            && e.VersionNumber == 1);
    }

    [Test]
    public async Task Retire_MissingSecret_ThrowsKeyNotFound()
    {
        Func<Task> act = () => _store.RetireVersionAsync(
            SecretRef.ForTenant(TenantA, "nope/missing"), versionNumber: 1);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── plaintext-never-surfaced ─────────────────────────────────────

    [Test]
    public async Task PublicSurface_NeverReturnsPlaintext()
    {
        const string plaintext = "top-secret-never-surface-me";
        var created = await _store.CreateAsync(TenantCreate("db/app-role", plaintext));

        // The facade stored the plaintext (readable only via the backend
        // seam) but none of its public projections carry it.
        (await _backend.GetVersionPlaintextAsync(created.Id, 1)).Should().Be(plaintext);

        var meta = await _store.GetAsync(created.ToRef());
        var version = await _store.GetVersionAsync(created.ToRef(), 1);
        var list = await _store.ListVersionsAsync(created.ToRef());

        Serialize(created).Should().NotContain(plaintext);
        Serialize(meta).Should().NotContain(plaintext);
        Serialize(version).Should().NotContain(plaintext);
        Serialize(list).Should().NotContain(plaintext);
    }

    // ── full Create → Get → Rotate → RetireVersion sequence ──────────

    [Test]
    public async Task Sequence_Create_Get_Rotate_Retire_HonorsInvariants()
    {
        // Create → exactly one active version.
        var created = await _store.CreateAsync(TenantCreate("db/app-role", "initial-secret-value"));
        created.ActiveVersionNumber.Should().Be(1);
        (await VersionsAsync(created.Id)).Count(v => v.Status == "active").Should().Be(1);

        // Get → resolves the same row.
        var fetched = await _store.GetAsync(created.ToRef());
        fetched!.Id.Should().Be(created.Id);

        // Rotate → pending successor, prior active → grace.
        var rotated = await _store.RotateAsync(
            created.ToRef(), new RotateSecretRequest(NewPlaintext: "rotated-secret-value"));
        rotated.ActiveVersionNumber.Should().Be(2);
        var afterRotate = (await VersionsAsync(created.Id))
            .ToDictionary(v => v.VersionNumber, v => v.Status);
        afterRotate[2].Should().Be("pending");
        afterRotate[1].Should().Be("retired_grace");

        // Retire the graced predecessor → revoked + scrubbed.
        await _store.RetireVersionAsync(created.ToRef(), versionNumber: 1);
        var v1 = (await VersionsAsync(created.Id)).Single(v => v.VersionNumber == 1);
        v1.Status.Should().Be("revoked");
        v1.Ciphertext.Should().BeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static string Serialize(object? value) =>
        value is null ? "" : JsonSerializer.Serialize(value);

    private CreateSecretRequest TenantCreate(
        string name, string? initialPlaintext, Guid? tenantId = null) =>
        new(Name: name,
            Scope: SecretScope.Tenant,
            TenantId: tenantId ?? TenantA,
            Purpose: SecretPurpose.DbCredential,
            ConsumerRefs: Array.Empty<ConsumerRef>(),
            OwnerUserId: Owner,
            RotationSchedule: RotationSchedule.None,
            InitialPlaintext: initialPlaintext);

    private CreateSecretRequest PlatformCreate(string name, string? initialPlaintext) =>
        new(Name: name,
            Scope: SecretScope.Platform,
            TenantId: null,
            Purpose: SecretPurpose.HmacSharedSecret,
            ConsumerRefs: Array.Empty<ConsumerRef>(),
            OwnerUserId: Owner,
            RotationSchedule: RotationSchedule.None,
            InitialPlaintext: initialPlaintext);

    private async Task<List<SecretVersionRow>> VersionsAsync(Guid secretId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.SecretVersions
            .AsNoTracking()
            .Where(v => v.SecretId == secretId)
            .ToListAsync();
    }

    /// <summary>
    /// Test double for <see cref="IDbContextFactory{TContext}"/> that
    /// hands out EF InMemory <see cref="SecretsDbContext"/> instances over
    /// a single backing database name (mirrors the other secret suites).
    /// </summary>
    private sealed class SecretsDbContextFactoryDouble
        : IDbContextFactory<SecretsDbContext>, IDisposable
    {
        private readonly string _dbName;
        private SecretsDbContext? _trackingHandle;

        public SecretsDbContextFactoryDouble(string dbName)
        {
            _dbName = dbName;
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
}
