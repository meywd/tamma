using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Stopgap;

namespace Tamma.Api.Tests.Secrets.Stopgap;

/// <summary>
/// Tests for the <see cref="MigrateSecretsCommand"/> CLI dispatcher
/// (Story 29-9 AC1).
/// </summary>
[TestFixture]
public class MigrateSecretsCommandTests
{
    [Test]
    public void ShouldRun_ReturnsTrueForMigrateSecretsArg()
    {
        MigrateSecretsCommand.ShouldRun(new[] { "migrate-secrets" })
            .Should().BeTrue();
    }

    [Test]
    public void ShouldRun_IsCaseInsensitive()
    {
        MigrateSecretsCommand.ShouldRun(new[] { "Migrate-Secrets" })
            .Should().BeTrue();
    }

    [Test]
    public void ShouldRun_ReturnsFalseForDifferentArgs()
    {
        MigrateSecretsCommand.ShouldRun(new[] { "serve" })
            .Should().BeFalse();
        MigrateSecretsCommand.ShouldRun(Array.Empty<string>())
            .Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_UsesInjectedMigrator_AndReturnsZeroOnSuccess()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStopgapSecretMigrator, StubMigrator>(_ =>
            new StubMigrator(new StopgapMigrationReport(
                Results: new[]
                {
                    new StopgapMigrationResult(
                        "anthropic/api-key",
                        StopgapMigrationOutcome.Imported,
                        "Anthropic:ApiKey", "ok"),
                },
                RanAt: DateTimeOffset.UtcNow)));
        services.AddSingleton(NullLogger<StopgapSecretMigrator>.Instance);
        var sp = services.BuildServiceProvider();

        var exit = await MigrateSecretsCommand.RunAsync(sp);
        exit.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_ReturnsNonZero_WhenAnyRowFailed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStopgapSecretMigrator, StubMigrator>(_ =>
            new StubMigrator(new StopgapMigrationReport(
                Results: new[]
                {
                    new StopgapMigrationResult(
                        "anthropic/api-key",
                        StopgapMigrationOutcome.Failed,
                        "Anthropic:ApiKey", "boom"),
                },
                RanAt: DateTimeOffset.UtcNow)));
        services.AddSingleton(NullLogger<StopgapSecretMigrator>.Instance);
        var sp = services.BuildServiceProvider();

        var exit = await MigrateSecretsCommand.RunAsync(sp);
        exit.Should().NotBe(0);
    }

    [Test]
    public async Task RunAsync_Throws_WhenMigratorNotRegistered()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Func<Task> act = () => MigrateSecretsCommand.RunAsync(sp);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class StubMigrator : IStopgapSecretMigrator
    {
        private readonly StopgapMigrationReport _report;
        public StubMigrator(StopgapMigrationReport report) { _report = report; }

        public Task<StopgapMigrationReport> RunAsync(
            Guid actorUserId, CancellationToken ct = default) =>
            Task.FromResult(_report);
    }
}
