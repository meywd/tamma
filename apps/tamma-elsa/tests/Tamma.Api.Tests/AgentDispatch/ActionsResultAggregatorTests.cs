using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.AgentDispatch;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 (AC4) / Epic 31 P3 (seam 6) — the server-side collect aggregation,
/// now over the resolved platform driver. Exercises the multi-read merge
/// (artifact / PR / branch file-changes / head SHA) against the platform fakes,
/// and the §4 skip-with-audit posture for typed <c>capability_unsupported</c>
/// sub-reads.
/// </summary>
[TestFixture]
public class ActionsResultAggregatorTests
{
    private const string Owner = "acme";
    private const string Name = "widgets";
    private const long RunId = 99;
    private readonly Guid _tenant = Guid.NewGuid();

    private FakePlatformActionsClient _actions = null!;
    private Mock<IGitPlatformClient> _client = null!;
    private RecordingEventRepository _events = null!;

    [SetUp]
    public void SetUp()
    {
        _actions = new FakePlatformActionsClient();
        _client = new Mock<IGitPlatformClient>(MockBehavior.Loose);
        _events = new RecordingEventRepository();

        // Loose-mock defaults answer null PlatformResults; make the git reads
        // answer typed empties so the merge logic (not mock plumbing) decides.
        _client
            .Setup(c => c.ListOpenPullRequestsForBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PullRequest>>.FromOk(Array.Empty<PullRequest>()));
        _client
            .Setup(c => c.ListBranchFileChangesAsync(It.IsAny<ListBranchFileChangesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PrFile>>.FromOk(Array.Empty<PrFile>()));
        _client
            .Setup(c => c.GetBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Branch>.FromError(new PlatformError.NotFound()));
    }

    private ActionsResultAggregator Sut() => new(_events);

    private FakePlatformDriver Driver(bool withActions = true) =>
        new(_client.Object, withActions ? _actions : null);

    private static CollectAgentRunRequest Request(string conclusion = "success") => new()
    {
        BranchName = "tamma/issue-42",
        Conclusion = conclusion,
        AgentProvider = "claude-code",
        DurationSeconds = 123,
        CorrelationId = "sess_abc",
    };

    private static byte[] BuildArtifactZip(string resultJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("result.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(resultJson);
        }
        return ms.ToArray();
    }

    private void SeedResultArtifact(string resultJson)
    {
        _actions.OnListArtifacts = _ => PlatformResult<IReadOnlyList<Artifact>>.FromOk(
            new[] { new Artifact("500", "tamma-result", 100, "https://ci/artifact/500") });
        _actions.ArtifactBytes["500"] = BuildArtifactZip(resultJson);
    }

    private static PullRequest Pr(int number, string url) => new(
        number.ToString(), "PR", null, "tamma/issue-42", "main",
        PullRequestState.Open, false, url, "bot", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Test]
    public async Task Aggregate_UsesArtifactWhenAvailable()
    {
        SeedResultArtifact(@"{
            ""success"": true, ""task"": ""implement"", ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"", ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [""a.ts""], ""pr_number"": 7, ""commit_sha"": ""abc"",
            ""tokens_used"": 1000, ""duration_seconds"": 60, ""agent_provider"": ""claude-code""
        }");

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.Success.Should().BeTrue("mediation succeeded");
        result.AgentSuccess.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        result.CommitSha.Should().Be("abc");
        result.TokensUsed.Should().Be(1000);
        result.FilesChanged.Should().ContainSingle().Which.Should().Be("a.ts");
        result.CredentialSource.Should().Be("installation");
        _events.Appended.Should().BeEmpty("nothing was skipped — no audit event");
    }

    [Test]
    public async Task Aggregate_FallsBackToGitState_WhenArtifactMissing()
    {
        // Post-swap fallback: FilesChanged from the branch file-change read,
        // CommitSha from the branch tip. (The pre-swap base...head compare —
        // and its commit COUNT — did not survive the platform abstraction;
        // CommitsCount is 0 = "not computed".)
        _client
            .Setup(c => c.ListBranchFileChangesAsync(It.IsAny<ListBranchFileChangesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PrFile>>.FromOk(new[]
            {
                new PrFile("src/x.ts", PrFileStatus.Modified, 5, 3),
                new PrFile("src/y.ts", PrFileStatus.Added, 20, 0),
            }));
        _client
            .Setup(c => c.GetBranchAsync(Owner, Name, "tamma/issue-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<Branch>.FromOk(new Branch("tamma/issue-42", "head-sha", false)));

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request("failure"));

        result.AgentSuccess.Should().BeFalse();
        result.CommitSha.Should().Be("head-sha");
        result.FilesChanged.Should().HaveCount(2);
        result.ErrorMessage.Should().Contain("failure");
        result.ErrorMessage.Should().Contain("no result artifact");
    }

    [Test]
    public async Task Aggregate_IncludesPullRequestWhenFound()
    {
        _client
            .Setup(c => c.ListOpenPullRequestsForBranchAsync(
                Owner, Name, "tamma/issue-42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PullRequest>>.FromOk(new[]
            {
                Pr(11, "https://github.com/acme/widgets/pull/11"),
            }));

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.PrNumber.Should().Be(11);
        result.PrUrl.Should().Be("https://github.com/acme/widgets/pull/11");
    }

    [Test]
    public async Task Aggregate_ChecksPassed_IsUnknown_PostSwap()
    {
        // Check-run reads are platform-specific and not abstracted: null =
        // "unknown", the same value a still-pending check produced pre-swap.
        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.ChecksPassed.Should().BeNull();
    }

    [Test]
    public async Task Aggregate_IgnoresExpiredArtifacts()
    {
        // SizeBytes 0 + empty URL is the platform model's "expired" encoding.
        _actions.OnListArtifacts = _ => PlatformResult<IReadOnlyList<Artifact>>.FromOk(
            new[] { new Artifact("500", "tamma-result", 0, "") });

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.TokensUsed.Should().Be(0);
        result.AgentProvider.Should().Be("claude-code");
    }

    [Test]
    public async Task Aggregate_OversizedResultJsonInZip_IsRejected()
    {
        var hugePayload = new string('z', (int)(AgentResultArtifactParser.MaxResultJsonBytes + 1024));
        var json = $@"{{
            ""success"": true, ""task"": ""implement"", ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"", ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [], ""commit_sha"": ""abc123"", ""agent_log_summary"": ""{hugePayload}""
        }}";
        SeedResultArtifact(json);

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request("success"));

        result.AgentSuccess.Should().BeTrue("monitor said success; artifact was rejected but that doesn't flip it");
        result.TokensUsed.Should().Be(0, "artifact was rejected so no tokens carried over");
        result.AgentLogSummary.Should().BeNull("no artifact means no log summary");
    }

