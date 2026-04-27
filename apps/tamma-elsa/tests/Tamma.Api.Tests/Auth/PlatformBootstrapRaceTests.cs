using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Email;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-R2 / PF-S9 — pin the bootstrap-superadmin race fix.
///
/// <para>Pre-fix behaviour: two concurrent first-user registrations
/// both observed <c>existingUserCount == 0</c> and both received
/// <c>platform_admin</c>. The pre-fix comment dismissed this as
/// "two platform admins, which is fine"; the round-2 review caught
/// it as a privilege-escalation risk against a freshly-deployed
/// instance.</para>
///
/// <para>Post-fix: the schema's <c>platform_bootstrap</c> table has
/// a unique PK + CHECK (Id = 1) constraint. Concurrent registrations
/// race for exactly one sentinel row; the loser silently stays at the
/// default <c>"user"</c> platform role.</para>
/// </summary>
[TestFixture]
public class PlatformBootstrapRaceTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private static HttpClient CreateRegisteringClient()
    {
        // The shared factory's default Email config is empty; the
        // outbox sender requires Email:From to be set, otherwise the
        // verification email blows up the registration flow. We swap
        // in an in-memory email service via DI.
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dashboard:Url"] = "https://dash.test.tamma.dev",
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddEmailServices();
                services.AddSingleton<IEmailService>(new InMemoryEmailService());
            });
        }).CreateClient();
    }

    [Test]
    public async Task TryClaim_FirstCall_ReturnsTrue()
    {
        var userId = await SeedUserAsync("first@example.com");
        await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IPlatformBootstrapRepository>();

        var won = await repo.TryClaimAsync(userId);

        won.Should().BeTrue();
        var hasClaim = await repo.HasBeenClaimedAsync();
        hasClaim.Should().BeTrue();
    }

    [Test]
    public async Task TryClaim_SecondCall_ReturnsFalse()
    {
        var firstUser = await SeedUserAsync("first@example.com");
        var secondUser = await SeedUserAsync("second@example.com");

        await using var scope1 = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var repo1 = scope1.ServiceProvider
            .GetRequiredService<IPlatformBootstrapRepository>();
        await repo1.TryClaimAsync(firstUser);

        await using var scope2 = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var repo2 = scope2.ServiceProvider
            .GetRequiredService<IPlatformBootstrapRepository>();
        var second = await repo2.TryClaimAsync(secondUser);

        second.Should().BeFalse(
            "the schema's CHECK (Id = 1) + unique PK reject every claim after the first");
    }

    [Test]
    public async Task TryClaim_ConcurrentClaimants_ExactlyOneWins()
    {
        // PF-S9 — the actual race. We fire N parallel claims via
        // Task.WhenAll, each in its own scope (i.e. its own
        // ControlPlaneDbContext + transaction), and assert that
        // exactly one returns true.
        const int Concurrency = 8;
        var users = new List<Guid>();
        for (var i = 0; i < Concurrency; i++)
        {
            users.Add(await SeedUserAsync($"race-{i}@example.com"));
        }

        var tasks = users.Select(async uid =>
        {
            await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
            var repo = scope.ServiceProvider
                .GetRequiredService<IPlatformBootstrapRepository>();
            return await repo.TryClaimAsync(uid);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1,
            "exactly one concurrent claimant must win — the unique PK + CHECK constraint forbids more");
        results.Count(r => !r).Should().Be(Concurrency - 1,
            "every loser silently stays at the default user role");
    }

    [Test]
    public async Task Register_FirstUser_PromotedToPlatformAdmin()
    {
        // End-to-end check via the HTTP register endpoint. The first
        // call results in a user whose platform_role = 'platform_admin'.
        using var client = CreateRegisteringClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "first-admin@example.com",
            password = "Sup3rSecure!",
            displayName = "Bootstrap Admin",
        });
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());

        await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == "first-admin@example.com");
        user.Should().NotBeNull();
        user!.PlatformRole.Should().Be("platform_admin",
            "the first registered user wins the platform_bootstrap claim and is promoted");
    }

    [Test]
    public async Task Register_SecondUser_StaysAsRegularUser()
    {
        using var client = CreateRegisteringClient();

        var first = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "first@example.com",
            password = "Sup3rSecure!",
            displayName = "First",
        });
        first.IsSuccessStatusCode.Should().BeTrue();

        var second = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "second@example.com",
            password = "Sup3rSecure!",
            displayName = "Second",
        });
        second.IsSuccessStatusCode.Should().BeTrue();

        await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var firstUser = await db.Users
            .FirstOrDefaultAsync(u => u.Email == "first@example.com");
        var secondUser = await db.Users
            .FirstOrDefaultAsync(u => u.Email == "second@example.com");
        firstUser!.PlatformRole.Should().Be("platform_admin");
        secondUser!.PlatformRole.Should().Be("user",
            "the bootstrap sentinel was already claimed; subsequent users default to 'user'");
    }

    [Test]
    public async Task Register_ConcurrentFirstUsers_ExactlyOnePlatformAdmin()
    {
        // The end-to-end PF-S9 regression case: hammer the registration
        // endpoint with N concurrent first-user requests. Exactly one
        // should land as platform_admin in the resulting users table.
        const int Concurrency = 5;
        using var client = CreateRegisteringClient();

        var registrationTasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < Concurrency; i++)
        {
            var idx = i;
            registrationTasks.Add(client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                email = $"concurrent-{idx}@example.com",
                password = "Sup3rSecure!",
                displayName = $"Concurrent{idx}",
            }));
        }
        var responses = await Task.WhenAll(registrationTasks);
        responses.All(r => r.IsSuccessStatusCode).Should().BeTrue(
            "every registration is independent — no email collision");

        await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var admins = await db.Users
            .Where(u => u.PlatformRole == "platform_admin")
            .CountAsync();
        admins.Should().Be(1,
            "PF-S9 — concurrent first-user registrations must produce EXACTLY one platform_admin, "
            + "regardless of how many race for the bootstrap sentinel");

        var bootstrap = await db.PlatformBootstraps.SingleAsync();
        bootstrap.Id.Should().Be(PlatformBootstrap.SentinelId);
    }

    /// <summary>
    /// Helper — insert a user directly via DI'd repository so the
    /// race tests have valid user ids to claim with.
    /// </summary>
    private static async Task<Guid> SeedUserAsync(string email)
    {
        await using var scope = ApiTestFixture.Factory.Services.CreateAsyncScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
            PlatformRole = "user",
        });
        return user.Id;
    }
}
