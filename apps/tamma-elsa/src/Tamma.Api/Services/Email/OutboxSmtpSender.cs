using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Email;

/// <summary>
/// Options bound by <see cref="EmailServiceCollectionExtensions.AddEmailServices"/>.
/// </summary>
public sealed class OutboxSmtpSenderOptions
{
    /// <summary>How often the sender polls for pending messages. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry backoff schedule. The nth entry applies after the nth failure.
    /// Defaults: 60s → 5m → 30m → 2h → 6h.
    /// </summary>
    public IReadOnlyList<TimeSpan> BackoffSchedule { get; set; } = new[]
    {
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };

    /// <summary>
    /// Task #10 (post-review): when <c>true</c> (default) the sender starts
    /// its poll loop in <see cref="BackgroundService.ExecuteAsync"/>. Tests
    /// that assert outbox-row state (e.g. <c>AuthRegisterTxnIdIntegrationTests
    /// .Register_OutboxRowPersistedWithMatchingTxnId</c>) flake when the
    /// loop races the test and flips <c>status="pending"</c> to
    /// <c>"sent"</c> / <c>"failed"</c> before the assertion runs. The shared
    /// test fixture (and <c>AuthRegisterTxnIdIntegrationTests</c>'s own derived
    /// host) opt out via the <c>AlertHostedServiceTestExtensions
    /// .DisableAlertHostedServices</c> helper, which sets this flag false.
    /// Mirrors the existing <c>BuiltInAlertRuleSeederOptions.RunOnStartup</c>
    /// gate pattern.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Lease timeout for the durability reaper. A row claimed into <c>sending</c>
    /// whose <c>UpdatedAt</c> is older than this is assumed orphaned by a crashed
    /// sender and reset to <c>pending</c> so it is re-delivered (at-least-once).
    /// Applies to BOTH the per-tenant <c>email_outbox</c> and the control-plane
    /// <c>platform_email_outbox</c>. The reap runs at most once per this interval
    /// (throttled in the poll loop). Default 5 minutes.
    /// </summary>
    public TimeSpan SendingLeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// <see cref="BackgroundService"/> that drains the email outbox via
/// <see cref="IEmailOutboxRepository.ClaimNextPendingAsync"/> and delivers each
/// claimed message through an <see cref="ISmtpTransport"/>. Retries use an
/// exponential backoff schedule; the transaction id is the only identifier
/// ever placed in log lines. Event emission:
/// <list type="bullet">
///   <item><description><see cref="EmailEventTypes.Sent"/> on success.</description></item>
///   <item><description><see cref="EmailEventTypes.Failed"/> only when the retry
///     ceiling is reached (the final, permanent failure).</description></item>
/// </list>
///
/// <para>Story 28-6 — the sender drains BOTH the per-tenant
/// <c>email_outbox</c> AND the control-plane <c>platform_email_outbox</c>
/// in the same loop. Platform-scope mail (registration verification,
/// welcome, password reset, deletion confirmation) lives on the CP table
/// because it must deliver before a tenant DB exists or after one is
/// gone (Doc 03 §7.1, Epic 28 conflict resolution #2). The CP repo is
/// optional — when <see cref="IPlatformEmailOutboxRepository"/> is not
/// registered (legacy single-DB topologies) the platform path is a
/// no-op, so this change is back-compat with deployments that have
/// not yet shipped the CP migration.</para>
/// </summary>
public sealed class OutboxSmtpSender : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxSmtpSenderOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<OutboxSmtpSender> _logger;

    // Story 28-6 — set to 1 after the first Postgres 42P01 (relation does
    // not exist) on the platform queue so subsequent polls skip the
    // attempt entirely instead of letting EF log the error each cycle.
    // Volatile read in the hot path.
    private int _platformPathDisabled;

    // Durability reaper throttle — the reclaim UPDATE matches 0 rows in steady
    // state, so it runs at most once per SendingLeaseTimeout rather than every
    // poll. MinValue means "reap on the first cycle" so a restart immediately
    // recovers rows a prior crash orphaned in 'sending'.
    private DateTime _lastReclaimUtc = DateTime.MinValue;

