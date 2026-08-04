using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Executes shell commands in the workspace directory with blocked-pattern validation
/// and configurable timeout.
/// </summary>
public class ShellExecuteTool : IToolExecutor
{
    private readonly ILogger<ShellExecuteTool> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _workspaceRoot;
    private readonly int _timeoutSeconds;
    private readonly bool _sandboxed;

    public string ToolName => "shell_execute";

    public string Description =>
        "Execute a shell command in the workspace directory. Some dangerous commands are blocked for safety.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["command"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Shell command to execute"
            }
        },
        ["required"] = new[] { "command" }
    };

    public ShellExecuteTool(ILogger<ShellExecuteTool> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                         ?? Environment.CurrentDirectory;
        _timeoutSeconds = int.TryParse(configuration["ToolExecution:ShellTimeoutSeconds"], out var t)
            ? t
            : 60;
        _sandboxed = configuration.GetValue("Tools:Shell:Sandboxed", false);
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
            var command = args.GetProperty("command").GetString()
                          ?? throw new ArgumentException("Missing 'command' argument");

            // Validate against blocked patterns (shared via CommandValidator)
            var blockedPattern = CommandValidator.GetBlockedPatternName(command);
            if (blockedPattern != null)
            {
                _logger.LogWarning(
                    "Shell command blocked by ActionGate: {ToolName} {ToolCallId} blockedPattern={BlockedPatternName}",
                    ToolName, toolCallId, blockedPattern);

                var blockedResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Command blocked by security policy (matched: {blockedPattern}).", sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, blockedResult);
                return blockedResult;
            }

            // Story 42-10 (AC4, D2): under the sandboxed profile, confine the
            // command to the workspace root before it spawns. Unsandboxed is
            // byte-identical to before (the screen is a no-op).
            if (_sandboxed)
            {
                var confinement = WorkspaceConfinementScreen.GetViolation(command, _workspaceRoot);
                if (confinement != null)
                {
                    _logger.LogWarning(
                        "Shell command blocked by workspace confinement: {ToolName} {ToolCallId} reason={Reason}",
                        ToolName, toolCallId, confinement);

                    var confinedResult = new ToolExecutionResult(toolCallId, ToolName, false,
                        $"Command blocked by workspace confinement: {confinement}", sw.ElapsedMilliseconds);
                    LogCompletion(toolCallId, confinedResult);
                    return confinedResult;
                }
            }

            _logger.LogDebug(
                "Shell command execution started: {ToolName} {ToolCallId} timeout={TimeoutMs}ms",
                ToolName, toolCallId, _timeoutSeconds * 1000);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Story 42-10 (AC1, D1): the child NEVER inherits the API process's
            // secrets. Applied in both profiles — the strip is unconditional.
            ProcessEnvironmentAllowlist.Apply(psi, _configuration);

            if (isWindows)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
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
                    "Shell command timed out: {ToolName} {ToolCallId} timeout={TimeoutMs}ms",
                    ToolName, toolCallId, _timeoutSeconds * 1000);

                var timeoutResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Command timed out after {_timeoutSeconds} seconds.", sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, timeoutResult);
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

            output.AppendLine();
            output.Append($"Exit code: {process.ExitCode}");

            var finalOutput = ToolOutputHelper.Truncate(output.ToString(), _logger, ToolName, toolCallId);
            var result = new ToolExecutionResult(toolCallId, ToolName, process.ExitCode == 0,
                finalOutput, sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = new ToolExecutionResult(toolCallId, ToolName, false,
                "Operation was cancelled.", sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Tool execution failed: {ToolName} {ToolCallId} exception={ExceptionType} message={ExceptionMessage} duration={DurationMs}ms",
                ToolName, toolCallId, ex.GetType().Name, ex.Message, sw.ElapsedMilliseconds);

            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Shell execution error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private void LogCompletion(string toolCallId, ToolExecutionResult result)
    {
        _logger.LogInformation(
            "Tool execution completed: {ToolName} {ToolCallId} success={Success} duration={DurationMs}ms outputSize={OutputSizeBytes}B",
            ToolName, toolCallId, result.Success, result.DurationMs, result.Output?.Length ?? 0);
    }
}
