using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.Core;

/// <summary>
/// Unit coverage for the durable DCB-event drain (<see cref="EventDrain"/>)
/// and the <see cref="TammaEvent"/> → wire projection
/// (<see cref="EventPersistenceMiddleware.ToWireRecord"/>).
///
/// <para>These exercise the cursor / retry / dedup semantics against a fake
/// flush delegate — no Elsa runtime, no network. This is the audit-trail
/// safety net: a flush failure must NOT advance the cursor or drop events.</para>
/// </summary>
[TestFixture]
public class EventDrainTests
{
    [Test]
    public async Task FlushAsync_SendsNewEvents_AndAdvancesCursor()
    {
        var props = NewProps(Event("A"), Event("B"));
        var captured = new List<IReadOnlyList<TammaEvent>>();

        var sent = await EventDrain.FlushAsync(props, pending =>
        {
            captured.Add(pending);
            return Task.FromResult(true);
        });

        sent.Should().Be(2);
        captured.Should().HaveCount(1);
        captured[0].Select(e => e.EventType).Should().BeEquivalentTo(new[] { "A", "B" });
        props[EventDrain.CursorKey].Should().Be(2);
    }

    [Test]
    public async Task FlushAsync_Cursor_PreventsResendingPersistedEvents()
    {
        var list = new List<TammaEvent> { Event("A"), Event("B") };
        var props = new Dictionary<object, object> { [EventDrain.EventsKey] = list };
        var batches = new List<List<string>>();

        Task<bool> Flush(IReadOnlyList<TammaEvent> p)
        {
            batches.Add(p.Select(e => e.EventType).ToList());
            return Task.FromResult(true);
        }

        // First flush sends A, B.
        await EventDrain.FlushAsync(props, Flush);
        // A new event is emitted after the first flush.
        list.Add(Event("C"));
        // Second flush sends ONLY C — A and B are not re-sent.
        var sent2 = await EventDrain.FlushAsync(props, Flush);

        sent2.Should().Be(1);
        batches.Should().HaveCount(2);
        batches[0].Should().BeEquivalentTo(new[] { "A", "B" });
        batches[1].Should().BeEquivalentTo(new[] { "C" });
        props[EventDrain.CursorKey].Should().Be(3);
    }

    [Test]
    public async Task FlushAsync_OnFailure_DoesNotAdvanceCursor_AndRetriesNextTime()
    {
        var props = NewProps(Event("A"), Event("B"));
        var attempts = new List<List<string>>();
        var failFirst = true;

        Task<bool> Flush(IReadOnlyList<TammaEvent> p)
        {
            attempts.Add(p.Select(e => e.EventType).ToList());
            if (failFirst) { failFirst = false; return Task.FromResult(false); }
            return Task.FromResult(true);
        }

        var errors = new List<int>();
        var sent1 = await EventDrain.FlushAsync(props, Flush, onError: (c, _) => errors.Add(c));

        sent1.Should().Be(0, "a failed flush persists nothing");
        props.ContainsKey(EventDrain.CursorKey).Should().BeFalse("cursor must not advance on failure");
        errors.Should().ContainSingle().Which.Should().Be(2);

        // Retry — the SAME events are re-sent and now succeed.
        var sent2 = await EventDrain.FlushAsync(props, Flush);

        sent2.Should().Be(2);
        attempts.Should().HaveCount(2);
        attempts[0].Should().BeEquivalentTo(new[] { "A", "B" });
        attempts[1].Should().BeEquivalentTo(new[] { "A", "B" }, "the failed batch is retried in full");
        props[EventDrain.CursorKey].Should().Be(2);
    }

