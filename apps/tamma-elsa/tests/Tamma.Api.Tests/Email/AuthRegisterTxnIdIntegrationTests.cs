using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Email;
using Tamma.Data;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// End-to-end validation of the new event-sourced, transaction-id-correlated
/// email pipeline. Registers a user against the real HTTP + Postgres stack
/// configured for SMTP provider mode (outbox path) and asserts:
/// <list type="bullet">
///   <item><description>A <c>EMAIL.QUEUED.SUCCESS</c> row appears in
///     <c>domain_events</c> with a <c>txn_id</c> tag.</description></item>
///   <item><description>An <c>email_outbox</c> row exists whose <c>Id</c>
///     equals that <c>txn_id</c> — proving end-to-end correlation through
///     the pipeline.</description></item>
/// </list>
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

        // Give any scoped async work a moment to flush to Postgres.
        await Task.Delay(50);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var queued = await db.DomainEvents
            .IgnoreQueryFilters()
            .Where(e => e.Type == EmailEventTypes.Queued)
            .ToListAsync();

        queued.Should().ContainSingle(
            "registration must enqueue exactly one email and emit exactly one QUEUED event");

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(queued[0].Tags)!;
        var txnIdStr = tags["txn_id"];
        txnIdStr.Should().NotBeNullOrEmpty();
        tags["template"].Should().Be("verification");

        // The txn_id on the event MUST equal the outbox row id — that is the
        // end-to-end correlation contract the pipeline promises.
        var outboxRows = await db.EmailOutbox.ToListAsync();
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
        await Task.Delay(50);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();

        var rows = await db.EmailOutbox.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be("pending");
        rows[0].Template.Should().Be("verification");
        rows[0].ToAddress.Should().Be("outbox-row@example.com",
            "the OUTBOX is the one place we DO persist the recipient");

        var queued = await db.DomainEvents
            .IgnoreQueryFilters()
            .Where(e => e.Type == EmailEventTypes.Queued)
            .ToListAsync();

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(queued[0].Tags)!;
        tags["txn_id"].Should().Be(rows[0].Id.ToString(),
            "outbox row id IS the transaction id emitted in the QUEUED event");
    }
}
