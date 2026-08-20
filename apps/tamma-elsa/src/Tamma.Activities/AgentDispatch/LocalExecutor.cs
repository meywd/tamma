using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// <see cref="IAgentExecutor"/> that runs the agent locally as a child
/// process (story 19-5 AC-2). Used when Tamma is running in CLI mode
/// or self-hosted with the agent on the same machine.
///
/// <para><b>JSON shell-out protocol:</b> the executor writes a request
/// file and invokes the Tamma CLI (Node.js) with
/// <c>--request &lt;path&gt; --output &lt;path&gt;</c>. The CLI command
/// (<c>packages/cli/src/commands/execute-agent.ts</c>) reads the request,
/// runs the agent, and writes an <see cref="AgentResultArtifact"/>-shaped
/// JSON file to the output path. Schema:</para>
///
/// <para>Request file (<c>.tamma/exec-request-{sessionId}.json</c>):</para>
/// <code>
/// {
///   "repository": "owner/repo",
///   "branch_name": "tamma/issue-42",
///   "issue_number": 42,
///   "issue_title": "Fix login flow",
///   "task": "implement",
///   "plan_json": "{...}",
///   "tamma_session_id": "sess_abc123",
///   "agent_provider": "claude-code",
///   "agent_config_json": "{...}",
///   "timeout_minutes": 30
/// }
/// </code>
///
/// <para>Result file (<c>.tamma/exec-result-{sessionId}.json</c>):
/// identical shape to <see cref="AgentResultArtifact"/>.</para>
///
/// <para><b>Packaging:</b> the CLI command exists and implements this
/// protocol, but its bundle does not ship with the .NET app — the entry
/// point must exist on disk (<c>pnpm --filter @tamma/cli build</c>) for a
/// local run to succeed. Because the child process runs in a per-session
/// temp <see cref="ProcessRunRequest.WorkingDirectory"/>, a relative
/// <see cref="LocalExecutorOptions.CliEntryPoint"/> would resolve against
/// that temp dir and never be found; it is therefore resolved to an
/// absolute path before the spawn (see
/// <see cref="LocalExecutorOptions.ResolveCliEntryPoint()"/>).</para>
/// </summary>
public sealed class LocalExecutor : IAgentExecutor
{
    public string Mode => ExecutionModeNames.Local;

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<LocalExecutor>? _logger;
    private readonly LocalExecutorOptions _options;

