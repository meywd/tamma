using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Searches for regex or literal patterns across files in the workspace.
/// Returns matching file paths and lines.
/// </summary>
public class SearchCodeTool : IToolExecutor
{
    private readonly ILogger<SearchCodeTool> _logger;
    private readonly string _workspaceRoot;

    /// <summary>Directories to skip during search.</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", "node_modules", ".git", ".vs", ".idea", "packages", "TestResults"
    };

    public string ToolName => "search_code";

    public string Description =>
        "Search for a regex or literal pattern across files in the workspace. Returns matching file paths and lines.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["pattern"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Regex or literal search pattern"
            },
            ["file_glob"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional file glob filter (e.g. '*.cs', '*.ts'). Default: all files."
            },
            ["max_results"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum number of matching lines to return. Default: 50."
            }
        },
        ["required"] = new[] { "pattern" }
    };

    public SearchCodeTool(ILogger<SearchCodeTool> logger, IConfiguration configuration)
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
            var pattern = args.GetProperty("pattern").GetString()
                          ?? throw new ArgumentException("Missing 'pattern' argument");
            var fileGlob = args.TryGetProperty("file_glob", out var fg) ? fg.GetString() ?? "*" : "*";
            var maxResults = args.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 50;

            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(5));
            }
            catch (RegexParseException ex)
            {
                var result = new ToolExecutionResult(toolCallId, ToolName, false,
                    $"Invalid regex pattern: {ex.Message}", sw.ElapsedMilliseconds);
                LogCompletion(toolCallId, result);
                return result;
            }

            var sb = new StringBuilder();
            var matchCount = 0;
            var searchPattern = fileGlob.Contains('*') || fileGlob.Contains('?') ? fileGlob : $"*{fileGlob}*";

            foreach (var file in EnumerateFilesSkippingDirs(_workspaceRoot, searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var relativePath = Path.GetRelativePath(_workspaceRoot, file);
                    var lineNumber = 0;

                    using var reader = new StreamReader(file);
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        lineNumber++;
                        if (regex.IsMatch(line))
                        {
                            sb.AppendLine($"{relativePath}:{lineNumber}: {line.TrimEnd()}");
                            matchCount++;
                            if (matchCount >= maxResults)
                                break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip unreadable files (binary, locked, etc.)
                }

                if (matchCount >= maxResults)
                    break;
            }

            var output = matchCount == 0
                ? $"No matches found for pattern: {pattern}"
                : sb.ToString();

            var successResult = new ToolExecutionResult(toolCallId, ToolName, true,
                ToolOutputHelper.Truncate(output, _logger, ToolName, toolCallId), sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, successResult);
            return successResult;
        }
        catch (OperationCanceledException)
        {
            var result = new ToolExecutionResult(toolCallId, ToolName, false,
                "Search was cancelled.", sw.ElapsedMilliseconds);
            LogCompletion(toolCallId, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Tool execution failed: {ToolName} {ToolCallId} exception={ExceptionType} message={ExceptionMessage} duration={DurationMs}ms",
                ToolName, toolCallId, ex.GetType().Name, ex.Message, sw.ElapsedMilliseconds);

            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Search error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Enumerate files while skipping common non-source directories.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSkippingDirs(string root, string searchPattern)
    {
        var dirs = new Stack<string>();
        dirs.Push(root);

        while (dirs.Count > 0)
        {
            var currentDir = dirs.Pop();
            var dirName = Path.GetFileName(currentDir);

            // Skip hidden/non-source directories (except the root itself)
            if (!string.Equals(currentDir, root, StringComparison.OrdinalIgnoreCase)
                && (dirName.StartsWith('.') || SkipDirectories.Contains(dirName)))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir, searchPattern);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    dirs.Push(subDir);
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible directories
            }
        }
    }

    private void LogCompletion(string toolCallId, ToolExecutionResult result)
    {
        _logger.LogInformation(
            "Tool execution completed: {ToolName} {ToolCallId} success={Success} duration={DurationMs}ms outputSize={OutputSizeBytes}B",
            ToolName, toolCallId, result.Success, result.DurationMs, result.Output?.Length ?? 0);
    }
}
