using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Pins the rotation grace window at the QUERY, not just at the handler's revocation
/// check. <c>RotateAsync</c> stamps the outgoing key with <c>RevokedAt = now + 24h</c> so
/// dependent services can roll over without an outage — the <c>TAMMA_API_KEY</c> sitting
/// in every customer repo's Actions secrets is exactly that case, and it is re-provisioned
/// on rotate, so running workflows must not break in between.
///
/// <para>That window was unreachable in practice. The authentication candidate scan listed
/// keys with <c>ListByScopeAsync</c>, which filters <c>RevokedAt == null</c>, so a rotated
/// key disappeared from the only lookup path a used key has left: its first successful auth
/// rehashes the row to per-key-salted Argon2, after which no hash-equality lookup can find
/// it and the prefix scan is all that remains. The handler's grace branch had nothing to
/// act on and the old key 401'd immediately.</para>
/// </summary>
[TestFixture]
public class ApiKeyRepositoryGraceWindowTests
{
    private ControlPlaneDbContext _cp = null!;
    private ApiKeyRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _cp = new ControlPlaneDbContext(options);
        _repo = new ApiKeyRepository(_cp);
    }

    [TearDown]
    public void TearDown() => _cp.Dispose();

    private async Task<ApiKey> SeedAsync(string label, DateTime? revokedAt)
    {
        var key = await _repo.CreateAsync(new ApiKey
        {
            Scope = "installation",
            OwnerId = Guid.NewGuid().ToString(),
            KeyHash = $"hash-{label}",
            KeyPrefix = $"prefix-{label}",
            Label = label,
            Permissions = Array.Empty<string>(),
            CreatedAt = DateTime.UtcNow,
        });

        if (revokedAt is not null)
        {
            key.RevokedAt = revokedAt;
            await _cp.SaveChangesAsync();
        }

        return key;
    }

    [Test]
    public async Task ListValidByScope_IncludesAKeyInsideItsRotationGraceWindow()
    {
        await SeedAsync("live", revokedAt: null);
        await SeedAsync("rotating", revokedAt: DateTime.UtcNow.AddHours(24));
        await SeedAsync("revoked", revokedAt: DateTime.UtcNow.AddHours(-1));

        var valid = await _repo.ListValidByScopeAsync("installation");

        valid.Select(k => k.Label).Should().BeEquivalentTo(new[] { "live", "rotating" },
            "a key whose RevokedAt is still in the future is inside its grace window and "
            + "must remain a candidate; one whose moment has passed must not");
    }

    [Test]
    public async Task ListByScope_StillExcludesEveryRevokedKey()
    {
        // Deliberately unchanged: this method also backs the admin key listing, and
        // widening it would quietly change what operators see.
        await SeedAsync("live", revokedAt: null);
        await SeedAsync("rotating", revokedAt: DateTime.UtcNow.AddHours(24));

        var listed = await _repo.ListByScopeAsync("installation");

        listed.Select(k => k.Label).Should().BeEquivalentTo(new[] { "live" });
    }
}