    public LocalExecutor(
        IProcessRunner processRunner,
        LocalExecutorOptions? options = null,
        ILogger<LocalExecutor>? logger = null)
    {
        _processRunner = processRunner;
        _logger = logger;
        _options = options ?? new LocalExecutorOptions();
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var workDir = ResolveWorkingDirectory(request);
        Directory.CreateDirectory(workDir);

        var requestPath = Path.Combine(workDir, $"exec-request-{SafeId(request.SessionId)}.json");
        var resultPath = Path.Combine(workDir, $"exec-result-{SafeId(request.SessionId)}.json");

        // Absolute, always: workDir is a per-session temp dir, so `node` would
        // resolve a relative entry point against THAT and never find the CLI.
        var entryPoint = _options.ResolveCliEntryPoint();

        try
        {
            await File.WriteAllTextAsync(requestPath, SerializeRequest(request), cancellationToken);

            var cliArgs = new List<string>
            {
                entryPoint,
                "execute-agent",
                "--request",
                requestPath,
                "--output",
                resultPath
            };

            _logger?.LogInformation(
                "LocalExecutor spawning {Exe} {Args} in {WorkDir} (session={SessionId})",
                _options.NodeExecutable, string.Join(' ', cliArgs), workDir, request.SessionId);

            var runResult = await _processRunner.RunAsync(new ProcessRunRequest(
                FileName: _options.NodeExecutable,
                Arguments: cliArgs,
                WorkingDirectory: workDir,
                EnvironmentOverrides: null,
                TimeoutSeconds: Math.Max(60, request.TimeoutMinutes * 60)), cancellationToken);

            if (runResult.TimedOut)
            {
                return AgentExecutionResult.Failed(
                    $"Local agent timed out after {request.TimeoutMinutes}m",
                    request.AgentProvider,
                    ExecutionModeNames.Local);
            }

            if (runResult.ExitCode != 0)
            {
                var tail = Tail(runResult.StdErr, 1024);
                return new AgentExecutionResult(
                    Success: false,
                    PrNumber: null,
                    PrUrl: null,
                    CommitSha: string.Empty,
                    FilesChanged: Array.Empty<string>(),
                    CommitsCount: 0,
                    ChecksPassed: null,
                    TokensUsed: 0,
                    DurationSeconds: runResult.DurationSeconds,
                    ErrorMessage: $"Local agent exited with {runResult.ExitCode}: {tail}",
                    AgentLogSummary: Tail(runResult.StdOut, 2048),
                    AgentProvider: request.AgentProvider,
                    AgentVersion: null,
                    ExecutionMode: ExecutionModeNames.Local);
            }

            if (!File.Exists(resultPath))
            {
                return new AgentExecutionResult(
                    Success: false,
                    PrNumber: null,
                    PrUrl: null,
                    CommitSha: string.Empty,
                    FilesChanged: Array.Empty<string>(),
                    CommitsCount: 0,
                    ChecksPassed: null,
                    TokensUsed: 0,
                    DurationSeconds: runResult.DurationSeconds,
                    ErrorMessage:
                        $"Local agent CLI did not produce a result file at '{resultPath}'. "
                        + $"Resolved entry point: '{entryPoint}' — build it with "
                        + "`pnpm --filter @tamma/cli build`, or set Agent:Local:CliEntryPoint "
                        + "to an absolute path if the CLI lives elsewhere.",
                    AgentLogSummary: Tail(runResult.StdOut, 2048),
                    AgentProvider: request.AgentProvider,
                    AgentVersion: null,
                    ExecutionMode: ExecutionModeNames.Local);
            }

            var resultJson = await File.ReadAllTextAsync(resultPath, cancellationToken);
            var artifact = AgentResultArtifactParser.ParseResultJson(resultJson);
            if (artifact is null)
            {
                return AgentExecutionResult.Failed(
                    "Local agent produced a result file but it failed JSON parsing",
                    request.AgentProvider,
                    ExecutionModeNames.Local);
            }

            return new AgentExecutionResult(
                Success: artifact.Success,
                PrNumber: artifact.PrNumber,
                PrUrl: null, // local agent doesn't create PRs; that's a separate workflow step
                CommitSha: artifact.CommitSha,
                FilesChanged: artifact.FilesChanged,
                CommitsCount: artifact.FilesChanged.Length > 0 ? 1 : 0,
                ChecksPassed: null,
                TokensUsed: artifact.TokensUsed,
                DurationSeconds: artifact.DurationSeconds > 0 ? artifact.DurationSeconds : runResult.DurationSeconds,
                ErrorMessage: artifact.ErrorMessage,
                AgentLogSummary: artifact.AgentLogSummary ?? Tail(runResult.StdOut, 2048),
                AgentProvider: artifact.AgentProvider,
                AgentVersion: artifact.AgentVersion,
                ExecutionMode: ExecutionModeNames.Local);
        }
        finally
        {
            // Best-effort cleanup — leave the files on disk for debugging
            // if any exception escaped; tests can provide their own temp dir.
            if (_options.CleanupAfterRun)
            {
                TryDelete(requestPath);
                TryDelete(resultPath);
            }
        }
    }

    private string ResolveWorkingDirectory(AgentExecutionRequest request)
    {
        if (!string.IsNullOrEmpty(_options.WorkingDirectory))
        {
            return _options.WorkingDirectory!;
        }
        // Default: a per-session temp dir. Tests inject WorkingDirectory
        // directly so we don't leak files in CI.
        return Path.Combine(Path.GetTempPath(), "tamma", SafeId(request.SessionId));
    }

