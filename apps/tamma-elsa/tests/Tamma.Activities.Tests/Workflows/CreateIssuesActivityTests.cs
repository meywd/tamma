using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 40-8 (AC1 + AC3 core) — unit tests over
/// <see cref="CreateIssuesActivity.CreateIssuesCoreAsync"/> with a scripted
/// <see cref="IIssueCreateClient"/> (the <c>ApplyCoreAsync</c> seam-testing pattern).
///
/// <para>Fail-first record (2026-08-03): the double-create, malformed/empty-input,
/// duplicate-title, pre-existing-title, invalid-item, and dedupe-degradation pins were
/// all run RED against the deliberate first cut of the core (throwing parse, no
/// platform-side dedupe, no warnings) before D3/D4's tolerant parse + dedupe landed.</para>
/// </summary>
[TestFixture]
public class CreateIssuesActivityTests
{
    private const string Repo = "owner/repo";

    // ── AC3 — idempotent re-run (the load-bearing pin) ──────────────────────

    [Test]
    public async Task ReRunAfterPartialFailure_DoesNotDoubleCreate()
    {
        // Run 1: items 3..5 fail (the crash-mid-burst shape — the platform durably
        // holds run 1's 2 creations). Run 2 re-sends the SAME input with a healthy
        // client. Without the platform-side pre-list (D3), run 2 re-creates items
        // 1..2 → 7 successful creates for 5 titles. With it: exactly 5, each once.
        var input = DraftsJson("t1", "t2", "t3", "t4", "t5");
        var client = new ScriptedIssueCreateClient
        {
            FailWith = title => title is "t3" or "t4" or "t5" ? 500 : 0,
        };

        var run1 = await CreateIssuesActivity.CreateIssuesCoreAsync(client, Repo, input);
        run1.CreatedCount.Should().Be(2);
        run1.FailedCount.Should().Be(3);

        client.FailWith = null; // healthy again
        var run2 = await CreateIssuesActivity.CreateIssuesCoreAsync(client, Repo, input);

        var createdTitles = client.Created.Select(c => c.Title).ToList();
        createdTitles.Should().HaveCount(5,
            "across a partial run + a full re-run of the same input, the platform must hold the " +
            "input set EXACTLY ONCE (40-8 AC3 — the platform is the durable record, D3)");
        createdTitles.Should().OnlyHaveUniqueItems("no title may be created twice");
        run2.CreatedCount.Should().Be(3, "run 2 creates only the 3 items run 1 failed");
        run2.SkippedCount.Should().Be(2, "run 1's 2 creations are skipped as already-existing");
    }

    [Test]
    public async Task PreExistingTitle_IsSkipped_NotDoubleCreated()
    {
        var client = new ScriptedIssueCreateClient();
        client.Existing.Add(new ExistingIssueRef(7, "t1", "open"));

        var events = new List<(string Type, string Status)>();
        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("t1", "t2"),
            emitItemEvent: (type, status, _, _) => events.Add((type, status)));