    // ===================================================================
    // §4 — capability_unsupported sub-reads SKIP WITH AUDIT, never throw
    // ===================================================================

    [Test]
    public async Task Aggregate_ArtifactListingUnsupported_SkipsWithOneAuditEvent_NeverThrows()
    {
        _actions.OnListArtifacts = _ => PlatformResult<IReadOnlyList<Artifact>>.FromError(
            new PlatformError.InvalidRequest(PlatformErrorText.CapabilityUnsupportedCode, "no artifact API"));

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.Success.Should().BeTrue("a capability-skipped source degrades the merge, never fails it");
        result.TokensUsed.Should().Be(0);

        var evt = _events.Appended.Should().ContainSingle().Subject;
        evt.Type.Should().Be(ActionsResultAggregator.CollectStepSkippedEventType);
        evt.Tags.Should().Contain("capability_unsupported").And.Contain("result_artifact");
        evt.TenantId.Should().Be(_tenant);
    }

    [Test]
    public async Task Aggregate_FileChangeReadUnsupported_SkipsWithAuditEvent()
    {
        _client
            .Setup(c => c.ListBranchFileChangesAsync(It.IsAny<ListBranchFileChangesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PlatformResult<IReadOnlyList<PrFile>>.FromError(
                new PlatformError.InvalidRequest(PlatformErrorText.CapabilityUnsupportedCode, "no commit reads")));

        var result = await Sut().AggregateAsync(Driver(), _tenant, Owner, Name, RunId, Request());

        result.Success.Should().BeTrue();
        result.FilesChanged.Should().BeEmpty();
        _events.Appended.Should().ContainSingle()
            .Which.Tags.Should().Contain("file_changes");
    }

    [Test]
    public async Task Aggregate_DriverWithoutActionsSurface_SkipsArtifactWithAuditEvent()
    {
        var result = await Sut().AggregateAsync(Driver(withActions: false), _tenant, Owner, Name, RunId, Request());

        result.Success.Should().BeTrue();
        _events.Appended.Should().ContainSingle()
            .Which.Tags.Should().Contain("result_artifact");
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public ConcurrentBag<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult(((IReadOnlyList<DomainEvent>)new List<DomainEvent>(), 0));
    }
}
