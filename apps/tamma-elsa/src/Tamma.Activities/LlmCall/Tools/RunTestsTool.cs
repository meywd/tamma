using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Runs test commands and captures stdout/stderr with configurable timeout.
/// </summary>
public class RunTestsTool : IToolExecutor
{
    private readonly ILogger<RunTestsTool> _logger;
    private readonly string _workspaceRoot;
    private readonly string _defaultTestCommand;
    private readonly int _timeoutSeconds;

    public string ToolName => "run_tests";

    public string Description =>
        "Run tests in the workspace and capture the output. Supports configurable test commands and optional project/filter arguments.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["command"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Test command to run (e.g. 'dotnet test', 'pnpm test'). Uses default if not specified."
            },
            ["project"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional project or directory path to test (relative to workspace root)"
            },
            ["filter"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional test filter expression (e.g. 'ClassName.MethodName' for dotnet, '--grep pattern' for pnpm)"
            }
        },
        ["required"] = Array.Empty<string>()
    };

    public RunTestsTool(ILogger<RunTestsTool> logger, IConfiguration configuration)
    {
        _logger = logger;
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                         ?? Environment.CurrentDirectory;
        _defaultTestCommand = configuration["ToolExecution:TestCommand"] ?? "dotnet test";
        _timeoutSeconds = int.TryParse(configuration["ToolExecution:TestTimeoutSeconds"], out var t)
            ? t
            : 120;
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

            var testCommand = args.TryGetProperty("command", out var cmdEl)
                ? cmdEl.GetString() ?? _defaultTestCommand
                : _defaultTestCommand;

            var project = args.TryGetProperty("project", out var projEl)
                ? projEl.GetString()
                : null;

            var filter = args.TryGetProperty("filter", out var filterEl)
                ? filterEl.GetString()
                : null;

            // Build the full command
            var fullCommand = new StringBuilder(testCommand);
            if (!string.IsNullOrWhiteSpace(project))
                fullCommand.Append($" {project}");
            if (!string.IsNullOrWhiteSpace(filter))
                fullCommand.Append($" --filter \"{filter}\"");

            _logger.LogDebug(
                "Test execution started: {ToolName} {ToolCallId} timeout={TimeoutMs}ms",
                ToolName, toolCallId, _timeoutSeconds * 1000);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var isWindows = OperatingSystem.IsWindows();
            var commandStr = fullCommand.ToString();

            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (isWindows)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(commandStr);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(commandStr);
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
                    "Test execution timed out: {ToolName} {ToolCallId} timeout={TimeoutMs}ms",
                    ToolName, toolCallId, _timeoutSeconds * 1000);

                var timeoutResult = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Test execution timed out after {_timeoutSeconds} seconds.", sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, timeoutResult);
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
                $"Test execution error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private void LogCompletion(string toolCallId, ToolExecutionResult result)
    {
        _logger.LogInformation(
            "Tool execution completed: {ToolName} {ToolCallId} success={Success} duration={DurationMs}ms outputSize={OutputSizeBytes}B",
            ToolName, toolCallId, result.Success, result.DurationMs, result.Output?.Length ?? 0);
    }
}
