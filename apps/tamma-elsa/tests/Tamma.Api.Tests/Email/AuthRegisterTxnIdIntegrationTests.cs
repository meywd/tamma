using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// End-to-end validation of the new event-sourced, transaction-id-correlated
/// email pipeline. Registers a user against the real HTTP + Postgres stack
/// configured for SMTP provider mode (outbox path) and asserts:
/// <list type="bullet">
///   <item><description>A <c>EMAIL.QUEUED.SUCCESS</c> row appears with a
///     <c>txn_id</c> tag.</description></item>
///   <item><description>A <c>platform_email_outbox</c> row exists whose
///     <c>Id</c> equals that <c>txn_id</c> — proving end-to-end correlation
///     through the pipeline.</description></item>
/// </list>
///
/// <para>Story 28-1 PR B — verification email is now platform-scope
/// (no tenant DB exists yet at registration). The row lands in
/// <c>platform_email_outbox</c> not the per-tenant <c>email_outbox</c>;
/// the QUEUED event uses the same txn-id as the platform-outbox row.</para>
///
/// <para>The log-line-level assertion ("Register logs the txn id") lives in
/// <see cref="AuthRegisterLogAssertionTests"/> (unit scope). Serilog's hosted
/// <c>UseSerilog</c> replaces the <see cref="ILoggerFactory"/> wholesale, so
/// an in-process <see cref="ILoggerProvider"/> registered via DI is not
/// visible — the unit test exercises the endpoint directly with its own
/// logger factory instead.</para>
/// </summary>
[TestFixture]
public class AuthRegisterTxnIdIntegrationTests
{
    private const string TestEmail = "txnid@example.com";

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    private HttpClient CreateClient()
    {
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            // Determinism guard (Story 28-6 follow-up): explicitly gate the
            // racy BackgroundService loops in THIS derived host instead of
            // relying on the base fixture's DisableAlertHostedServices being
            // inherited through WithWebHostBuilder. The OutboxSmtpSender poll
            // loop, if live, claims the freshly-persisted pending row and
            // mutates its Status / Attempts / NextAttemptAt (or deletes it on
            // terminal failure) within the 5s poll window — exactly the
            // active→pending→failed transition that flaked the Status
            // assertion below. Gating it makes the producer the SOLE writer of
            // the row for the lifetime of the assertion. ProcessOnceAsync stays
            // public for the email tests that DO want to drive delivery.
            b.DisableAlertHostedServices();

            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:Provider"] = "smtp",
                    ["Email:From"] = "noreply@tamma.dev",
                    // Host is deliberately absent — we don't want the outbox
                    // sender actually delivering. The enqueue + event-emit
                    // path under test is upstream of the sender.
                });
            });
        }).CreateClient();
    }

    [Test]
    public async Task Register_EmitsQueuedEventWithTxnIdMatchingOutboxRow()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = TestEmail,
            password = "Sup3rSecure!",
            displayName = "TxnId Tester"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // The QUEUED event + outbox row are written synchronously inside the
        // request (Register awaits emailService.SendAsync before 201), and the
        // sender is gated off (CreateClient), so both are stable by now — no
        // flush delay or background-loop race to wait out.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // Story 28-1 PR D — verification email is platform-scope. The
        // QUEUED event lands in platform_events (EventRepository delegates
        // null-tenant appends to IPlatformEventRepository), not in the
        // tenant-resident domain_events.
        var queued = await db.PlatformEvents
            .IgnoreQueryFilters()
            .Where(e => e.Type == EmailEventTypes.Queued)
            .ToListAsync();

        queued.Should().ContainSingle(
            "registration must enqueue exactly one email and emit exactly one QUEUED event");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(queued[0].Tags)!;
        var txnIdStr = tags["txn_id"];
        txnIdStr.Should().NotBeNullOrEmpty();
        tags["template"].Should().Be("verification");

        // Story 28-1 PR B — verification email is platform-scope.
        // The txn_id on the event MUST equal the platform-outbox row id.
        var outboxRows = await db.PlatformEmailOutbox.ToListAsync();
        outboxRows.Should().ContainSingle();
        outboxRows[0].Id.ToString().Should().Be(txnIdStr);

        // Event payload must NOT leak recipient / subject / body — CodeQL
        // would flag those. Tags and data are checked separately.
        var combined = queued[0].Tags + queued[0].Data;
        combined.Should().NotContain(TestEmail);
        combined.Should().NotContain("Verify your Tamma");
    }

    [Test]
    public async Task Register_OutboxRowPersistedWithMatchingTxnId()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "outbox-row@example.com",
            password = "Sup3rSecure!",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // No Task.Delay needed: AuthEndpoints.Register awaits
        // emailService.SendAsync BEFORE it returns 201, so the platform-outbox
        // row is durably persisted by the time the response is observed. With
        // the OutboxSmtpSender gated off (see CreateClient), the producer is the
        // SOLE writer of this row — so Status="pending" is a stable invariant,
        // not a value we happened to read before a background loop flipped it.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // Story 28-1 PR B — verification email is platform-scope; row
        // lands in platform_email_outbox.
        var rows = await db.PlatformEmailOutbox.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be("pending");
        rows[0].Template.Should().Be("verification");
        rows[0].ToAddress.Should().Be("outbox-row@example.com",
            "the OUTBOX is the one place we DO persist the recipient");

        // Story 28-1 PR D — verification email QUEUED event lands in
        // platform_events (platform-scope, null tenant id).
        var queued = await db.PlatformEvents
            .IgnoreQueryFilters()
            .Where(e => e.Type == EmailEventTypes.Queued)
            .ToListAsync();

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(queued[0].Tags)!;
        tags["txn_id"].Should().Be(rows[0].Id.ToString(),
            "platform-outbox row id IS the transaction id emitted in the QUEUED event");
    }
}