    [Test]
    public async Task FlushAsync_WhenFlushThrows_TreatsAsFailure_CursorUnchanged_NeverThrows()
    {
        var props = NewProps(Event("A"));
        var errors = new List<(int Count, Exception? Ex)>();

        var sent = await EventDrain.FlushAsync(
            props,
            _ => throw new InvalidOperationException("api down"),
            onError: (c, ex) => errors.Add((c, ex)));

        sent.Should().Be(0);
        props.ContainsKey(EventDrain.CursorKey).Should().BeFalse();
        errors.Should().ContainSingle();
        errors[0].Count.Should().Be(1);
        errors[0].Ex.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public async Task FlushAsync_NoNewEvents_IsNoOp()
    {
        var props = NewProps(Event("A"));
        props[EventDrain.CursorKey] = 1; // already flushed.
        var called = false;

        var sent = await EventDrain.FlushAsync(props, _ => { called = true; return Task.FromResult(true); });

        sent.Should().Be(0);
        called.Should().BeFalse();
    }

    [Test]
    public async Task FlushAsync_EmptyOrMissingList_IsNoOp()
    {
        var empty = new Dictionary<object, object>();
        (await EventDrain.FlushAsync(empty, _ => Task.FromResult(true))).Should().Be(0);

        var emptyList = new Dictionary<object, object>
        {
            [EventDrain.EventsKey] = new List<TammaEvent>(),
        };
        (await EventDrain.FlushAsync(emptyList, _ => Task.FromResult(true))).Should().Be(0);
    }

    [Test]
    public void ToWireRecord_ProjectsAllFields_FromTammaEvent()
    {
        var evt = new TammaEvent
        {
            EventType = "CODE.GENERATED.SUCCESS",
            Status = "success",
            Error = null,
            Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Duration = TimeSpan.FromMilliseconds(150),
            ActivityId = "act-7",
            ActivityName = "GenerateCode",
            WorkflowInstanceId = "wf-9",
            Data = new Dictionary<string, object?> { ["filesChanged"] = 3, ["issueNumber"] = 42 },
            Tags = new Dictionary<string, object?> { ["provider"] = "anthropic" },
        };

        var wire = EventPersistenceMiddleware.ToWireRecord(evt);

        wire.EventType.Should().Be("CODE.GENERATED.SUCCESS");
        wire.Status.Should().Be("success");
        wire.DurationMs.Should().Be(150);
        wire.ActivityId.Should().Be("act-7");
        wire.ActivityName.Should().Be("GenerateCode");
        wire.WorkflowInstanceId.Should().Be("wf-9");
        wire.IssueNumber.Should().Be(42, "issueNumber is lifted out of Data for the column");
        wire.Tags!["provider"].Should().Be("anthropic");
        wire.Data.Should().NotBeNull();
        wire.Data!.Value.GetProperty("filesChanged").GetInt32().Should().Be(3);
    }

    [Test]
    public async Task RoundTrip_EmittedEvents_ReachTheAppendChannel_ViaClient()
    {
        // Emit-shaped events sitting in the transient list (the same
        // List<TammaEvent> TammaEventEmitter populates on a TammaActivity run).
        var props = NewProps(
            EventWithData("ADL.CONFIG.INIT.STARTED", new() { ["repo"] = "tamma" }),
            EventWithData("ADL.CONFIG.INIT.COMPLETED", new() { ["issueNumber"] = 7 }));

        var handler = new RecordingHandler();
        var client = new Tamma.Activities.LlmCall.TammaApiClient(
            new HttpClient(handler) { BaseAddress = null },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Tamma.Activities.LlmCall.TammaApiClient>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Tamma:ApiUrl"] = "http://tamma.test" })
                .Build());

        var tenantId = Guid.NewGuid();
        var sent = await EventDrain.FlushAsync(
            props,
            pending => client.AppendEventsAsync(
                pending.Select(EventPersistenceMiddleware.ToWireRecord).ToList(), tenantId));

        sent.Should().Be(2);
        handler.LastBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        var types = body.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("eventType").GetString()).ToList();
        types.Should().BeEquivalentTo(new[] { "ADL.CONFIG.INIT.STARTED", "ADL.CONFIG.INIT.COMPLETED" });

        // issueNumber lifted from Data is on the wire record's column field.
        var completed = body.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("eventType").GetString() == "ADL.CONFIG.INIT.COMPLETED");
        completed.GetProperty("issueNumber").GetInt32().Should().Be(7);
        handler.LastTenantHeader.Should().Be(tenantId.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastTenantHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastTenantHeader = request.Headers.TryGetValues("X-Tenant-Id", out var v) ? v.FirstOrDefault() : null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent("{\"ok\":true,\"persisted\":2}"),
            };
        }
    }

    private static TammaEvent EventWithData(string type, Dictionary<string, object?> data) => new()
    {
        EventType = type,
        Status = "success",
        WorkflowInstanceId = "wf-1",
        Data = data,
    };

    private static Dictionary<object, object> NewProps(params TammaEvent[] events) =>
        new() { [EventDrain.EventsKey] = new List<TammaEvent>(events) };

    private static TammaEvent Event(string type) => new()
    {
        EventType = type,
        Status = "success",
        WorkflowInstanceId = "wf-1",
    };
}
