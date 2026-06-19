using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC7) — CLI entrypoint for
/// <c>dotnet run --project Tamma.Api -- seed-billing</c>. Mirrors
/// <c>MigrateSecretsCommand</c>: resolves <see cref="IBillingProvider"/> from
/// the built DI graph and runs an idempotent Stripe catalog sync.
///
/// <para>Single-user mode: prints "billing is SaaS-only" and exits 0 (the
/// <see cref="NullBillingProvider"/> is registered, so no Stripe call is made).
/// SaaS: runs the catalog sync, prints a per-slug created/reused report, and
/// exits 0 on success / 1 on failure. Re-running is a no-op.</para>
/// </summary>
public static class SeedBillingCommand
{
    /// <summary>True when the first positional arg is <c>seed-billing</c>.</summary>
    public static bool ShouldRun(string[] args) =>
        args is { Length: > 0 } && string.Equals(
            args[0], "seed-billing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Execute the catalog sync using the supplied DI <paramref name="services"/>.
    /// Opens a scope so the scoped provider + CP context resolve correctly.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var provider = sp.GetService<IBillingProvider>()
            ?? throw new InvalidOperationException(
                "IBillingProvider is not registered. Call AddTammaBilling() during startup wiring.");
        var logger = sp.GetService<ILogger<StripeBillingProvider>>();

        if (!provider.IsEnabled)
        {
            Console.WriteLine(
                "seed-billing: billing is SaaS-only — this Tamma instance runs in "
                + "single-user mode (NullBillingProvider). No Stripe catalog to sync.");
            return 0;
        }

        try
        {
            var result = await provider.SyncCatalogAsync(ct);

            Console.WriteLine(
                "seed-billing: catalog synced. created={0} reused={1}",
                result.TotalCreated, result.TotalReused);
            foreach (var slug in result.Slugs)
            {
                Console.WriteLine(
                    "  {0,-12} created={1} reused={2}", slug.PlanSlug, slug.Created, slug.Reused);
            }

            logger?.LogInformation(
                "seed-billing CLI completed: created={Created} reused={Reused} over {Slugs} slugs.",
                result.TotalCreated, result.TotalReused, result.Slugs.Count);
            return 0;
        }
        catch (Exception ex)
        {
            // Never echo the exception detail blindly (could carry config
            // context). The structured log gets the full error; stdout stays terse.
            Console.Error.WriteLine(
                "seed-billing: FAILED — see logs. ({0})", ex.GetType().Name);
            logger?.LogError(ex, "seed-billing CLI failed.");
            return 1;
        }
    }
}
