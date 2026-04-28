using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// CLI entrypoint for <c>dotnet run --project Tamma.Api -- migrate-secrets</c>
/// per Story 29-9 AC1. Resolves an <see cref="IStopgapSecretMigrator"/>
/// from the DI container, runs the import, and prints a human-readable
/// report to stdout (plus the structured report to the log).
///
/// <para>Returns 0 when every entry was Imported or Skipped; returns
/// a non-zero exit code when any entry ended up Failed so CI / the
/// runbook can escalate.</para>
/// </summary>
public static class MigrateSecretsCommand
{
    /// <summary>
    /// Returns true when <paramref name="args"/> selects this command
    /// (first positional arg is <c>migrate-secrets</c>).
    /// </summary>
    public static bool ShouldRun(string[] args) =>
        args is { Length: > 0 } && string.Equals(
            args[0], "migrate-secrets", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Execute the migration using the supplied
    /// <paramref name="services"/> provider. The caller is expected to
    /// have built the same DI graph the HTTP server uses so the
    /// migrator sees the real Postgres backend + auditor.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var migrator = services.GetService(typeof(IStopgapSecretMigrator))
            as IStopgapSecretMigrator
            ?? throw new InvalidOperationException(
                "IStopgapSecretMigrator is not registered. Call " +
                "AddTammaSecretStopgapMigrator() during startup wiring.");
        var logger = services.GetService(typeof(ILogger<StopgapSecretMigrator>))
            as ILogger<StopgapSecretMigrator>;

        var report = await migrator.RunAsync(Guid.Empty, ct);

        Console.WriteLine(
            "migrate-secrets: imported={0} skipped={1} no_source={2} failed={3}",
            report.ImportedCount, report.SkippedCount,
            report.NoSourceCount, report.FailedCount);
        foreach (var row in report.Results)
        {
            Console.WriteLine(
                "  {0,-30} {1,-14} {2}",
                row.CabinetName, row.Outcome, row.Detail ?? string.Empty);
        }

        logger?.LogInformation(
            "migrate-secrets CLI completed: {Report}", report);

        return report.FailedCount == 0 ? 0 : 2;
    }
}
