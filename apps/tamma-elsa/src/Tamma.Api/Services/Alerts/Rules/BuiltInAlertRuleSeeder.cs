using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Options for <see cref="BuiltInAlertRuleSeeder"/>.
/// </summary>
public sealed class BuiltInAlertRuleSeederOptions
{
    /// <summary>
    /// When <c>true</c> (default) the seeder runs in <c>StartAsync</c>
    /// during host bootstrap. Tests that don't need the built-in rules
    /// override this to <c>false</c> to skip the per-factory DB
    /// round-trip (~hundreds of ms × 75 factories in the API test
    /// suite). The seeder method <see cref="BuiltInAlertRuleSeeder.SeedAsync"/>
    /// is still callable directly for tests that opt back in.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Story 5.6 (Wave C.2) — seeds the five built-in alert rules into
/// <c>alert_rules</c> on app startup. Runs as an
/// <see cref="IHostedService"/> before <see cref="AlertRuleEvaluator"/>
/// so the evaluator sees the built-ins on its first refresh.
///
/// <para><b>Idempotency contract</b>:</para>
/// <list type="bullet">
///   <item><description>Re-run = no-op in the common case (already-
///     seeded rows with unchanged spec).</description></item>
///   <item><description>Spec drift (e.g. we bumped a description or
///     predicate between releases) triggers a surgical update on
///     <c>description</c>, <c>event_type</c>, <c>predicate</c>,
///     <c>throttle_seconds</c>, <c>is_built_in</c>. The seeder does
///     NOT touch <c>is_enabled</c>, <c>channel_ids</c>, or
///     <c>severity</c> so admin overrides survive re-deploy.</description></item>
///   <item><description>New built-in key → insert a fresh row.</description></item>
///   <item><description>Existing built-in key no longer in the spec
///     list → the seeder leaves it alone (a future release wanting
///     to retire a built-in should explicitly delete it). No silent
///     deletion.</description></item>
/// </list>
/// </summary>
public sealed class BuiltInAlertRuleSeeder : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BuiltInAlertRuleSeeder> _logger;
    private readonly BuiltInAlertRuleSeederOptions _options;

    public BuiltInAlertRuleSeeder(
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<BuiltInAlertRuleSeeder> logger)
        : this(services, timeProvider, logger, new BuiltInAlertRuleSeederOptions())
    {
    }

    public BuiltInAlertRuleSeeder(
        IServiceProvider services,
        TimeProvider timeProvider,
        ILogger<BuiltInAlertRuleSeeder> logger,
        BuiltInAlertRuleSeederOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "BuiltInAlertRuleSeeder gated off (RunOnStartup=false); skipping startup seed.");
            return;
        }

        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Don't fail app startup on seed drift — the evaluator
            // still works with whatever rules are in the DB. Log loud
            // so CI / prod ops see the drift.
            _logger.LogError(ex,
                "BuiltInAlertRuleSeeder failed; continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<SeedResult> SeedAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();

        // Fetch all existing built-in rows in one round-trip. We key
        // by BuiltInKey; null keys are admin-created rules and are
        // untouched.
        var existing = await db.AlertRules
            .Where(r => r.BuiltInKey != null)
            .ToDictionaryAsync(r => r.BuiltInKey!, ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        int inserted = 0, updated = 0, unchanged = 0;

        foreach (var spec in BuiltInAlertRules.All)
        {
            if (existing.TryGetValue(spec.BuiltInKey, out var row))
            {
                if (ApplySurgicalUpdate(row, spec, now))
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                db.AlertRules.Add(new AlertRule
                {
                    // Set Id client-side so EF InMemory (test shim)
                    // doesn't collide on the Guid.Empty default. In
                    // production Postgres applies gen_random_uuid()
                    // anyway, so this is a strict superset.
                    Id = Guid.NewGuid(),
                    Name = spec.Name,
                    Description = spec.Description,
                    IsEnabled = true,
                    Severity = spec.Severity,
                    EventType = spec.EventType,
                    Predicate = spec.Predicate,
                    ThrottleSeconds = spec.ThrottleSeconds,
                    ChannelIds = Array.Empty<Guid>(),
                    IsBuiltIn = true,
                    BuiltInKey = spec.BuiltInKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Built-in alert rules seeded: {Inserted} inserted, " +
            "{Updated} updated, {Unchanged} unchanged.",
            inserted, updated, unchanged);

        return new SeedResult(inserted, updated, unchanged);
    }

    /// <summary>
    /// Apply surgical update to drift fields only. Returns true when
    /// the row was actually changed (SaveChanges will flush), false
    /// when the row already matches the spec.
    /// </summary>
    private static bool ApplySurgicalUpdate(
        AlertRule row, BuiltInAlertRuleSpec spec, DateTime now)
    {
        var changed = false;

        if (row.Description != spec.Description)
        {
            row.Description = spec.Description;
            changed = true;
        }
        if (row.EventType != spec.EventType)
        {
            row.EventType = spec.EventType;
            changed = true;
        }
        if (row.Predicate != spec.Predicate)
        {
            row.Predicate = spec.Predicate;
            changed = true;
        }
        if (row.ThrottleSeconds != spec.ThrottleSeconds)
        {
            row.ThrottleSeconds = spec.ThrottleSeconds;
            changed = true;
        }
        if (!row.IsBuiltIn)
        {
            row.IsBuiltIn = true;
            changed = true;
        }
        // Preserve admin overrides: is_enabled, channel_ids, severity,
        // name (Name is an identity; never overwrite).

        if (changed)
        {
            row.UpdatedAt = now;
        }
        return changed;
    }

    public sealed record SeedResult(int Inserted, int Updated, int Unchanged);
}
