using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Integrations;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Postgres;
using Tamma.Api.Tests.TestDoubles;

namespace Tamma.Api.Tests.Integrations;

/// <summary>
/// Integration BYOK cabinet write/read round-trip over the REAL
/// <see cref="SecretStore"/> facade (set) + <see cref="CabinetTenantProviderKeyReader"/>
/// (read), on the EF-InMemory <see cref="SecretsDbContext"/> + in-memory backend
/// (the same fixture the other secret suites use).
///
/// <para>Pins: set mints an active v1 whose bundle the reader reads back; a second
/// tenant's same-named credential does NOT collide (the cabinet's tenant-scoped
/// unique key); set-twice for one tenant is a duplicate; remove drops it (reader →
/// null) and a later set can re-create the same slug.</para>
/// </summary>
[TestFixture]
public class IntegrationCredentialCabinetTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Owner = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private SecretsDbContextFactoryDouble _factory = null!;
    private InMemorySecretStoreBackend _backend = null!;
    private IntegrationCredentialCabinet _cabinet = null!;
    private CabinetTenantProviderKeyReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecretsDbContextFactoryDouble(Guid.NewGuid().ToString());
        _backend = new InMemorySecretStoreBackend();
        var store = new SecretStore(
            _factory, _backend, new RecordingSecretAccessAuditor(),
            TimeProvider.System, NullLogger<SecretStore>.Instance);
        _cabinet = new IntegrationCredentialCabinet(
            store, _factory, _backend, NullLogger<IntegrationCredentialCabinet>.Instance);
        _reader = new CabinetTenantProviderKeyReader(
            _factory, _backend, NullLogger<CabinetTenantProviderKeyReader>.Instance);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static string Bundle(string token) =>
        JiraCredentialCodec.Serialize(new JiraCredential("https://jira.example.com", "bot@example.com", token));

    [Test]
    public async Task Set_ThenReaderReadsBackBundle_AsActiveV1()
    {
        var meta = await _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a"), Owner);
        meta.ActiveVersionNumber.Should().Be(1);

        var row = await _reader.TryReadAsync(TenantA, IntegrationCabinetNames.JiraConfig);
        row.Should().NotBeNull();
        var cred = JiraCredentialCodec.TryDeserialize(row!.Plaintext);
        cred.Should().NotBeNull();
        cred!.ApiToken.Should().Be("fake-token-a");
    }

    [Test]
    public async Task TwoTenants_SameSlug_DoNotCollide()
    {
        await _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a"), Owner);
        await _cabinet.SetAsync(TenantB, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-b"), Owner);

        var a = await _reader.TryReadAsync(TenantA, IntegrationCabinetNames.JiraConfig);
        var b = await _reader.TryReadAsync(TenantB, IntegrationCabinetNames.JiraConfig);

        JiraCredentialCodec.TryDeserialize(a!.Plaintext)!.ApiToken.Should().Be("fake-token-a");
        JiraCredentialCodec.TryDeserialize(b!.Plaintext)!.ApiToken.Should().Be("fake-token-b");
    }

    [Test]
    public async Task Set_Twice_SameTenant_IsDuplicate()
    {
        await _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a"), Owner);

        var act = () => _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a2"), Owner);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Remove_DropsIt_ReaderReturnsNull_AndReSetSucceeds()
    {
        await _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a"), Owner);

        var removed = await _cabinet.RemoveAsync(TenantA, IntegrationCabinetNames.JiraConfig);
        removed.Should().BeTrue();

        (await _reader.TryReadAsync(TenantA, IntegrationCabinetNames.JiraConfig)).Should().BeNull();

        // A later set can cleanly re-create the same slug.
        var meta = await _cabinet.SetAsync(TenantA, IntegrationCabinetNames.JiraConfig, "jira", Bundle("fake-token-a3"), Owner);
        meta.ActiveVersionNumber.Should().Be(1);
        JiraCredentialCodec.TryDeserialize(
            (await _reader.TryReadAsync(TenantA, IntegrationCabinetNames.JiraConfig))!.Plaintext)!
            .ApiToken.Should().Be("fake-token-a3");
    }

    [Test]
    public async Task Remove_Missing_ReturnsFalse()
    {
        (await _cabinet.RemoveAsync(TenantA, IntegrationCabinetNames.JiraConfig)).Should().BeFalse();
    }

    /// <summary>
    /// EF-InMemory <see cref="SecretsDbContext"/> factory over one backing db name
    /// (mirrors the other secret suites — no testcontainer).
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

        public Task<SecretsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _trackingHandle?.Dispose();
            _trackingHandle = null;
        }
    }
}
