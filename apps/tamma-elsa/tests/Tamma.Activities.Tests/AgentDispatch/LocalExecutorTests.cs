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

    // ── Story 40-1 AC8 — the entry point must resolve from a temp workdir ──
    // The child runs with WorkingDirectory = a per-session temp dir, so the
    // repo-relative default would be resolved against THAT and never found.
    // These pin the resolution, not the packaging: the CLI bundle still has to
    // be built (`pnpm --filter @tamma/cli build`) for a real local run.

    [Test]
    public async Task ExecuteAsync_SpawnsAnAbsoluteEntryPoint_OnDefaultConfiguration()
    {
        ProcessRunRequest? captured = null;
        var runner = new FakeProcessRunner
        {
            OnRun = (req, _) => { captured = req; return new ProcessRunResult(0, "", "", false, 1); }
        };
        // Default CliEntryPoint, default (temp) working directory — the shape a
        // self-hosted install runs with when AgentExecutorFactory resolves `local`.
        var executor = new LocalExecutor(runner, new LocalExecutorOptions());

        await executor.ExecuteAsync(MakeRequest());

        captured.Should().NotBeNull();
        var entryPoint = captured!.Arguments[0];
        Path.IsPathRooted(entryPoint).Should().BeTrue(
            "node resolves a relative entry point against the child's working directory, "
            + $"which is the per-session temp dir '{captured.WorkingDirectory}'");
        Path.GetDirectoryName(entryPoint).Should().NotBe(captured.WorkingDirectory,
            "the CLI does not live in the per-session scratch dir");
    }

    [Test]
    public void ResolveCliEntryPoint_HonoursAnAbsoluteConfiguredPath()
    {
        var configured = Path.Combine(Path.GetTempPath(), "somewhere", "tamma-cli.js");
        var resolved = LocalExecutorOptions.ResolveCliEntryPoint(
            configured, "/opt/tamma/bin", "/opt/tamma", _ => false);

        resolved.Should().Be(Path.GetFullPath(configured),
            "an operator who configures an absolute path gets exactly that path");
    }

    [Test]
    public void ResolveCliEntryPoint_AnchorsARelativePath_AtTheFirstAncestorThatHasIt()
    {
        // The engine's bin dir sits several levels below the repo root, where the
        // default `packages/cli/dist/index.js` actually lives.
        var repoRoot = Path.GetFullPath("/repo");
        var expected = Path.Combine(repoRoot, "packages", "cli", "dist", "index.js");

        var resolved = LocalExecutorOptions.ResolveCliEntryPoint(
            LocalExecutorOptions.DefaultCliEntryPoint,
            Path.Combine(repoRoot, "apps", "tamma-elsa", "src", "Tamma.Api", "bin", "Debug", "net8.0"),
            Path.Combine(repoRoot, "apps", "tamma-elsa"),
            path => path == expected);

        resolved.Should().Be(expected);
    }

    [Test]
    public void ResolveCliEntryPoint_StillReturnsAnAbsolutePath_WhenNothingIsBuilt()
    {
        var baseDir = Path.GetFullPath("/opt/tamma/bin");
        var resolved = LocalExecutorOptions.ResolveCliEntryPoint(
            LocalExecutorOptions.DefaultCliEntryPoint, baseDir, "/opt/tamma", _ => false);

        Path.IsPathRooted(resolved).Should().BeTrue(
            "a not-yet-built CLI must fail against a NAMED location, not a temp-relative one");
        resolved.Should().StartWith(baseDir);
    }

    [Test]
    public async Task ExecuteAsync_MissingResultFile_NamesTheEntryPointAndTheBuildStep()
    {
        // The old message blamed an unimplemented CLI command. The command exists
        // (packages/cli/src/commands/execute-agent.ts); the real failure is that
        // its bundle is not built or not where the config points.
        var runner = new FakeProcessRunner
        {
            OnRun = (_, _) => new ProcessRunResult(0, "ran fine", "", TimedOut: false, DurationSeconds: 2)
        };
        var opts = new LocalExecutorOptions { WorkingDirectory = _workDir, CleanupAfterRun = false };
        var executor = new LocalExecutor(runner, opts);

        var result = await executor.ExecuteAsync(MakeRequest());

        result.ErrorMessage.Should().Contain("Agent:Local:CliEntryPoint");
        result.ErrorMessage.Should().Contain("pnpm --filter @tamma/cli build");
        result.ErrorMessage.Should().NotContain("may not be implemented",
            "the execute-agent command is implemented and unit-tested");
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<ProcessRunRequest, CancellationToken, ProcessRunResult> OnRun { get; set; } =
            (_, _) => new ProcessRunResult(0, "", "", false, 0);

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(OnRun(request, cancellationToken));
    }
}
