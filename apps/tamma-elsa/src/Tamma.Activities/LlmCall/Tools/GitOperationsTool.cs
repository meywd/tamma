using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Actions;

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

    /// <summary>
    /// Allowed git subcommands — Story 43-4 (AC6, D6): a PROJECTION over the
    /// Story 43-2 <see cref="GitSubcommand"/> wire set, no longer a hand-written
    /// copy. The comparer stays <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// deliberately: the pre-refactor set matched case-insensitively, so a
    /// model-issued <c>"STATUS"</c>/<c>"Push"</c> must keep being accepted (bug
    /// 2026-07-27-gitoperationstool-case-insensitive-subcommand-refactor-trap —
    /// <c>EnumWire</c> parsing alone is ordinal case-sensitive and would have
    /// silently regressed it). Fourteen in, fourteen out: parity with the
    /// literal pre-refactor names is pinned by
    /// <c>GitOperationsSubcommandTests</c> / <c>GitSubcommandParitySweepTests</c>.
    /// </summary>
    private static readonly HashSet<string> AllowedSubcommands =
        Enum.GetValues<GitSubcommand>()
            .Select(s => s.ToWire())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical subcommand list, derived from the same enum the allow-set projects.</summary>
    private static readonly string SubcommandList =
        string.Join(", ", Enum.GetValues<GitSubcommand>().Select(s => s.ToWire()));

    public string ToolName => "git_operations";

    public string Description =>
        $"Execute git operations in the workspace. Supports subcommands: {SubcommandList}.";

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
                    $"Unknown git subcommand: '{subcommand}'. Allowed: {SubcommandList}",
                    sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, errorResult, subcommand);
                return errorResult;
            }

            // Block shell metacharacters in extra args to prevent command injection.
            // Git is invoked directly (not via shell), so metacharacters have no
            // legitimate use and indicate an injection attempt.
            if (!string.IsNullOrWhiteSpace(extraArgs) &&
                CommandValidator.ContainsShellMetacharacters(extraArgs))
            {
                _logger.LogWarning(
                    "Git args blocked — shell metacharacters detected: {ToolName} {ToolCallId}",
                    ToolName, toolCallId);

                var injectionResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    "Git arguments blocked: shell metacharacters are not allowed.",
                    sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, injectionResult, subcommand);
                return injectionResult;
            }

            _logger.LogDebug(
                "Git operation: {ToolName} {ToolCallId} subcommand={Subcommand}",
                ToolName, toolCallId, subcommand);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = _gitBinary,
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Use ArgumentList (not Arguments string) to prevent injection.
            // Each token is passed as a separate OS-level argument.
            psi.ArgumentList.Add(subcommand);
            if (!string.IsNullOrWhiteSpace(extraArgs))
            {
                foreach (var token in extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    psi.ArgumentList.Add(token);
                }
            }

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

            // Synchronous WaitForExit ensures async OutputDataReceived/ErrorDataReceived
            // event handlers have completed before we read the builders.
            process.WaitForExit();

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
