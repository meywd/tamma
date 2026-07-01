using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Notifications;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Core.Interfaces;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Notifications;

/// <summary>
/// Story 38-3 (AC4/AC7) — exercises the out-of-band <see cref="OutboxSlackSender"/>:
/// the sole webhook-credential holder drains <c>slack_outbox</c>, performs the post
/// via <see cref="ISlackIntegrationService"/>, marks the row, and audits terminal
/// outcomes to <c>platform_events</c>. Also asserts the credential-safety contract:
/// the webhook URL never lands in the row / event / (implicitly) the log.
/// </summary>
[TestFixture]
public class OutboxSlackSenderTests
{
    private const string Webhook = "https://hooks.slack.com/services/T000/B000/XXXSUPERSECRET";

    private DbContextOptions<ControlPlaneDbContext> _cpOptions = null!;
    private IConfiguration _config = null!;

    [SetUp]
    public void SetUp()
    {
        _cpOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase("slack-outbox-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:WebhookUrl"] = Webhook,
            })
            .Build();
    }

    private (ServiceProvider sp, OutboxSlackSender sender, FakeSlack slack) BuildSender()
    {
        var captured = _cpOptions;
        var services = new ServiceCollection();
        services.AddScoped<ControlPlaneDbContext>(_ => new TestControlPlaneDbContext(captured));
        services.AddScoped<ISlackOutboxRepository, SlackOutboxRepository>();
        services.AddScoped<IPlatformEventRepository, PlatformEventRepository>();
        var slack = new FakeSlack();
        services.AddSingleton<ISlackIntegrationService>(slack);

        var sp = services.BuildServiceProvider();
        var sender = new OutboxSlackSender(
            sp,
            new OutboxSlackSenderOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                BackoffSchedule = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            },
            _config,
            NullLogger<OutboxSlackSender>.Instance);
        return (sp, sender, slack);
    }

    private async Task<SlackOutboxMessage> SeedAsync(SlackOutboxMessage row)
    {
        using var cp = new TestControlPlaneDbContext(_cpOptions);
        var repo = new SlackOutboxRepository(cp);
        return await repo.EnqueueAsync(row);
    }

    private static SlackOutboxMessage ChannelRow(int maxAttempts = 5) => new()
    {
        Channel = "eng-updates",
        MessageType = "Info",
        Body = ":information_source: build green",
        MaxAttempts = maxAttempts,
    };

    private static SlackOutboxMessage DmRow() => new()
    {
        TargetUserId = "U123",
        MessageType = "Warning",
        Body = ":warning: heads up",
        MaxAttempts = 5,
    };

    // ── Happy path (AC4/AC7) ──────────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_ChannelRow_PostsToChannel_MarksSent_EmitsSentEvent()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            var enq = await SeedAsync(ChannelRow());

            var processed = await sender.ProcessOnceAsync(CancellationToken.None);

            processed.Should().BeTrue();
            slack.ChannelCalls.Should().ContainSingle();
            slack.ChannelCalls[0].channel.Should().Be("eng-updates");
            slack.ChannelCalls[0].message.Should().Be(":information_source: build green");
            slack.DmCalls.Should().BeEmpty();

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            var row = await verify.SlackOutbox.FindAsync(enq.Id);
            row.Should().BeNull("the delivered row is purged — the durable audit lives in platform_events");

            var events = await verify.PlatformEvents
                .Where(e => e.Type == NotificationSlackEventTypes.Sent)
                .ToListAsync();
            events.Should().ContainSingle();
            var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(events[0].Tags)!;
            tags["outbox_id"].Should().Be(enq.Id.ToString());
            tags["scope"].Should().Be("platform");
            tags["channel"].Should().Be("eng-updates");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_DmRow_PostsDirectMessage_MarksSent()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            var enq = await SeedAsync(DmRow());

            await sender.ProcessOnceAsync(CancellationToken.None);

            slack.DmCalls.Should().ContainSingle();
            slack.DmCalls[0].userId.Should().Be("U123");
            slack.ChannelCalls.Should().BeEmpty();

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            (await verify.SlackOutbox.FindAsync(enq.Id))
                .Should().BeNull("the delivered row is purged");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_NoPending_ReturnsFalse()
    {
        var (sp, sender, _) = BuildSender();
        try
        {
            (await sender.ProcessOnceAsync(CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Failure / backoff (AC4) ───────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_TransientFailure_RequeuesWithBackoff_EmitsFailedEvent()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            slack.ChannelResult = IntegrationResult<bool>.Fail("slack returned 500");
            var enq = await SeedAsync(ChannelRow(maxAttempts: 5));

            var before = DateTime.UtcNow;
            await sender.ProcessOnceAsync(CancellationToken.None);

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            var row = await verify.SlackOutbox.FindAsync(enq.Id);
            row!.Status.Should().Be("pending", "under the ceiling the row is re-queued");
            row.Attempts.Should().Be(1);
            row.NextAttemptAt.Should().BeAfter(before, "the retry is backed off into the future");
            row.LastError.Should().Contain("500");

            var failed = await verify.PlatformEvents
                .Where(e => e.Type == NotificationSlackEventTypes.Failed)
                .ToListAsync();
            failed.Should().ContainSingle();
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    [Test]
    public async Task ProcessOnceAsync_TerminalFailure_RemovesRow_EmitsTerminalFailedEvent()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            slack.ChannelResult = IntegrationResult<bool>.Fail("slack unreachable");
            var enq = await SeedAsync(ChannelRow(maxAttempts: 1));

            await sender.ProcessOnceAsync(CancellationToken.None);

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            (await verify.SlackOutbox.FindAsync(enq.Id))
                .Should().BeNull("the terminally-failed row is purged — the audit lives in platform_events");

            var failed = await verify.PlatformEvents
                .Where(e => e.Type == NotificationSlackEventTypes.Failed)
                .ToListAsync();
            failed.Should().ContainSingle();
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(failed[0].Data)!;
            data["terminal"].GetBoolean().Should().BeTrue();
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Split-leg independence (FIX 1) ────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_SplitLegs_ChannelFailRetries_WithoutTouchingDeliveredDmRow()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            // The channel leg fails; the DM leg succeeds. Each is a SEPARATE row (split
            // at enqueue), so the channel retry must not re-send — or resurrect — the DM.
            slack.ChannelResult = IntegrationResult<bool>.Fail("channel 500");

            var chan = await SeedAsync(new SlackOutboxMessage
            {
                Channel = "eng-updates",
                MessageType = "Info",
                Body = ":information_source: note",
                MaxAttempts = 5,
                NextAttemptAt = DateTime.UtcNow.AddSeconds(-10), // claimed first
            });
            var dm = await SeedAsync(new SlackOutboxMessage
            {
                TargetUserId = "U9",
                MessageType = "Info",
                Body = ":information_source: note",
                MaxAttempts = 5,
                NextAttemptAt = DateTime.UtcNow,
            });

            // Poll 1 → channel leg fails and re-queues; Poll 2 → DM leg claimed on its own.
            (await sender.ProcessOnceAsync(CancellationToken.None)).Should().BeTrue();
            (await sender.ProcessOnceAsync(CancellationToken.None)).Should().BeTrue();

            slack.ChannelCalls.Should().ContainSingle("only the channel leg is (re)posted");
            slack.DmCalls.Should().ContainSingle("the DM leg is delivered exactly once");
            slack.DmCalls[0].userId.Should().Be("U9");

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            var chanRow = await verify.SlackOutbox.FindAsync(chan.Id);
            chanRow!.Status.Should().Be("pending", "the channel leg re-queues independently");
            chanRow.Attempts.Should().Be(1);
            (await verify.SlackOutbox.FindAsync(dm.Id))
                .Should().BeNull("the delivered DM leg is purged, untouched by the channel retry");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Credential safety (AC7) ───────────────────────────────────────────────

    [Test]
    public async Task ProcessOnceAsync_Failure_RedactsWebhookFromRowAndEvent()
    {
        var (sp, sender, slack) = BuildSender();
        try
        {
            // The transport error leaks the webhook URL — the sender must redact it
            // before it reaches the row, the event, or the log.
            slack.ChannelResult = IntegrationResult<bool>.Fail($"POST {Webhook} failed: connection reset");
            var enq = await SeedAsync(ChannelRow(maxAttempts: 5));

            await sender.ProcessOnceAsync(CancellationToken.None);

            using var verify = new TestControlPlaneDbContext(_cpOptions);
            var row = await verify.SlackOutbox.FindAsync(enq.Id);
            row!.LastError.Should().NotContain(Webhook, "the webhook secret must never be stored on the row");
            row.LastError.Should().Contain("[redacted-webhook]");

            var failed = await verify.PlatformEvents
                .Where(e => e.Type == NotificationSlackEventTypes.Failed)
                .ToListAsync();
            var payload = failed[0].Tags + failed[0].Data + failed[0].Metadata;
            payload.Should().NotContain(Webhook, "no event payload may carry the webhook secret");
        }
        finally
        {
            sender.Dispose();
            sp.Dispose();
        }
    }

    // ── Test double ───────────────────────────────────────────────────────────

    private sealed class FakeSlack : ISlackIntegrationService
    {
        public List<(string channel, string message)> ChannelCalls { get; } = new();
        public List<(string userId, string message)> DmCalls { get; } = new();
        public IntegrationResult<bool> ChannelResult { get; set; } = IntegrationResult<bool>.Ok(true);
        public IntegrationResult<bool> DmResult { get; set; } = IntegrationResult<bool>.Ok(true);

        public Task<IntegrationResult<bool>> SendSlackMessageAsync(string channel, string message)
        {
            ChannelCalls.Add((channel, message));
            return Task.FromResult(ChannelResult);
        }

        public Task<IntegrationResult<bool>> SendSlackDirectMessageAsync(string userId, string message)
        {
            DmCalls.Add((userId, message));
            return Task.FromResult(DmResult);
        }
    }
}
