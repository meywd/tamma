---
title: "Story 12.1: Tool Executor Interface & Registry — Implementation Plan"
sidebar:
  order: 120
---

## Overview

This plan creates the `IToolExecutor` / `IToolExecutorRegistry` abstractions and six built-in tool implementations (FileRead, FileWrite, SearchCode, ShellExecute, GitOperations, RunTests). All new code lives under `Tamma.Activities/ToolExecution/`. Models are added to the existing `LlmCallModels.cs`. DI registration goes into `Program.cs`.

---

## Step-by-Step Implementation Tasks

### Task 1: Add New Models to LlmCallModels.cs

**File**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`

**What to add** (after the existing `NormalizedLlmResponse` class, around line 391):

```csharp
// ============================================================
// Tool Execution
// ============================================================

/// <summary>
/// Result of a single tool execution within the agentic loop.
/// </summary>
public record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    bool Success,
    string Output,
    long DurationMs
);

/// <summary>
/// Configuration for the agentic tool loop.
/// </summary>
public record ToolLoopConfig
{
    /// <summary>Maximum number of LLM round-trips before forcing termination.</summary>
    public int MaxSteps { get; init; } = 20;

    /// <summary>Allowlist of tool names the LLM may invoke. Null or empty = all tools allowed.</summary>
    public string[]? AllowedTools { get; init; }

    /// <summary>Total context window size in tokens for the model being used.</summary>
    public int ContextWindowTokens { get; init; } = 200_000;

    /// <summary>Fraction of context window at which compaction is triggered (0.0-1.0).</summary>
    public double CompactionThreshold { get; init; } = 0.8;

    /// <summary>Whether to enable SSE streaming for tool loop progress events.</summary>
    public bool EnableStreaming { get; init; } = false;
}

/// <summary>
/// Provider-agnostic conversation message for multi-turn tool use.
/// Serialized to Anthropic or OpenAI format at the HTTP call layer.
/// </summary>
public record ConversationMessage
{
    /// <summary>Message role: "system", "user", "assistant", or "tool".</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Text content (may be null for assistant messages that only contain tool calls).</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the assistant (only present when Role = "assistant").</summary>
    public ToolCallInfo[]? ToolCalls { get; init; }

    /// <summary>Tool call ID this message is a result for (only present when Role = "tool").</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name this result is for (only present when Role = "tool", used for Anthropic format).</summary>
    public string? ToolName { get; init; }
}

/// <summary>
/// Information about a single tool call from the LLM response.
/// </summary>
public record ToolCallInfo(
    string Id,
    string Name,
    string ArgumentsJson
);

/// <summary>
/// Normalized stop reason across providers.
/// </summary>
public enum StopReason
{
    /// <summary>LLM finished naturally (Anthropic: end_turn, OpenAI: stop).</summary>
    EndTurn,

    /// <summary>LLM wants to call tools (Anthropic: tool_use, OpenAI: tool_calls).</summary>
    ToolUse,

    /// <summary>Hit max_tokens limit.</summary>
    MaxTokens,

    /// <summary>Unknown or unmapped stop reason.</summary>
    Unknown
}
```

**Also modify `NormalizedLlmResponse`** (line ~381) to add `StopReason`:

```csharp
public class NormalizedLlmResponse
{
    public bool Success { get; set; }
    public string? ResponseText { get; set; }
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
    public StopReason StopReason { get; set; } = StopReason.EndTurn;  // <-- NEW
}
```

**Also modify `LlmCallWorkflowInput`** (line ~13) to add tool loop fields:

```csharp
/// <summary>Whether to enable the agentic tool loop. Default: false (single-turn, backward compatible).</summary>
public bool EnableToolLoop { get; set; } = false;

/// <summary>Configuration for the agentic tool loop (only used when EnableToolLoop = true).</summary>
public ToolLoopConfig? ToolLoopConfig { get; set; }
```

**Also modify `LlmCallWorkflowOutput`** (line ~52) to add tool loop output fields:

```csharp
/// <summary>Cumulative token usage across all tool loop turns (0 if tool loop was not enabled).</summary>
public int ToolLoopTokens { get; set; }

/// <summary>Number of tool loop iterations (0 if tool loop was not enabled).</summary>
public int ToolLoopTurns { get; set; }

