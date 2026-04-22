using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class LocalExecutorTests
{
    private string _workDir = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "tamma-local-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static AgentExecutionRequest MakeRequest() =>
        new(
            Repository: "acme/widgets",
            BranchName: "tamma/issue-42",
            IssueNumber: 42,
            IssueTitle: "Fix it",
            Task: "implement",
            PlanJson: "{\"x\":1}",
            SessionId: "sess_xyz",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 30);

    [Test]
    public void Mode_IsLocal()
    {
        var executor = new LocalExecutor(new FakeProcessRunner(), new LocalExecutorOptions());
        executor.Mode.Should().Be(ExecutionModeNames.Local);
    }

    [Test]
    public async Task ExecuteAsync_ReturnsSuccess_WhenCliWritesResult()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (req, _) =>
            {
                // Simulate the CLI writing the result file.
                var resultPath = req.Arguments[req.Arguments.ToList().IndexOf("--output") + 1];
                File.WriteAllText(resultPath, @"{
                    ""success"": true,
                    ""task"": ""implement"",
                    ""issue_number"": 42,
                    ""branch_name"": ""tamma/issue-42"",
                    ""tamma_session_id"": ""sess_xyz"",
                    ""files_changed"": [""a.ts""],
                    ""commit_sha"": ""deadbeef"",
                    ""tokens_used"": 1234,
                    ""duration_seconds"": 10,
                    ""agent_provider"": ""claude-code""
                }");
                return new ProcessRunResult(ExitCode: 0, StdOut: "ok", StdErr: "", TimedOut: false, DurationSeconds: 5);
            }
        };

        var opts = new LocalExecutorOptions
        {
            WorkingDirectory = _workDir,
            CleanupAfterRun = false
        };
        var executor = new LocalExecutor(runner, opts);

        var result = await executor.ExecuteAsync(MakeRequest());

        result.Success.Should().BeTrue();
        result.ExecutionMode.Should().Be(ExecutionModeNames.Local);
        result.CommitSha.Should().Be("deadbeef");
        result.FilesChanged.Should().ContainSingle().Which.Should().Be("a.ts");
        result.TokensUsed.Should().Be(1234);
    }

    [Test]
    public async Task ExecuteAsync_ReportsTimeout()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (_, _) => new ProcessRunResult(-1, "", "killed", TimedOut: true, DurationSeconds: 1800)
        };
        var opts = new LocalExecutorOptions { WorkingDirectory = _workDir, CleanupAfterRun = false };
        var executor = new LocalExecutor(runner, opts);

        var result = await executor.ExecuteAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
    }

    [Test]
    public async Task ExecuteAsync_ReportsNonZeroExit()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (_, _) => new ProcessRunResult(42, "", "boom", TimedOut: false, DurationSeconds: 3)
        };
        var opts = new LocalExecutorOptions { WorkingDirectory = _workDir, CleanupAfterRun = false };
        var executor = new LocalExecutor(runner, opts);

        var result = await executor.ExecuteAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exited with 42");
        result.ErrorMessage.Should().Contain("boom");
    }

    [Test]
    public async Task ExecuteAsync_ReportsMissingResultFile()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (_, _) => new ProcessRunResult(0, "ran fine", "", TimedOut: false, DurationSeconds: 2)
        };
        var opts = new LocalExecutorOptions { WorkingDirectory = _workDir, CleanupAfterRun = false };
        var executor = new LocalExecutor(runner, opts);

        var result = await executor.ExecuteAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("did not produce a result file");
    }

    [Test]
    public async Task ExecuteAsync_WritesRequestFile_WithExpectedFields()
    {
        string? capturedPath = null;
        var runner = new FakeProcessRunner
        {
            OnRun = (req, _) =>
            {
                capturedPath = req.Arguments[req.Arguments.ToList().IndexOf("--request") + 1];
                return new ProcessRunResult(1, "", "", false, 1);
            }
        };
        var opts = new LocalExecutorOptions { WorkingDirectory = _workDir, CleanupAfterRun = false };
        var executor = new LocalExecutor(runner, opts);

        await executor.ExecuteAsync(MakeRequest());

        capturedPath.Should().NotBeNull();
        File.Exists(capturedPath).Should().BeTrue();
        var text = await File.ReadAllTextAsync(capturedPath!);
        text.Should().Contain("\"repository\": \"acme/widgets\"");
        text.Should().Contain("\"tamma_session_id\": \"sess_xyz\"");
        text.Should().Contain("\"agent_provider\": \"claude-code\"");
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<ProcessRunRequest, CancellationToken, ProcessRunResult> OnRun { get; set; } =
            (_, _) => new ProcessRunResult(0, "", "", false, 0);

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(OnRun(request, cancellationToken));
    }
}