        result.CreatedCount.Should().Be(1);
        result.SkippedCount.Should().Be(1, "an existing exact-title issue suppresses the create (D3, pinned limitation)");
        client.Created.Select(c => c.Title).Should().Equal("t2");
        events.Should().Contain(e => e.Type == IssuesCreateEvents.ItemSkipped,
            "a skip is recorded loudly, never silent");
    }

    [Test]
    public async Task DuplicateTitlesInInput_CollapseWithWarning()
    {
        var client = new ScriptedIssueCreateClient();
        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("same", "same", "other"));

        result.CreatedCount.Should().Be(2, "duplicate titles inside one input collapse to one issue (D3, pinned limitation)");
        result.SkippedCount.Should().Be(1);
        result.Warnings.Should().NotBeEmpty("the collapse must be warned, not silent");
        client.Created.Select(c => c.Title).Should().OnlyHaveUniqueItems();
    }

    // ── AC1 — never a fault: tolerant parse ─────────────────────────────────

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{}")]
    [TestCase(null)]
    public async Task MalformedJson_CompletesWithZero_AndWarns(string? issuesJson)
    {
        var client = new ScriptedIssueCreateClient();
        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(client, Repo, issuesJson);

        result.CreatedCount.Should().Be(0);
        result.FailedCount.Should().Be(0, "malformed input is a warning, not a failure outcome");
        result.Warnings.Should().NotBeEmpty("AC1: malformed issuesJson completes with a recorded warning");
        client.CreateCalls.Should().Be(0);
    }

    [Test]
    public async Task EmptyArray_CompletesWithZero_AndWarns()
    {
        var client = new ScriptedIssueCreateClient();
        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(client, Repo, "[]");

        result.CreatedCount.Should().Be(0);
        result.Warnings.Should().NotBeEmpty("AC1: an empty array completes with zero creations and a recorded warning");
        client.CreateCalls.Should().Be(0);
    }

    [Test]
    public async Task InvalidItems_AreSkippedWithWarning_ValidOnesStillCreate()
    {
        // Non-object entries and drafts without a usable title must not throw and
        // must not block the valid drafts.
        const string mixed = """[1, "loose", {"noTitle": true}, {"title": ""}, {"title": "ok", "body": "b"}]""";
        var client = new ScriptedIssueCreateClient();

        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(client, Repo, mixed);

        result.CreatedCount.Should().Be(1);
        result.SkippedCount.Should().Be(4, "every invalid entry is recorded as skipped");
        result.Warnings.Should().NotBeEmpty();
        client.Created.Select(c => c.Title).Should().Equal("ok");
    }

    // ── AC1 — happy path + per-item failure behaviour ───────────────────────

    [Test]
    public async Task CreatesOneIssuePerItem_AndReturnsNumbers()
    {
        var client = new ScriptedIssueCreateClient();
        var events = new List<(string Type, string Status)>();

        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, """[{"title":"a","body":"ba","labels":["deferred"]},{"title":"b"},{"title":"c"}]""",
            emitItemEvent: (type, status, _, _) => events.Add((type, status)));

        result.CreatedCount.Should().Be(3);
        result.FailedCount.Should().Be(0);
        client.CreateCalls.Should().Be(3, "one mediated create per array item");
        result.IssueNumbers.Should().HaveCount(3, "AC1: the result carries the created issue numbers");
        result.IssueNumbers.Should().OnlyHaveUniqueItems();
        events.Count(e => e.Type == IssuesCreateEvents.ItemSuccess).Should().Be(3,
            "AC5: one event per created item");
        client.Created[0].Labels.Should().Contain("deferred", "labels ride the create");
    }

    [Test]
    public async Task PerItemFailure_EmitsLoudFailedEvent_AndContinues()
    {
        var client = new ScriptedIssueCreateClient { FailWith = t => t == "bad" ? 502 : 0 };
        var events = new List<(string Type, string? Error)>();

        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("good1", "bad", "good2"),
            emitItemEvent: (type, _, error, _) => events.Add((type, error)));

        result.CreatedCount.Should().Be(2, "a per-item failure must not stop the batch");
        result.FailedCount.Should().Be(1);
        events.Should().Contain(e => e.Type == IssuesCreateEvents.ItemFailed && e.Error!.Contains("502"),
            "the per-item failure is loud and carries the status code");
    }

    [Test]
    public async Task ClientThrow_IsCountedAsItemFailure_NeverEscapes()
    {
        var client = new ScriptedIssueCreateClient { ThrowOnCreate = t => t == "boom" };

        var act = async () => await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("ok", "boom"));

        var result = (await act.Should().NotThrowAsync(
            "D4: the core never throws — the parent cycle has no failure edge")).Subject;
        result.CreatedCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
    }

    // ── D3 — dedupe-read robustness ─────────────────────────────────────────

    [Test]
    public async Task DedupeListFailure_DegradesToWithinRunDedupe_WithWarning()
    {
        var client = new ScriptedIssueCreateClient { ThrowOnList = true };

        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("t1", "t1", "t2"));

        result.CreatedCount.Should().Be(2, "a failed dedupe read must not block the creates");
        result.SkippedCount.Should().Be(1, "within-run dedupe still holds");
        result.Warnings.Should().Contain(w => w.Contains("dedupe", StringComparison.OrdinalIgnoreCase),
            "degraded dedupe must be recorded loudly");
    }

    [Test]
    public async Task DedupeListTruncation_RecordsWarning()
    {
        var client = new ScriptedIssueCreateClient();
        for (var i = 0; i < CreateIssuesActivity.DedupePageSize; i++)
            client.Existing.Add(new ExistingIssueRef(i + 1, $"existing-{i}", "open"));

        var result = await CreateIssuesActivity.CreateIssuesCoreAsync(
            client, Repo, DraftsJson("new-title"), maxDedupePages: 1);

        result.CreatedCount.Should().Be(1);
        result.Warnings.Should().Contain(w => w.Contains("truncat", StringComparison.OrdinalIgnoreCase),
            "hitting the dedupe page cap degrades dedupe to within-run only and must be warned");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string DraftsJson(params string[] titles) =>
        "[" + string.Join(",", titles.Select(t => $$"""{"title":"{{t}}","body":"body of {{t}}"}""")) + "]";

    private sealed class ScriptedIssueCreateClient : IIssueCreateClient
    {
        private int _nextNumber = 100;

        public List<(string Title, string Body, IReadOnlyList<string> Labels)> Created { get; } = new();
        public List<ExistingIssueRef> Existing { get; } = new();
        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public Func<string, int>? FailWith { get; set; }
        public Func<string, bool>? ThrowOnCreate { get; set; }
        public bool ThrowOnList { get; set; }

        public Task<IssueCreateResult> CreateIssueAsync(
            string repository, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
        {
            CreateCalls++;
            if (ThrowOnCreate?.Invoke(title) == true)
                throw new HttpRequestException($"boom creating '{title}'");
            var status = FailWith?.Invoke(title) ?? 0;
            if (status != 0)
                return Task.FromResult(IssueCreateResult.Fail(status));

            var number = _nextNumber++;
            Created.Add((title, body, labels));
            Existing.Add(new ExistingIssueRef(number, title, "open"));
            return Task.FromResult(IssueCreateResult.Ok(number));
        }

        public Task<IReadOnlyList<ExistingIssueRef>> ListIssuesAsync(
            string repository, int page, int perPage, CancellationToken ct)
        {
            ListCalls++;
            if (ThrowOnList)
                throw new HttpRequestException("list failed");
            IReadOnlyList<ExistingIssueRef> slice =
                Existing.Skip((page - 1) * perPage).Take(perPage).ToList();
            return Task.FromResult(slice);
        }
    }
}