    public OutboxSmtpSender(
        IServiceProvider serviceProvider,
        OutboxSmtpSenderOptions options,
        IConfiguration config,
        ILogger<OutboxSmtpSender> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Task #10 (post-review): gate for the shared test fixture. Tests
        // that assert outbox-row state shouldn't race the poll loop.
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "OutboxSmtpSender gated off (RunOnStartup=false); skipping poll loop.");
            return;
        }

        // Only run when SMTP is the active provider. The single hosted-service
        // registration covers all three provider modes (smtp / resend /
        // in-memory) so we don't need conditional DI registration.
        var provider = (_config["Email:Provider"] ?? "smtp").Trim().ToLowerInvariant();
        if (provider != "smtp")
        {
            _logger.LogInformation("OutboxSmtpSender disabled (non-smtp provider)");
            return;
        }

        // Allow the poll interval to be overridden by configuration at startup.
        var seconds = _config.GetValue("Email:OutboxPollIntervalSeconds", 0);
        if (seconds > 0)
        {
            _options.PollInterval = TimeSpan.FromSeconds(seconds);
        }

        var leaseSeconds = _config.GetValue("Email:OutboxSendingLeaseSeconds", 0);
        if (leaseSeconds > 0)
        {
            _options.SendingLeaseTimeout = TimeSpan.FromSeconds(leaseSeconds);
        }

        _logger.LogInformation(
            "OutboxSmtpSender started. Poll interval={Interval}",
            _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Seam D (Story 43-9 AC9) — ONE gate call per tick, deny-only.
                if (await Tamma.Api.Services.Actions.BackgroundActionGateAccessor
                        .MayRunTickAsync(
                            _serviceProvider,
                            Tamma.Core.Actions.BackgroundActor.OutboxSmtpSender,
                            tenantId: null, stoppingToken).ConfigureAwait(false))
                {
                    await ProcessOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxSmtpSender cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxSmtpSender stopped");
    }

    /// <summary>
    /// Claim and deliver a single message from either the per-tenant or
    /// the platform outbox, returning <c>true</c> when a row was
    /// processed. Tenant outbox is drained first to preserve historical
    /// behaviour; platform outbox is checked only when the tenant queue
    /// is empty. Exposed for tests so they don't race the polling timer.
    ///
    /// <para>Story 28-1 PR B — the tenant claim path now fans out
    /// across active tenants via
    /// <see cref="IEmailOutboxRepository.ClaimNextPendingFromAnyTenantAsync"/>
    /// instead of the previous "scan a single shared CP table" path.
    /// Once PR D moves the per-tenant outbox into per-tenant DBs the
    /// fan-out becomes the only correct way to drain.</para>
    ///
    /// <para>Wave-4 review H3 — tenant-first ordering CAN starve the
    /// platform queue indefinitely under continuous tenant traffic. Pre-PR
    /// the direct-CP-scan path interleaved tenant + platform rows by
    /// <c>NextAttemptAt</c>; the cycle-aware ordering here gives no
    /// fairness guarantee. Acceptable today because (a) the platform
    /// queue carries low-volume verification / password-reset / welcome
    /// mail and (b) a busy tenant queue still drains one row per poll
    /// so platform mail is delayed at most one poll cycle past the moment
    /// the tenant queue empties. Round-robin (alternate tenant ↔
    /// platform per cycle) is tracked as a follow-up. Re-evaluate when
    /// EMAIL.QUEUED.SUCCESS rows pile up on the platform table without
    /// EMAIL.SENT.SUCCESS catching up.</para>
    /// </summary>
    public async Task<bool> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutboxRepository>();
        var transport = scope.ServiceProvider.GetRequiredService<ISmtpTransport>();
        var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();

        var now = DateTime.UtcNow;

        // Durability reaper (throttled) — recover rows a crashed sender left
        // orphaned in 'sending' back to 'pending' before we claim the next
        // batch. Covers both the per-tenant and platform queues so at-least-once
        // delivery holds across process restarts.
        await MaybeReclaimAsync(scope.ServiceProvider, outbox, now, ct);

        var claimed = await outbox.ClaimNextPendingFromAnyTenantAsync(now, ct);
        if (claimed is not null)
        {
            await ProcessTenantClaimedAsync(outbox, transport, events, claimed, ct);
            return true;
        }

        // Tenant queue empty — try the platform queue. Story 28-6.
        return await TryProcessPlatformOnceAsync(scope.ServiceProvider, transport, ct);
    }

    /// <summary>
    /// Run the durability reaper at most once per
    /// <see cref="OutboxSmtpSenderOptions.SendingLeaseTimeout"/>, across BOTH
    /// the per-tenant <c>email_outbox</c> (cross-tenant fan-out) and the
    /// control-plane <c>platform_email_outbox</c>. Rows a crashed sender
    /// orphaned in <c>sending</c> (UpdatedAt older than the lease) are reset to
    /// <c>pending</c> so the next claim re-delivers them. Folded into the
    /// existing poll loop — no extra hosted service. The platform reap honours
    /// the same 42P01 / not-registered back-compat guards as the drain path;
    /// reap failures are logged and swallowed so the claim path still runs.
    /// </summary>
    private async Task MaybeReclaimAsync(
        IServiceProvider scopedProvider,
        IEmailOutboxRepository outbox,
        DateTime now,
        CancellationToken ct)
    {
        if (now - _lastReclaimUtc < _options.SendingLeaseTimeout) return;
        _lastReclaimUtc = now;

        // Per-tenant queues.
        try
        {
            var reclaimed = await outbox.ReclaimStuckSendingFromAllTenantsAsync(
                now, _options.SendingLeaseTimeout, ct);
            if (reclaimed > 0)
            {
                _logger.LogWarning(
                    "Reclaimed {Count} tenant email row(s) stuck in 'sending' past the {Lease} lease",
                    reclaimed, _options.SendingLeaseTimeout);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant email outbox reclaim pass failed");
        }

        // Platform queue — same back-compat guards as TryProcessPlatformOnceAsync.
        if (Volatile.Read(ref _platformPathDisabled) == 1) return;

        var platformOutbox = scopedProvider.GetService<IPlatformEmailOutboxRepository>();
        if (platformOutbox is null) return;

        try
        {
            var reclaimed = await platformOutbox.ReclaimStuckSendingAsync(
                now, _options.SendingLeaseTimeout, ct);
            if (reclaimed > 0)
            {
                _logger.LogWarning(
                    "Reclaimed {Count} platform email row(s) stuck in 'sending' past the {Lease} lease",
                    reclaimed, _options.SendingLeaseTimeout);
            }
        }
        catch (Npgsql.PostgresException pgEx)
            when (string.Equals(pgEx.SqlState, "42P01", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _platformPathDisabled, 1);
            _logger.LogWarning(
                "platform_email_outbox table missing on this connection — " +
                "disabling the platform email path for this process. " +
                "Apply the Story 28-1 CP migration to enable it.");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            when (dbEx.InnerException is Npgsql.PostgresException pgEx
                && string.Equals(pgEx.SqlState, "42P01", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _platformPathDisabled, 1);
            _logger.LogWarning(
                "platform_email_outbox table missing on this connection — " +
                "disabling the platform email path for this process. " +
                "Apply the Story 28-1 CP migration to enable it.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Platform email outbox reclaim pass failed");
        }
    }

    private async Task ProcessTenantClaimedAsync(
        IEmailOutboxRepository outbox,
        ISmtpTransport transport,
        IEventRepository events,
        EmailOutboxMessage claimed,
        CancellationToken ct)
    {
        // ClaimNextPendingFromAnyTenantAsync always returns a row whose
        // TenantId is set — the tenant outbox is strictly tenant-scoped
        // post Story 28-1 PR B. Defensive null-check kept so a future
        // contract change shows up at runtime not as a silent CP hit.
        if (claimed.TenantId is not Guid tid)
        {
            _logger.LogError(
                "Tenant-outbox row {TxnId} has no TenantId — refusing to mark sent",
                claimed.Id);
            return;
        }

        try
        {
            await transport.SendAsync(claimed, ct);
            await outbox.MarkSentAsync(tid, claimed.Id, ct);
            await EmitSentAsync(events, claimed);

            // Purge the row now that the event store has the permanent audit.
            // Recipient address, subject, and body don't need to persist beyond
            // delivery — EMAIL.SENT.SUCCESS holds txn id + template metadata.
            // Failed rows (MarkFailedAsync → Status=failed) are NOT deleted;
            // operators need them for inspection.
            await outbox.DeleteAsync(tid, claimed.Id, ct);

            _logger.LogInformation("Email delivered txn={TxnId}", claimed.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NEVER log recipient / subject / body / host — only the txn id.
            var attempt = claimed.Attempts + 1;
            var backoff = PickBackoff(attempt);
            var updated = await outbox.MarkFailedAsync(tid, claimed.Id, ex.Message, backoff, ct);

            if (updated is not null && updated.Status == "failed")
            {
                _logger.LogError(ex,
                    "Email permanently failed txn={TxnId} attempts={Attempts}",
                    claimed.Id, updated.Attempts);
                await EmitFailedAsync(events, claimed, ex);

                // Inbox is a retry buffer only — once retries are exhausted,
                // the audit lives in the event store (EMAIL.SENT.FAILED with
                // txn id + error class). The row carries recipient / subject
                // / body which aren't needed after the terminal outcome, so
                // delete it too.
                await outbox.DeleteAsync(tid, claimed.Id, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Email transient failure txn={TxnId} attempt={Attempt}",
                    claimed.Id, attempt);
            }
        }
    }

    /// <summary>
    /// Story 28-6 — drain one row from <c>platform_email_outbox</c> if
    /// the repo is registered. Falls back to <c>false</c> (no-op) in two
    /// back-compat cases:
    /// <list type="bullet">
    ///   <item><description><see cref="IPlatformEmailOutboxRepository"/>
    ///     is not registered (legacy single-DB topology).</description></item>
    ///   <item><description>Repo is registered but the underlying
    ///     <c>platform_email_outbox</c> table doesn't exist yet (Postgres
    ///     error 42P01 — the CP migration from Story 28-1 hasn't been
    ///     applied to the test/dev database). The first occurrence is
    ///     logged at debug; subsequent polls stay quiet.</description></item>
    /// </list>
    /// </summary>
    private async Task<bool> TryProcessPlatformOnceAsync(
        IServiceProvider scopedProvider, ISmtpTransport transport, CancellationToken ct)
    {
        if (Volatile.Read(ref _platformPathDisabled) == 1)
        {
            // A previous poll hit 42P01 — the CP migration hasn't been
            // applied to this DB. Skip without touching the connection so
            // we don't log a stack trace every cycle.
            return false;
        }

        var platformOutbox = scopedProvider.GetService<IPlatformEmailOutboxRepository>();
        if (platformOutbox is null)
        {
            // Legacy / single-DB topology — silently skip the platform
            // path on each poll. Logged once at startup if the operator
            // wants to see why; not per-poll to keep logs quiet.
            return false;
        }

        PlatformEmailOutboxMessage? claimed;
        try
        {
            claimed = await platformOutbox.ClaimNextPendingAsync(DateTime.UtcNow, ct);
        }
        catch (Npgsql.PostgresException pgEx)
            when (string.Equals(pgEx.SqlState, "42P01", StringComparison.Ordinal))
        {
            // platform_email_outbox table missing — repo was wired but the
            // CP migration hasn't been applied to this DB. Disable the
            // platform path for the lifetime of the process to keep logs
            // quiet; restart picks up the CP migration on the next boot.
            Interlocked.Exchange(ref _platformPathDisabled, 1);
            _logger.LogWarning(
                "platform_email_outbox table missing on this connection — " +
                "disabling the platform email path for this process. " +
                "Apply the Story 28-1 CP migration to enable it.");
            return false;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            when (dbEx.InnerException is Npgsql.PostgresException pgEx
                && string.Equals(pgEx.SqlState, "42P01", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _platformPathDisabled, 1);
            _logger.LogWarning(
                "platform_email_outbox table missing on this connection — " +
                "disabling the platform email path for this process. " +
                "Apply the Story 28-1 CP migration to enable it.");
            return false;
        }

        if (claimed is null) return false;

        // Map the platform row onto an EmailOutboxMessage so the existing
        // ISmtpTransport seam works unchanged. Only the fields the
        // transport reads are populated; status/attempts on the platform
        // row remain authoritative for retry policy.
        var transportShim = ToTransportShim(claimed);

        // Resolve the CP-bound IPlatformEventRepository for terminal
        // event emission. Optional — if absent, the sent/failed events
        // simply aren't written and the operation continues; the
        // platform outbox row itself carries enough state for ops.
        var platformEvents = scopedProvider.GetService<IPlatformEventRepository>();

        try
        {
            await transport.SendAsync(transportShim, ct);
            await platformOutbox.MarkSentAsync(claimed.Id, ct);
            if (platformEvents is not null)
            {
                await EmitPlatformSentAsync(platformEvents, claimed, ct);
            }

            // Same delete-on-success contract as the tenant path —
            // recipient/subject/body don't linger past delivery. The
            // permanent audit lives in platform_events.
            await platformOutbox.DeleteAsync(claimed.Id, ct);

            _logger.LogInformation(
                "Platform email delivered txn={TxnId} template={Template}",
                claimed.Id, claimed.Template);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempt = claimed.Attempts + 1;
            var backoff = PickBackoff(attempt);
            var updated = await platformOutbox.MarkFailedAsync(
                claimed.Id, ex.Message, backoff, ct);

            if (updated is not null && updated.Status == "failed")
            {
                _logger.LogError(ex,
                    "Platform email permanently failed txn={TxnId} attempts={Attempts}",
                    claimed.Id, updated.Attempts);
                if (platformEvents is not null)
                {
                    await EmitPlatformFailedAsync(platformEvents, claimed, ex, ct);
                }

                // Same delete-on-terminal-failure contract — the audit
                // is in platform_events; the row's PII can go.
                await platformOutbox.DeleteAsync(claimed.Id, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Platform email transient failure txn={TxnId} attempt={Attempt}",
                    claimed.Id, attempt);
            }

            return true;
        }
    }

    /// <summary>
    /// Shim a <see cref="PlatformEmailOutboxMessage"/> into the
    /// <see cref="EmailOutboxMessage"/> shape the existing
    /// <see cref="ISmtpTransport"/> consumes. Only fields the transport
    /// reads (recipient, subject, html/text body, from, attempt count)
    /// are copied; everything mutable on the platform row stays under
    /// <see cref="IPlatformEmailOutboxRepository"/> control.
    /// </summary>
    private static EmailOutboxMessage ToTransportShim(PlatformEmailOutboxMessage src)
    {
        return new EmailOutboxMessage
        {
            Id = src.Id,
            TenantId = src.TenantId,
            UserId = src.UserId,
            Template = src.Template,
            ToAddress = src.ToAddress,
            Subject = src.Subject,
            HtmlBody = src.HtmlBody,
            TextBody = src.TextBody,
            FromAddress = src.FromAddress,
            Status = src.Status,
            Attempts = src.Attempts,
            MaxAttempts = src.MaxAttempts,
            NextAttemptAt = src.NextAttemptAt,
            LastError = src.LastError,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,
            SentAt = src.SentAt,
        };
    }

    private TimeSpan PickBackoff(int attempt)
    {
        if (_options.BackoffSchedule.Count == 0) return TimeSpan.FromMinutes(1);
        var idx = Math.Min(attempt - 1, _options.BackoffSchedule.Count - 1);
        idx = Math.Max(idx, 0);
        return _options.BackoffSchedule[idx];
    }

    private static async Task EmitSentAsync(IEventRepository events, EmailOutboxMessage row)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["attempts"] = row.Attempts + 1,
        };
        await events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Sent,
            TenantId = row.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }

    private static async Task EmitFailedAsync(
        IEventRepository events, EmailOutboxMessage row, Exception ex)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["error_class"] = ex.GetType().FullName,
        };
        await events.AppendAsync(new DomainEvent
        {
            Type = EmailEventTypes.Failed,
            TenantId = row.TenantId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        });
    }

    // Story 28-6 — terminal-outcome events for the platform outbox land
    // in platform_events instead of the per-tenant domain_events stream
    // because some platform mail (verification, deletion confirmation)
    // fires before/after a tenant DB exists.
    private static async Task EmitPlatformSentAsync(
        IPlatformEventRepository events,
        PlatformEmailOutboxMessage row,
        CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
            ["scope"] = "platform",
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["attempts"] = row.Attempts + 1,
        };
        await events.AppendAsync(new PlatformEvent
        {
            Type = EmailEventTypes.Sent,
            TenantId = row.TenantId,
            UserId = row.UserId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        }, ct);
    }

    private static async Task EmitPlatformFailedAsync(
        IPlatformEventRepository events,
        PlatformEmailOutboxMessage row,
        Exception ex,
        CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["txn_id"] = row.Id.ToString(),
            ["template"] = row.Template,
            ["tenant_id"] = row.TenantId?.ToString(),
            ["user_id"] = row.UserId?.ToString(),
            ["scope"] = "platform",
        };
        var data = new Dictionary<string, object?>
        {
            ["provider"] = "smtp",
            ["error_class"] = ex.GetType().FullName,
        };
        await events.AppendAsync(new PlatformEvent
        {
            Type = EmailEventTypes.Failed,
            TenantId = row.TenantId,
            UserId = row.UserId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        }, ct);
    }
}
