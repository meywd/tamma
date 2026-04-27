using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;
using Tamma.Data;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// M15 — integration-flavoured tests for the SSE poll loop's
/// consecutive-error gate. Uses an in-memory CP context where the
/// initial high-water-mark read succeeds, then a throwing factory so
/// every subsequent tick fails. After
/// <see cref="AdminTenantEventsSseEndpoint.MaxConsecutiveErrors"/>
/// failures the stream must emit <c>event: end</c> +
/// <c>data: {"reason":"upstream_error"}</c> and break.
///
/// <para>Avoids the full HTTP host so the test stays fast + isolated.
/// We pump the static endpoint with a hand-rolled <see cref="HttpContext"/>
/// and read the resulting body to assert the wire shape.</para>
/// </summary>
[TestFixture]
public class AdminTenantEventsSseLoopTests
{
    [Test]
    public async Task PollLoop_FailsClosedAfter_MaxConsecutiveErrors()
    {
        var tenantId = Guid.NewGuid();
        var http = BuildContext();

        // Factory pattern: first call (cursor read) succeeds, every
        // subsequent call throws. We can't easily inject mid-stream
        // behaviour through IDbContextFactory<T> directly, so we use a
        // wrapping factory that becomes throwing after N successful
        // creations. The cursor read counts as 1; the loop ticks count
        // as the rest.
        var factory = new ThrowingAfterFactory(_ => true, throwAfterCount: 1);

        var jsonOpts = Options.Create(new JsonOptions());
        var loggerFactory = new NullLoggerFactory();

        // Cancel after a generous window so a runaway test doesn't
        // hang the suite. The poll loop is on a 2-second cadence; 5
        // failures × 2s = ~10s plus overhead. 60s is well above that.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        http.RequestAborted = cts.Token;

        await AdminTenantEventsSseEndpoint.StreamEvents(
            tenantId, factory, jsonOpts, loggerFactory, TimeProvider.System, http, cts.Token);

        var body = ReadBody(http);
        body.Should().Contain("event: end",
            "M15 — stream must close cleanly after consecutive errors exceed the cap");
        body.Should().Contain("\"reason\":\"upstream_error\"",
            "M15 — close reason must be the upstream-error label per the spec");
        // Five consecutive errors emitted as `: error` comments before
        // the terminal end frame. Each one carries the (n/5) progress.
        body.Should().Contain("(5/5)");
    }

    [Test]
    public async Task PollLoop_TenantEmpty_Returns400_AndNeverEnters()
    {
        var http = BuildContext();
        var factory = new ThrowingAfterFactory(_ => false, throwAfterCount: 999);
        var jsonOpts = Options.Create(new JsonOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        http.RequestAborted = cts.Token;

        await AdminTenantEventsSseEndpoint.StreamEvents(
            Guid.Empty, factory, jsonOpts, new NullLoggerFactory(), TimeProvider.System, http, cts.Token);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext BuildContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// EF context factory that hands out InMemory contexts the first
    /// <c>throwAfterCount</c> times, then throws. Lets the test simulate
    /// a CP outage that begins after the SSE stream successfully reads
    /// the initial cursor.
    /// </summary>
    private sealed class ThrowingAfterFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly Func<int, bool> _seedRows;
        private readonly int _throwAfter;
        private int _calls;
        private readonly string _dbName;

        public ThrowingAfterFactory(Func<int, bool> seedRows, int throwAfterCount)
        {
            _seedRows = seedRows;
            _throwAfter = throwAfterCount;
            _dbName = $"sse-loop-{Guid.NewGuid():N}";
        }

        public ControlPlaneDbContext CreateDbContext()
            => CreateInternal();

        public Task<ControlPlaneDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateInternal());
        }

        private ControlPlaneDbContext CreateInternal()
        {
            _calls++;
            if (_calls > _throwAfter)
                throw new InvalidOperationException("simulated CP outage");
            var opts = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new ControlPlaneDbContext(opts);
        }
    }
}