/// <summary>Whether the tool loop exhausted maxSteps without the LLM producing a final response.</summary>
public bool ToolLoopExhausted { get; set; }
```

---

### Task 2: Create IToolExecutor Interface

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutor.cs`

```csharp
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Contract for a single tool that the LLM can invoke during the agentic tool loop.
/// Each implementation is registered in DI as IToolExecutor and discovered by the registry.
/// </summary>
public interface IToolExecutor
{
    /// <summary>
    /// Unique tool name matching what the LLM sees (e.g. "file_read", "shell_execute").
    /// </summary>
    string ToolName { get; }

    /// <summary>
    /// Human-readable description sent to the LLM as part of the tool definition.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema for the tool's input parameters (serialized as Dictionary for the LLM API).
    /// </summary>
    Dictionary<string, object> InputSchema { get; }

    /// <summary>
    /// Execute the tool with the given arguments.
    /// </summary>
    /// <param name="toolCallId">Provider-assigned tool call ID for correlation.</param>
    /// <param name="argumentsJson">JSON-serialized arguments from the LLM.</param>
    /// <param name="cancellationToken">Cancellation token (includes timeout).</param>
    /// <returns>Structured result with output text and timing.</returns>
    Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}
```

---

### Task 3: Create IToolExecutorRegistry Interface

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutorRegistry.cs`

```csharp
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Registry for discovering and retrieving tool executors by name.
/// Supports allowlist filtering for workflow-level tool restriction.
/// </summary>
public interface IToolExecutorRegistry
{
    /// <summary>
    /// Get a tool executor by name. Returns null if no executor is registered for that name.
    /// </summary>
    IToolExecutor? GetExecutor(string toolName);

    /// <summary>
    /// Check if a tool is allowed given the current allowlist.
    /// Returns true if allowlist is null/empty (all tools allowed) or if the name is in the allowlist.
    /// </summary>
    bool IsAllowed(string toolName, string[]? allowlist);

    /// <summary>
    /// Get all registered tool executors (for building the tools array sent to the LLM).
    /// </summary>
    IReadOnlyList<IToolExecutor> GetAll();

    /// <summary>
    /// Get all registered tool executors filtered by an allowlist.
    /// </summary>
    IReadOnlyList<IToolExecutor> GetAllowed(string[]? allowlist);
}
```

---

### Task 4: Create ToolExecutorRegistry Implementation

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolExecutorRegistry.cs`

```csharp
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Default registry populated via DI (IEnumerable&lt;IToolExecutor&gt;).
/// Tool names are case-insensitive.
/// </summary>
public class ToolExecutorRegistry : IToolExecutorRegistry
{
    private readonly Dictionary<string, IToolExecutor> _executors;
    private readonly ILogger<ToolExecutorRegistry> _logger;

    public ToolExecutorRegistry(
        IEnumerable<IToolExecutor> executors,
        ILogger<ToolExecutorRegistry> logger)
    {
        _logger = logger;
        _executors = new Dictionary<string, IToolExecutor>(StringComparer.OrdinalIgnoreCase);

        foreach (var executor in executors)
        {
            if (_executors.ContainsKey(executor.ToolName))
            {
                _logger.LogWarning(
                    "Duplicate tool executor registration for '{ToolName}', keeping first",
                    executor.ToolName);
                continue;
            }
            _executors[executor.ToolName] = executor;
            _logger.LogDebug("Registered tool executor: {ToolName}", executor.ToolName);
        }

        _logger.LogInformation("ToolExecutorRegistry initialized with {Count} tools", _executors.Count);
    }

    public IToolExecutor? GetExecutor(string toolName)
    {
        _executors.TryGetValue(toolName, out var executor);
        return executor;
    }

    public bool IsAllowed(string toolName, string[]? allowlist)
    {
        if (allowlist == null || allowlist.Length == 0)
            return true;

        return allowlist.Any(a => string.Equals(a, toolName, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<IToolExecutor> GetAll()
        => _executors.Values.ToList().AsReadOnly();

    public IReadOnlyList<IToolExecutor> GetAllowed(string[]? allowlist)
    {
        if (allowlist == null || allowlist.Length == 0)
            return GetAll();

        return _executors.Values
            .Where(e => allowlist.Any(a =>
                string.Equals(a, e.ToolName, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }
}
```

---

### Task 5: Create ToolOutputHelper (Shared Truncation Logic)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolOutputHelper.cs`