    private static string SerializeRequest(AgentExecutionRequest r)
    {
        var dict = new Dictionary<string, object?>
        {
            ["repository"] = r.Repository,
            ["branch_name"] = r.BranchName,
            ["issue_number"] = r.IssueNumber,
            ["issue_title"] = r.IssueTitle,
            ["task"] = r.Task,
            ["plan_json"] = r.PlanJson,
            ["tamma_session_id"] = r.SessionId,
            ["agent_provider"] = r.AgentProvider,
            ["agent_config_json"] = r.AgentConfigJson ?? "{}",
            ["timeout_minutes"] = r.TimeoutMinutes
        };
        return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SafeId(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Guid.NewGuid().ToString("N");
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private static string Tail(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : "..." + text[^max..];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore cleanup failures */ }
    }
}

/// <summary>
/// Configuration for <see cref="LocalExecutor"/>. Bound from the
/// <c>Agent:Local</c> configuration section or overridden in tests.
/// </summary>
public sealed class LocalExecutorOptions
{
    /// <summary>Node.js executable (PATH-resolved by default).</summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>
    /// Path to the Tamma CLI entry point. A relative value is anchored at the
    /// app's own directory or the working directory (see
    /// <see cref="ResolveCliEntryPoint(string?, string?, string?, Func{string, bool})"/>);
    /// set <c>Agent:Local:CliEntryPoint</c> to an absolute path when the CLI is
    /// packaged somewhere else.
    /// </summary>
    public string CliEntryPoint { get; set; } = DefaultCliEntryPoint;

    /// <summary>Repo-relative location of the built CLI bundle.</summary>
    public const string DefaultCliEntryPoint = "packages/cli/dist/index.js";

    /// <summary>
    /// Working directory for the child process. If empty, a per-session
    /// temp dir is used.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Delete the per-session request/result files after the run.</summary>
    public bool CleanupAfterRun { get; set; } = true;

    /// <summary>
    /// Resolve <see cref="CliEntryPoint"/> against the running app's own location
    /// and the process working directory.
    /// </summary>
    public string ResolveCliEntryPoint() => ResolveCliEntryPoint(
        CliEntryPoint, AppContext.BaseDirectory, Directory.GetCurrentDirectory(), File.Exists);

    /// <summary>
    /// Turn a configured entry point into an ABSOLUTE path. The child process runs
    /// in a per-session temp working directory, so a relative path handed to
    /// <c>node</c> resolves against that temp dir and fails — the default
    /// <see cref="DefaultCliEntryPoint"/> is relative to the REPO root, which is
    /// somewhere above the app's bin directory.
    ///
    /// <para>An absolute configured value is honoured verbatim. A relative one is
    /// probed against <paramref name="baseDirectory"/> and
    /// <paramref name="currentDirectory"/> and each of their ancestors, first hit
    /// wins. When nothing exists yet (the CLI bundle is built separately) the path
    /// is still returned absolute, anchored at the first candidate root, so the
    /// failure names a real location instead of a temp-relative one.</para>
    /// </summary>
    public static string ResolveCliEntryPoint(
        string? configured, string? baseDirectory, string? currentDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var entry = string.IsNullOrWhiteSpace(configured) ? DefaultCliEntryPoint : configured.Trim();
        if (Path.IsPathRooted(entry)) return Path.GetFullPath(entry);

        var anchors = AnchorChain(baseDirectory, currentDirectory).ToList();
        foreach (var anchor in anchors)
        {
            var candidate = Path.GetFullPath(Path.Combine(anchor, entry));
            if (fileExists(candidate)) return candidate;
        }

        var fallback = anchors.Count > 0 ? anchors[0] : Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(fallback, entry));
    }

    private static IEnumerable<string> AnchorChain(string? baseDirectory, string? currentDirectory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in new[] { baseDirectory, currentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            while (dir is not null)
            {
                if (seen.Add(dir.FullName)) yield return dir.FullName;
                dir = dir.Parent;
            }
        }
    }

    public static LocalExecutorOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Agent:Local");
        var options = new LocalExecutorOptions();
        if (section.Exists())
        {
            options.NodeExecutable = section["NodeExecutable"] ?? options.NodeExecutable;
            options.CliEntryPoint = section["CliEntryPoint"] ?? options.CliEntryPoint;
            options.WorkingDirectory = section["WorkingDirectory"];
            if (bool.TryParse(section["CleanupAfterRun"], out var cleanup))
                options.CleanupAfterRun = cleanup;
        }
        return options;
    }
}
