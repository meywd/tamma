using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Writes content to a file with path validation against the workspace root.
/// Creates parent directories if needed.
/// </summary>
public class FileWriteTool : IToolExecutor
{
    private readonly ILogger<FileWriteTool> _logger;
    private readonly string _workspaceRoot;

    public string ToolName => "file_write";

    public string Description =>
        "Write content to a file at the given path relative to the workspace root. Creates parent directories if needed.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "File path relative to workspace root"
            },
            ["content"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Content to write to the file"
            }
        },
        ["required"] = new[] { "path", "content" }
    };

    public FileWriteTool(ILogger<FileWriteTool> logger, IConfiguration configuration)
    {
        _logger = logger;
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                         ?? Environment.CurrentDirectory;
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
            var path = args.GetProperty("path").GetString()
                       ?? throw new ArgumentException("Missing 'path' argument");
            var content = args.GetProperty("content").GetString()
                          ?? throw new ArgumentException("Missing 'content' argument");

            var resolvedPath = PathValidator.ResolveSafePath(path, _workspaceRoot);

            // Ensure parent directory exists
            var dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(resolvedPath, content, cancellationToken);

            var result = new ToolExecutionResult(toolCallId, ToolName, true,
                $"Successfully wrote {content.Length} characters to {path}", sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result);
            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("outside"))
        {
            _logger.LogWarning(
                "File path validation failed (traversal): {ToolName} {ToolCallId}",
                ToolName, toolCallId);

            var result = new ToolExecutionResult(toolCallId, ToolName, false,
                "Access denied: path resolves outside the workspace root.", sw.ElapsedMilliseconds);
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
                $"Error writing file: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    private void LogCompletion(string toolCallId, ToolExecutionResult result)
    {
        _logger.LogInformation(
            "Tool execution completed: {ToolName} {ToolCallId} success={Success} duration={DurationMs}ms outputSize={OutputSizeBytes}B",
            ToolName, toolCallId, result.Success, result.DurationMs, result.Output?.Length ?? 0);
    }
}
