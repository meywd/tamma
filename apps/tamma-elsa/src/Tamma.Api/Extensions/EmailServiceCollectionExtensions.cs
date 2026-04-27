using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Email;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// Wires the transactional-email subsystem into DI. The composition root
/// (<c>Program.cs</c>) calls <see cref="AddEmailServices"/> once — the parent
/// auth-foundation stream owns that file per the inter-stream contract, so
/// the wiring lives here.
///
/// <para>Provider selection (<c>Email:Provider</c>, default <c>"smtp"</c>):</para>
/// <list type="bullet">
///   <item><description><c>"smtp"</c> — <see cref="SmtpEmailService"/>
///     (outbox-backed) + <see cref="OutboxSmtpSender"/> hosted service. Requires
///     <c>Email:Smtp:Host</c> at sender start-up, but enqueueing works without
///     any SMTP config — messages simply pile up until the sender is healthy.</description></item>
///   <item><description><c>"resend"</c> — <see cref="ResendEmailService"/>
///     using the named <c>"resend"</c> <see cref="IHttpClientFactory"/> client.
///     Requires <c>Email:Resend:ApiKey</c>.</description></item>
///   <item><description>Anything else / empty — falls back to
///     <see cref="InMemoryEmailService"/> with a boot-time warning so local-dev
///     stays usable.</description></item>
/// </list>
/// </summary>
public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmailServices(this IServiceCollection services)
    {
        // Shared repository — Scoped because ControlPlaneDbContext is Scoped.
        services.TryAddScoped<IEmailOutboxRepository, EmailOutboxRepository>();
        // Story 28-1 PR B — SmtpEmailService routes platform-scope email
        // (verification, password reset, welcome) through the platform
        // repo. TryAdd so callers that already register a custom impl
        // win.
        services.TryAddScoped<IPlatformEmailOutboxRepository, PlatformEmailOutboxRepository>();

        // The production SMTP transport. Can be replaced in tests by
        // substituting a test-double ISmtpTransport before AddEmailServices
        // runs, or by re-registering afterwards.
        services.TryAddSingleton<ISmtpTransport, MailKitSmtpTransport>();

        // Named HttpClient for Resend. Safe to register unconditionally — it
        // is only resolved when ResendEmailService is the active provider.
        services.AddHttpClient("resend", client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
        });

        services.AddSingleton<OutboxSmtpSenderOptions>();

        services.AddSingleton<IEmailService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var provider = (config["Email:Provider"] ?? "smtp").Trim().ToLowerInvariant();

            return provider switch
            {
                "resend" => BuildResend(sp),
                "smtp" => BuildSmtp(sp),
                _ => BuildInMemoryFallback(sp, loggerFactory),
            };
        });

        // Registered unconditionally; the sender's ExecuteAsync bails out
        // immediately when Email:Provider != "smtp" so InMemory / Resend
        // modes pay zero polling overhead.
        services.AddHostedService<OutboxSmtpSender>();

        return services;
    }

    private static IEmailService BuildResend(IServiceProvider sp)
    {
        return new ResendEmailService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IEventRepository>(),
            sp.GetRequiredService<Tamma.Data.ITenantContext>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ResendEmailService>>());
    }

    private static IEmailService BuildSmtp(IServiceProvider sp)
    {
        // SmtpEmailService resolves the scoped outbox repo + event repo via a
        // scoped factory on each SendAsync. We materialise a scope here and
        // wrap it so callers don't have to think about lifetimes.
        return new ScopedSmtpEmailService(sp);
    }

    private static IEmailService BuildInMemoryFallback(
        IServiceProvider sp, ILoggerFactory loggerFactory)
    {
        var fallbackLogger = loggerFactory.CreateLogger(typeof(EmailServiceCollectionExtensions));
        fallbackLogger.LogWarning(
            "Email:Provider is not configured to smtp|resend; using InMemoryEmailService. " +
            "No transactional emails will be delivered. Configure Email:Provider in non-test environments.");

        // Best-effort: wire the inbox to the event store so tests that DO care
        // about events get them for free.
        var events = sp.GetService<IEventRepository>();
        return events is null ? new InMemoryEmailService() : new InMemoryEmailService(events);
    }
}

/// <summary>
/// Adapter that lets a Scoped-DbContext-backed <see cref="SmtpEmailService"/>
/// be resolved as a Singleton <see cref="IEmailService"/>. Each
/// <see cref="SendAsync"/> call opens its own DI scope, constructs a fresh
/// <see cref="SmtpEmailService"/>, delegates, and disposes the scope.
///
/// <para>Why not just register SmtpEmailService as Scoped and let DI manage
/// lifetimes? Because <see cref="IEmailService"/> is resolved as a Singleton
/// (see <see cref="EmailServiceCollectionExtensions"/>) so the InMemory
/// fallback retains its SentMessages queue across requests — and that's the
/// contract the integration tests assert against.</para>
/// </summary>
internal sealed class ScopedSmtpEmailService : IEmailService
{
    private readonly IServiceProvider _rootProvider;

    public ScopedSmtpEmailService(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public async Task<Guid> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var scope = _rootProvider.CreateScope();
        var inner = new SmtpEmailService(
            scope.ServiceProvider.GetRequiredService<IEmailOutboxRepository>(),
            scope.ServiceProvider.GetRequiredService<IPlatformEmailOutboxRepository>(),
            scope.ServiceProvider.GetRequiredService<IEventRepository>(),
            scope.ServiceProvider.GetRequiredService<Tamma.Data.ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            scope.ServiceProvider.GetRequiredService<ILogger<SmtpEmailService>>());
        return await inner.SendAsync(message, ct);
    }
}

