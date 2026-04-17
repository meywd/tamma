using Tamma.Api.Services.Email;

namespace Tamma.Api.Extensions;

/// <summary>
/// Wires the transactional-email abstraction into DI. The composition root
/// (<c>Program.cs</c>) should call <see cref="AddEmailServices"/> once — the
/// parent auth-foundation stream owns that file per the inter-stream contract,
/// so the wiring is exposed as an extension method here.
///
/// Selection rule:
/// <list type="bullet">
///   <item><description>If <c>Email:Smtp:Host</c> is configured →
///     <see cref="SmtpEmailService"/> (production / staging).</description></item>
///   <item><description>Otherwise → <see cref="InMemoryEmailService"/> with a
///     boot-time warning so operators are alerted.</description></item>
/// </list>
/// The registration is singleton because both implementations are
/// stateless per message (or deliberately stateful, in the in-memory case —
/// tests rely on the singleton inbox persisting across requests).
/// </summary>
public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmailServices(this IServiceCollection services)
    {
        services.AddSingleton<IEmailService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var host = config["Email:Smtp:Host"];

            if (!string.IsNullOrWhiteSpace(host))
            {
                var smtpLogger = sp.GetRequiredService<ILogger<SmtpEmailService>>();
                return new SmtpEmailService(config, smtpLogger);
            }

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var fallbackLogger = loggerFactory.CreateLogger(typeof(EmailServiceCollectionExtensions));
            fallbackLogger.LogWarning(
                "Email:Smtp:Host is not configured; falling back to InMemoryEmailService. " +
                "No transactional emails will be delivered. Configure SMTP in non-test environments.");
            return new InMemoryEmailService();
        });

        return services;
    }
}
