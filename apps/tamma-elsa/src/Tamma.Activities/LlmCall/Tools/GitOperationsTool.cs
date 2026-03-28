using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Executes git CLI operations within the workspace. Supports a fixed set of subcommands.
/// </summary>
public class GitOperationsTool : IToolExecutor
{
    private readonly ILogger<GitOperationsTool> _logger;
    private readonly string _workspaceRoot;
    private readonly string _gitBinary;
    private readonly int _timeoutSeconds;

    /// <summary>Allowed git subcommands.</summary>
    private static readonly HashSet<string> AllowedSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "add", "commit", "push", "branch", "checkout",
        "stash", "show", "fetch", "pull", "rev-parse", "ls-files"
    };

    public string ToolName => "git_operations";

    public string Description =>
        "Execute git operations in the workspace. Supports subcommands: status, diff, log, add, commit, push, branch, checkout, stash, show, fetch, pull, rev-parse, ls-files.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["subcommand"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Git subcommand to run (e.g. 'status', 'diff', 'log')"
            },
            ["args"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Additional arguments to pass to the git subcommand (optional)"
            }
        },
        ["required"] = new[] { "subcommand" }
    };

    public GitOperationsTool(ILogger<GitOperationsTool> logger, IConfiguration configuration)
    {
        _logger = logger;
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                         ?? Environment.CurrentDirectory;
        _gitBinary = configuration["ToolExecution:GitBinary"] ?? "git";
        _timeoutSeconds = int.TryParse(configuration["ToolExecution:GitTimeoutSeconds"], out var t)
            ? t
            : 60;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Tool execution started: {ToolName} {ToolCallId} argsSize={ArgumentsSizeBytes}B",
            ToolName, toolCallId, argumentsJson?.Length ?? 0);

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson ?? "{}");
            var subcommand = args.GetProperty("subcommand").GetString()
                             ?? throw new ArgumentException("Missing 'subcommand' argument");
            var extraArgs = args.TryGetProperty("args", out var argsEl) ? argsEl.GetString() ?? "" : "";

            if (!AllowedSubcommands.Contains(subcommand))
            {
                var errorResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Unknown git subcommand: '{subcommand}'. Allowed: {string.Join(", ", AllowedSubcommands)}",
                    sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, errorResult, subcommand);
                return errorResult;
            }

            var gitArgs = string.IsNullOrWhiteSpace(extraArgs)
                ? subcommand
                : $"{subcommand} {extraArgs}";

            _logger.LogDebug(
                "Git operation: {ToolName} {ToolCallId} subcommand={Subcommand}",
                ToolName, toolCallId, subcommand);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = _gitBinary,
                Arguments = gitArgs,
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

                _logger.LogWarning(
                    "Git operation timed out: {ToolName} {ToolCallId} subcommand={Subcommand} timeout={TimeoutMs}ms",
                    ToolName, toolCallId, subcommand, _timeoutSeconds * 1000);

                var timeoutResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Git {subcommand} timed out after {_timeoutSeconds} seconds.", sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, timeoutResult, subcommand);
                return timeoutResult;
            }

            var stdout = stdoutBuilder.ToString();
            var stderr = stderrBuilder.ToString();
            var output = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(stdout))
                output.Append(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (output.Length > 0)
                    output.AppendLine();
                output.AppendLine("--- stderr ---");
                output.Append(stderr);
            }

            if (output.Length == 0)
                output.Append("(no output)");

            output.AppendLine();
            output.Append($"Exit code: {process.ExitCode}");

            var finalOutput = ToolOutputHelper.Truncate(output.ToString(), _logger, ToolName, toolCallId);
            var result = new ToolExecutionResult(toolCallId, ToolName, process.ExitCode == 0,
                finalOutput, sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result, subcommand);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = new ToolExecutionResult(toolCallId, ToolName, false,
                "Operation was cancelled.", sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result, null);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Tool execution failed: {ToolName} {ToolCallId} exception={ExceptionType} message={ExceptionMessage} duration={DurationMs}ms",
                ToolName, toolCallId, ex.GetType().Name, ex.Message, sw.ElapsedMilliseconds);

            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Git operation error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private void LogCompletion(string toolCallId, ToolExecutionResult result, string? subcommand)
    {
        _logger.LogInformation(
            "Tool execution completed: {ToolName} {ToolCallId} subcommand={Subcommand} success={Success} duration={DurationMs}ms outputSize={OutputSizeBytes}B",
            ToolName, toolCallId, subcommand ?? "unknown", result.Success, result.DurationMs, result.Output?.Length ?? 0);
    }
}