```csharp
using System.Text;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Shared utility for truncating tool output to the 50KB maximum.
/// </summary>
public static class ToolOutputHelper
{
    public const int MaxOutputBytes = 50 * 1024; // 50KB

    /// <summary>
    /// Truncate output string to MaxOutputBytes. If truncated, appends a suffix indicating
    /// total size.
    /// </summary>
    public static string Truncate(string output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        var bytes = Encoding.UTF8.GetByteCount(output);
        if (bytes <= MaxOutputBytes)
            return output;

        // Binary search for the character count that fits
        var charCount = output.Length;
        while (Encoding.UTF8.GetByteCount(output.AsSpan(0, charCount)) > MaxOutputBytes - 100)
        {
            charCount = (int)(charCount * 0.9);
        }

        return output[..charCount] + $"\n[truncated: {bytes} bytes total, showing first {charCount} chars]";
    }
}
```

---

### Task 6: Create PathValidator (Shared Path Security Logic)

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/PathValidator.cs`

```csharp
namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Validates and resolves file paths against a workspace root to prevent directory traversal.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Resolve a path relative to workspaceRoot. Throws if the resolved path escapes the workspace.
    /// </summary>
    /// <param name="requestedPath">Path from the LLM (relative or absolute).</param>
    /// <param name="workspaceRoot">The workspace root directory (absolute).</param>
    /// <returns>The fully resolved absolute path within the workspace.</returns>
    /// <exception cref="InvalidOperationException">Thrown if path escapes workspace.</exception>
    public static string ResolveSafePath(string requestedPath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("Path cannot be empty", nameof(requestedPath));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be empty", nameof(workspaceRoot));

        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        // If relative, combine with workspace root; if absolute, use as-is for validation
        var combinedPath = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.Combine(workspaceRoot, requestedPath);

        var resolvedPath = Path.GetFullPath(combinedPath);

        if (!resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedPath, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{requestedPath}' resolves to '{resolvedPath}' which is outside workspace root '{workspaceRoot}'");
        }

        return resolvedPath;
    }
}
```

---

### Task 7: Create FileReadTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileReadTool.cs`

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution.Tools;

public class FileReadTool : IToolExecutor
{
    private readonly ILogger<FileReadTool> _logger;
    private readonly string _workspaceRoot;

    public string ToolName => "file_read";

    public string Description => "Read the contents of a file at the given path relative to the workspace root.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "File path relative to workspace root"
            }
        },
        ["required"] = new[] { "path" }
    };

    public FileReadTool(ILogger<FileReadTool> logger, IConfiguration configuration)
    {
        _logger = logger;
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                       ?? Environment.CurrentDirectory;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            var path = args.GetProperty("path").GetString()
                     ?? throw new ArgumentException("Missing 'path' argument");

            var resolvedPath = PathValidator.ResolveSafePath(path, _workspaceRoot);

            if (!File.Exists(resolvedPath))
            {
                return new ToolExecutionResult(toolCallId, ToolName, false,
                    $"File not found: {path}", sw.ElapsedMilliseconds);
            }

            var content = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
            var output = ToolOutputHelper.Truncate(content);

            _logger.LogDebug("file_read: {Path} ({Length} chars)", path, content.Length);

            return new ToolExecutionResult(toolCallId, ToolName, true, output, sw.ElapsedMilliseconds);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("outside workspace"))
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Access denied: {ex.Message}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Error reading file: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
```

---

### Task 8: Create FileWriteTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileWriteTool.cs`

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution.Tools;

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
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
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

            _logger.LogDebug("file_write: {Path} ({Length} chars)", path, content.Length);

            return new ToolExecutionResult(toolCallId, ToolName, true,
                $"Successfully wrote {content.Length} characters to {path}", sw.ElapsedMilliseconds);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("outside workspace"))
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Access denied: {ex.Message}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Error writing file: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
```

---

### Task 9: Create SearchCodeTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/SearchCodeTool.cs`

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution.Tools;

