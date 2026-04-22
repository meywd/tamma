using Tamma.Data.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tamma.Data;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Lightweight helper that constructs an in-memory <see cref="ControlPlaneDbContext"/>
/// plus a matching <see cref="ITenantDbContextFactory"/> backed by the same
/// EF-InMemory database name. The factory-issued tenant contexts see the
/// same rows as the CP context so mixed-plane repos under test behave
/// deterministically.
/// </summary>
public sealed class InMemoryDbFixture : IAsyncDisposable
{
    public TestControlPlaneDbContext Cp { get; }
    public ITenantDbContextFactory Factory { get; }
    public string DbName { get; }
    public DbContextOptions<ControlPlaneDbContext> CpOptions { get; }
    public DbContextOptions<TenantDbContext> TenantOptions { get; }

    public InMemoryDbFixture(string? dbName = null)
    {
        DbName = dbName ?? Guid.NewGuid().ToString();

        CpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(DbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        TenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(DbName)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Cp = new TestControlPlaneDbContext(CpOptions);
        Factory = new TestTenantDbContextFactory(TenantOptions);
    }

    public TestControlPlaneDbContext NewCpContext() => new(CpOptions);

    public async ValueTask DisposeAsync()
    {
        await Cp.DisposeAsync();
    }
}