public class SearchCodeTool : IToolExecutor
{
    private readonly ILogger<SearchCodeTool> _logger;
    private readonly string _workspaceRoot;

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
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            var pattern = args.GetProperty("pattern").GetString()
                        ?? throw new ArgumentException("Missing 'pattern' argument");
            var fileGlob = args.TryGetProperty("file_glob", out var fg) ? fg.GetString() ?? "*" : "*";
            var maxResults = args.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 50;

            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromSeconds(5));

            var sb = new StringBuilder();
            var matchCount = 0;
            var searchPattern = fileGlob.Contains('*') ? fileGlob : $"*{fileGlob}*";

            foreach (var file in Directory.EnumerateFiles(_workspaceRoot, searchPattern,
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip binary files and hidden dirs
                var relativePath = Path.GetRelativePath(_workspaceRoot, file);
                if (relativePath.StartsWith('.') || relativePath.Contains("/obj/")
                    || relativePath.Contains("/bin/") || relativePath.Contains("node_modules"))
                    continue;

                try
                {
                    var lineNumber = 0;
                    await foreach (var line in File.ReadLinesAsync(file, cancellationToken))
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
                catch (Exception) { /* skip unreadable files */ }

                if (matchCount >= maxResults)
                    break;
            }

            var output = matchCount == 0
                ? $"No matches found for pattern: {pattern}"
                : sb.ToString();

            _logger.LogDebug("search_code: pattern='{Pattern}', matches={Count}", pattern, matchCount);

            return new ToolExecutionResult(toolCallId, ToolName, true,
                ToolOutputHelper.Truncate(output), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Search error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
```

---

### Task 10: Create ShellExecuteTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/ShellExecuteTool.cs`

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ToolExecution.Tools;

public class ShellExecuteTool : IToolExecutor
{
    private readonly ILogger<ShellExecuteTool> _logger;
    private readonly string _workspaceRoot;
    private readonly int _timeoutSeconds;

    // Minimal blocked command patterns (until ActionGate from Story 11.3 is available)
    private static readonly Regex[] BlockedPatterns = new[]
    {
        new Regex(@"\brm\s+-rf\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bsudo\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bmkfs\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bdd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b(chmod|chown)\s+.*(-R|--recursive)\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bcurl\b.*\|\s*(bash|sh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bwget\b.*\|\s*(bash|sh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b:>\s*/", RegexOptions.Compiled), // truncating system files
    };

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
        _workspaceRoot = configuration["ToolExecution:WorkspaceRoot"]
                       ?? Environment.CurrentDirectory;
        _timeoutSeconds = int.TryParse(configuration["ToolExecution:ShellTimeoutSeconds"], out var t)
            ? t : 60;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            var command = args.GetProperty("command").GetString()
                        ?? throw new ArgumentException("Missing 'command' argument");

            // Validate against blocked patterns
            foreach (var pattern in BlockedPatterns)
            {
                if (pattern.IsMatch(command))
                {
                    _logger.LogWarning("Blocked shell command: {Command}", command);
                    return new ToolExecutionResult(toolCallId, ToolName, false,
                        $"Command blocked by security policy: {command}", sw.ElapsedMilliseconds);
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token);

            var stdout = stdoutBuilder.ToString();
            var stderr = stderrBuilder.ToString();
            var output = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout))
                output.AppendLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.AppendLine("--- stderr ---");
                output.AppendLine(stderr);
            }
            output.AppendLine($"Exit code: {process.ExitCode}");

            _logger.LogDebug("shell_execute: '{Command}' exit={ExitCode}", command, process.ExitCode);

            return new ToolExecutionResult(toolCallId, ToolName, process.ExitCode == 0,
                ToolOutputHelper.Truncate(output.ToString()), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Command timed out after {_timeoutSeconds} seconds", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Shell execution error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
```

---

### Task 11: Create GitOperationsTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/GitOperationsTool.cs`

Key features: wraps `git` CLI, supports subcommands `status`, `diff`, `log`, `add`, `commit`, `push`. Validates subcommand against an allowlist. Uses `System.Diagnostics.Process`.

```csharp
// Similar structure to ShellExecuteTool but with:
// - Subcommand allowlist: status, diff, log, add, commit, push, branch, checkout
// - Arguments JSON: { "subcommand": "status", "args": "--short" }
// - Git binary path from config or default "git"
// - WorkingDirectory = workspaceRoot
```

---

### Task 12: Create RunTestsTool

**File to create**: `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/RunTestsTool.cs`

Key features: runs a configurable test command (`dotnet test`, `pnpm test`), captures stdout/stderr, configurable timeout (default 120s).

```csharp
// Similar structure to ShellExecuteTool but with:
// - Test command from config: ToolExecution:TestCommand (default: "dotnet test")
// - Arguments JSON: { "filter": "ClassName.MethodName", "project": "path/to.csproj" }
// - Longer default timeout (120s)
// - Structured output with pass/fail counts if parseable
```

---

### Task 13: Register All Services in Program.cs

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

**Add after line 99** (`builder.Services.AddHttpClient();`):

```csharp
// Tool execution services — used by the agentic tool loop in CallLlmInlineActivity
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.FileReadTool>();
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.FileWriteTool>();
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.SearchCodeTool>();
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.ShellExecuteTool>();
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.GitOperationsTool>();
builder.Services.AddTransient<Tamma.Activities.ToolExecution.IToolExecutor, Tamma.Activities.ToolExecution.Tools.RunTestsTool>();
builder.Services.AddSingleton<Tamma.Activities.ToolExecution.IToolExecutorRegistry, Tamma.Activities.ToolExecution.ToolExecutorRegistry>();
```

---

## Files to Create (Full List)

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutor.cs` | Interface |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolExecutorRegistry.cs` | Registry interface |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolExecutorRegistry.cs` | Registry implementation |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolOutputHelper.cs` | 50KB truncation utility |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/PathValidator.cs` | Directory traversal prevention |
| 6 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileReadTool.cs` | File read implementation |
| 7 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/FileWriteTool.cs` | File write implementation |
| 8 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/SearchCodeTool.cs` | Code search implementation |
| 9 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/ShellExecuteTool.cs` | Shell execution implementation |
| 10 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/GitOperationsTool.cs` | Git operations implementation |
| 11 | `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/Tools/RunTestsTool.cs` | Test runner implementation |

## Files to Modify

| # | File Path | Line(s) | Change |
|---|-----------|---------|--------|
| 1 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | After L391 | Add `ToolExecutionResult`, `ToolLoopConfig`, `ConversationMessage`, `ToolCallInfo`, `StopReason` |
| 2 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | L381-391 (`NormalizedLlmResponse`) | Add `StopReason StopReason` property |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | L13-47 (`LlmCallWorkflowInput`) | Add `EnableToolLoop`, `ToolLoopConfig` properties |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` | L52-89 (`LlmCallWorkflowOutput`) | Add `ToolLoopTokens`, `ToolLoopTurns`, `ToolLoopExhausted` properties |
| 5 | `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | After L99 | Register all tool executors and registry in DI |

---

## Test Cases

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ToolExecutorRegistryTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 1 | `GetExecutor_RegisteredTool_ReturnsExecutor` | Lookup by exact name returns the correct executor |
| 2 | `GetExecutor_UnknownTool_ReturnsNull` | Lookup by unknown name returns null (not exception) |
| 3 | `GetExecutor_CaseInsensitive_ReturnsExecutor` | "FILE_READ" finds "file_read" |
| 4 | `IsAllowed_NullAllowlist_ReturnsTrue` | Null allowlist means all tools allowed |
| 5 | `IsAllowed_EmptyAllowlist_ReturnsTrue` | Empty array means all tools allowed |
| 6 | `IsAllowed_ToolInAllowlist_ReturnsTrue` | Named tool in list returns true |
| 7 | `IsAllowed_ToolNotInAllowlist_ReturnsFalse` | Named tool not in list returns false |
| 8 | `GetAll_ReturnsAllRegistered` | Returns all 6 tools |
| 9 | `GetAllowed_FiltersCorrectly` | Returns subset matching allowlist |
| 10 | `DuplicateRegistration_KeepsFirst` | Second tool with same name is ignored |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/FileReadToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 11 | `ExecuteAsync_ExistingFile_ReturnsContent` | Reads and returns file content |
| 12 | `ExecuteAsync_PathTraversal_ReturnsDenied` | `../../../etc/passwd` is rejected |
| 13 | `ExecuteAsync_FileNotFound_ReturnsError` | Missing file returns `Success=false` |
| 14 | `ExecuteAsync_LargeFile_OutputTruncated` | 100KB file truncated to 50KB with suffix |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/FileWriteToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 15 | `ExecuteAsync_NewFile_CreatesFile` | Creates file with correct content |
| 16 | `ExecuteAsync_ExistingFile_Overwrites` | Overwrites existing file |
| 17 | `ExecuteAsync_PathTraversal_ReturnsDenied` | Directory traversal rejected |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/SearchCodeToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 18 | `ExecuteAsync_PatternFound_ReturnsMatches` | Regex match returns file:line format |
| 19 | `ExecuteAsync_NoMatches_ReturnsEmpty` | No matches returns informative message |
| 20 | `ExecuteAsync_MaxResultsRespected` | Stops after max_results matches |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/ShellExecuteToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 21 | `ExecuteAsync_ValidCommand_ReturnsOutput` | `echo hello` returns "hello" |
| 22 | `ExecuteAsync_BlockedCommand_ReturnsDenied` | `sudo rm -rf /` is blocked |
| 23 | `ExecuteAsync_Timeout_ReturnsTimeoutError` | Long-running command times out |
| 24 | `ExecuteAsync_StderrCaptured_IncludedInOutput` | stderr included in output |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/GitOperationsToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 25 | `ExecuteAsync_Status_ReturnsOutput` | `git status` returns output |
| 26 | `ExecuteAsync_UnknownSubcommand_ReturnsError` | Invalid subcommand returns error |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/Tools/RunTestsToolTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 27 | `ExecuteAsync_ValidCommand_CapturesOutput` | Test command runs and captures |
| 28 | `ExecuteAsync_Timeout_ReturnsError` | Long test times out |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/ToolOutputHelperTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 29 | `Truncate_ShortOutput_ReturnsUnchanged` | Under 50KB returned as-is |
| 30 | `Truncate_LargeOutput_TruncatesWithSuffix` | Over 50KB gets suffix |

**Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/ToolExecution/PathValidatorTests.cs`

| # | Test Method | Verifies |
|---|-------------|----------|
| 31 | `ResolveSafePath_ValidRelative_ReturnsAbsolute` | `src/foo.cs` resolves correctly |
| 32 | `ResolveSafePath_Traversal_Throws` | `../../etc/passwd` throws |
| 33 | `ResolveSafePath_EmptyPath_Throws` | Empty string throws ArgumentException |

---

## Verification Steps

1. **Build**: `cd apps/tamma-elsa && dotnet build` — must compile without errors
2. **Tests**: `cd apps/tamma-elsa && dotnet test --filter "ToolExecution"` — all 33 tests pass
3. **DI check**: Start the ELSA server and verify logs show `"ToolExecutorRegistry initialized with 6 tools"`
4. **Manual smoke test**: Use the ELSA API to trigger a workflow that lists tools — verify all 6 tool names appear
5. **Serialization**: Verify `ToolExecutionResult`, `ConversationMessage`, `ToolCallInfo` round-trip through JSON

---

## Risks and Edge Cases

| Risk | Mitigation |
|------|------------|
| **Path traversal via symlinks** | `Path.GetFullPath()` resolves symlinks; the resolved path is validated against workspace root |
| **Shell injection** | Blocked command patterns cover the most dangerous cases; future integration with ActionGate (Story 11.3) will add comprehensive validation |
| **Process zombies from ShellExecuteTool** | `CancellationToken` with timeout + `process.Kill()` in finally block if needed; `using` ensures disposal |
| **Large binary files in FileReadTool** | Output truncation at 50KB prevents memory issues; consider adding a file-size pre-check |
| **Regex DoS in SearchCodeTool** | `TimeSpan.FromSeconds(5)` timeout on regex compilation prevents catastrophic backtracking |
| **Concurrent DI resolution** | `ToolExecutorRegistry` is registered as `Singleton`; tool executors as `Transient` — thread-safe |
| **Case sensitivity of tool names** | Registry uses `StringComparer.OrdinalIgnoreCase` — consistent with LLM tool name handling |
| **Missing config for workspace root** | Falls back to `Environment.CurrentDirectory` — acceptable for development, should be required in production |

---

## Implementation Order

1. Models (LlmCallModels.cs) — everything depends on these
2. PathValidator + ToolOutputHelper — shared utilities
3. IToolExecutor + IToolExecutorRegistry — interfaces
4. ToolExecutorRegistry — implementation
5. FileReadTool + FileWriteTool — simplest tools, validate the pattern
6. SearchCodeTool — medium complexity
7. ShellExecuteTool + GitOperationsTool + RunTestsTool — process-based tools
8. Program.cs DI registration
9. Tests (in parallel with implementation)
