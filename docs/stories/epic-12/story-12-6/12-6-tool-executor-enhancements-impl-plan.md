# Story 12.6: Tool Executor Enhancements — Implementation Plan

## Overview

This plan adds efficiency improvements and new tools to the agentic tool loop: structured output parsing, file read caching, tool chaining via batch operations, 4 new tools (list_directory, diff_files, analyze_dependencies, project_overview), tool usage analytics, and smart output truncation.

**Dependencies:** Stories 12.1, 12.2, 12.4 (all already implemented)

---

## Step-by-Step Implementation Tasks

### Task 1: FileReadCache — Per-session file content cache

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/FileReadCache.cs`

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.LlmCall.Tools;

public class FileReadCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 50;
    public long MaxTotalSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
}

/// <summary>
/// Per-session file read cache. Scoped lifetime ensures isolation between
/// workflow executions. Thread-safe via locking (session-scoped means low contention).
///
/// Cache is invalidated automatically when FileWriteTool writes to a cached path.
/// </summary>
public class FileReadCache
{
    private readonly ILogger<FileReadCache>? _logger;
    private readonly FileReadCacheOptions _options;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly LinkedList<string> _accessOrder; // For LRU eviction
    private long _currentSizeBytes;
    private int _hits;
    private int _misses;

    public record CacheEntry(
        string Content,
        DateTimeOffset ReadAt,
        long FileSizeBytes,
        DateTimeOffset LastModified,
        LinkedListNode<string> AccessNode);

    public FileReadCache(
        IOptions<FileReadCacheOptions>? options = null,
        ILogger<FileReadCache>? logger = null)
    {
        _options = options?.Value ?? new FileReadCacheOptions();
        _logger = logger;
        _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        _accessOrder = new LinkedList<string>();
    }

    /// <summary>
    /// Try to get a cached file. Returns true on cache hit.
    /// Validates that the file hasn't been modified since caching by checking
    /// the last-modified timestamp.
    /// </summary>
    public bool TryGet(string absolutePath, out string content)
    {
        content = string.Empty;

        if (!_options.Enabled)
        {
            _misses++;
            return false;
        }

        lock (_cache)
        {
            if (!_cache.TryGetValue(absolutePath, out var entry))
            {
                _misses++;
                return false;
            }

            // Check if file was modified on disk since caching
            try
            {
                var currentModified = File.GetLastWriteTimeUtc(absolutePath);
                if (currentModified > entry.LastModified.UtcDateTime)
                {
                    // File changed on disk — invalidate
                    RemoveEntry(absolutePath);
                    _misses++;
                    _logger?.LogDebug("Cache invalidated (file modified on disk): {Path}", absolutePath);
                    return false;
                }
            }
            catch
            {
                // Can't check — treat as miss
                _misses++;
                return false;
            }

            // Move to front of LRU list
            _accessOrder.Remove(entry.AccessNode);
            _accessOrder.AddFirst(entry.AccessNode);

            content = entry.Content;
            _hits++;

            _logger?.LogDebug("Cache hit: {Path}, size={SizeBytes}B", absolutePath, entry.FileSizeBytes);
            return true;
        }
    }

    /// <summary>
    /// Cache a file's content. Evicts LRU entries if size/count limits are exceeded.
    /// </summary>
    public void Set(string absolutePath, string content, long fileSize)
    {
        if (!_options.Enabled) return;

        lock (_cache)
        {
            // Remove existing entry if present
            if (_cache.ContainsKey(absolutePath))
                RemoveEntry(absolutePath);

            // Evict until we have room
            var contentSize = (long)content.Length * 2; // Approximate: 2 bytes per char in .NET
            while ((_currentSizeBytes + contentSize > _options.MaxTotalSizeBytes ||
                    _cache.Count >= _options.MaxEntries) && _accessOrder.Count > 0)
            {
                var lruKey = _accessOrder.Last!.Value;
                RemoveEntry(lruKey);
                _logger?.LogDebug("Cache evicted (LRU): {Path}", lruKey);
            }

            DateTimeOffset lastModified;
            try
            {
                lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(absolutePath), TimeSpan.Zero);
            }
            catch
            {
                lastModified = DateTimeOffset.UtcNow;
            }

            var node = _accessOrder.AddFirst(absolutePath);
            _cache[absolutePath] = new CacheEntry(content, DateTimeOffset.UtcNow, fileSize, lastModified, node);
            _currentSizeBytes += contentSize;

            _logger?.LogDebug("Cache set: {Path}, size={SizeBytes}B, entries={EntryCount}",
                absolutePath, contentSize, _cache.Count);
        }
    }

    /// <summary>
    /// Invalidate a specific path (called by FileWriteTool after writing).
    /// </summary>
    public void Invalidate(string absolutePath)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(absolutePath))
            {
                RemoveEntry(absolutePath);
                _logger?.LogDebug("Cache invalidated (explicit): {Path}", absolutePath);
            }
        }
    }

    /// <summary>Get cache statistics for analytics.</summary>
    public (int Hits, int Misses, int EntryCount, long SizeBytes) GetStats()
    {
        lock (_cache)
        {
            return (_hits, _misses, _cache.Count, _currentSizeBytes);
        }
    }

    private void RemoveEntry(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            _accessOrder.Remove(entry.AccessNode);
            _currentSizeBytes -= entry.Content.Length * 2;
            _cache.Remove(key);
        }
    }
}
```

**Integrate into FileReadTool:**

Add `FileReadCache` as a constructor dependency. Before disk I/O:

```csharp
// In FileReadTool.ExecuteAsync, after PathValidator.ResolveSafePath:
if (_cache?.TryGet(resolvedPath, out var cachedContent) == true)
{
    var cachedOutput = ToolOutputHelper.Truncate(cachedContent, _logger, ToolName, toolCallId);
    var cachedResult = new ToolExecutionResult(toolCallId, ToolName, true,
        "[cached] " + cachedOutput, sw.ElapsedMilliseconds);
    LogCompletion(toolCallId, cachedResult);
    return cachedResult;
}

// After reading from disk:
var content = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
_cache?.Set(resolvedPath, content, new FileInfo(resolvedPath).Length);
```

**Integrate into FileWriteTool:**

Add `FileReadCache` as a constructor dependency. After successful write:

```csharp
// After File.WriteAllTextAsync:
_cache?.Invalidate(resolvedPath);
```

**Tests (8):** See acceptance criteria in story file.

---

### Task 2: ToolOutputParser — Structured metadata extraction

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputParser.cs`

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Extracts structured metadata from tool output based on tool type.
/// Prepends a compact JSON header to the raw output so the LLM can
/// quickly parse key information without scanning the entire text.
/// </summary>
public static class ToolOutputParser
{
    /// <summary>
    /// Add structured metadata header to tool output.
    /// Format: [meta: {"key": "value", ...}]\n\n{raw_output}
    /// </summary>
    public static string AddMetadata(string toolName, string output, bool success)
    {
        if (string.IsNullOrEmpty(output))
            return output ?? string.Empty;

        var metadata = toolName switch
        {
            "file_read" => ExtractFileReadMeta(output),
            "search_code" => ExtractSearchMeta(output),
            "run_tests" => ExtractTestMeta(output),
            "shell_execute" => ExtractShellMeta(output, success),
            "git_operations" => ExtractGitMeta(output),
            _ => null
        };

        if (metadata == null || metadata.Count == 0)
            return output;

        var metaJson = JsonSerializer.Serialize(metadata);
        return $"[meta: {metaJson}]\n\n{output}";
    }

    private static Dictionary<string, object>? ExtractFileReadMeta(string output)
    {
        var lines = output.Split('\n');
        var meta = new Dictionary<string, object>
        {
            ["lineCount"] = lines.Length,
            ["sizeChars"] = output.Length
        };

        // Detect language from content heuristics
        var lang = DetectLanguage(output);
        if (lang != null) meta["language"] = lang;

        return meta;
    }

    private static Dictionary<string, object>? ExtractSearchMeta(string output)
    {
        if (output.StartsWith("No matches found"))
            return new Dictionary<string, object> { ["totalMatches"] = 0 };

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = new HashSet<string>();
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
                files.Add(line[..colonIdx]);
        }

        return new Dictionary<string, object>
        {
            ["totalMatches"] = lines.Length,
            ["filesMatched"] = files.Count
        };
    }

    private static Dictionary<string, object>? ExtractTestMeta(string output)
    {
        var meta = new Dictionary<string, object>();

        // dotnet test output patterns
        var passedMatch = Regex.Match(output, @"Passed:\s*(\d+)");
        var failedMatch = Regex.Match(output, @"Failed:\s*(\d+)");
        var skippedMatch = Regex.Match(output, @"Skipped:\s*(\d+)");
        var totalMatch = Regex.Match(output, @"Total:\s*(\d+)");

        if (totalMatch.Success)
        {
            meta["totalTests"] = int.Parse(totalMatch.Groups[1].Value);
            if (passedMatch.Success) meta["passed"] = int.Parse(passedMatch.Groups[1].Value);
            if (failedMatch.Success) meta["failed"] = int.Parse(failedMatch.Groups[1].Value);
            if (skippedMatch.Success) meta["skipped"] = int.Parse(skippedMatch.Groups[1].Value);
        }

        // pnpm/vitest output patterns
        var vitestMatch = Regex.Match(output, @"Tests\s+(\d+)\s+passed.*?(\d+)\s+failed", RegexOptions.Singleline);
        if (vitestMatch.Success && !totalMatch.Success)
        {
            meta["passed"] = int.Parse(vitestMatch.Groups[1].Value);
            meta["failed"] = int.Parse(vitestMatch.Groups[2].Value);
        }

        // Extract exit code
        var exitCodeMatch = Regex.Match(output, @"Exit code:\s*(-?\d+)");
        if (exitCodeMatch.Success)
            meta["exitCode"] = int.Parse(exitCodeMatch.Groups[1].Value);

        return meta.Count > 0 ? meta : null;
    }

    private static Dictionary<string, object>? ExtractShellMeta(string output, bool success)
    {
        var meta = new Dictionary<string, object> { ["success"] = success };

        var exitCodeMatch = Regex.Match(output, @"Exit code:\s*(-?\d+)");
        if (exitCodeMatch.Success)
            meta["exitCode"] = int.Parse(exitCodeMatch.Groups[1].Value);

        var lines = output.Split('\n');
        var stderrIdx = Array.FindIndex(lines, l => l.Contains("--- stderr ---"));
        meta["stdoutLines"] = stderrIdx >= 0 ? stderrIdx : lines.Length;
        if (stderrIdx >= 0)
            meta["stderrLines"] = lines.Length - stderrIdx - 1;

        return meta;
    }

    private static Dictionary<string, object>? ExtractGitMeta(string output)
    {
        var meta = new Dictionary<string, object>();

        var exitCodeMatch = Regex.Match(output, @"Exit code:\s*(-?\d+)");
        if (exitCodeMatch.Success)
            meta["exitCode"] = int.Parse(exitCodeMatch.Groups[1].Value);

        // Count changed files from git status/diff output
        var fileChanges = Regex.Matches(output, @"^\s*[MADRCU?!]\s+", RegexOptions.Multiline);
        if (fileChanges.Count > 0)
            meta["changedFiles"] = fileChanges.Count;

        return meta.Count > 0 ? meta : null;
    }

    private static string? DetectLanguage(string content)
    {
        // Simple heuristic based on content patterns
        if (content.Contains("namespace ") && content.Contains("class ")) return "csharp";
        if (content.Contains("import ") && content.Contains("from ")) return "typescript";
        if (content.Contains("def ") && content.Contains("import ")) return "python";
        if (content.Contains("package ") && content.Contains("func ")) return "go";
        if (content.Contains("fn ") && content.Contains("let ")) return "rust";
        return null;
    }
}
```

**Integration:** Called in `CallLlmInlineActivity` after tool execution, before adding the tool result message to conversation history:

```csharp
var toolOutput = result.Output;
// Add structured metadata header
toolOutput = ToolOutputParser.AddMetadata(toolCall.ToolName, toolOutput, result.Success);
// Then sanitize and add to messages...
```

**Tests (8):** See acceptance criteria in story file.

---

### Task 3: Smart Output Truncation

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs`

Add a new overload of `Truncate` that accepts the tool name for tool-aware truncation:

```csharp
/// <summary>
/// Tool-aware truncation that preserves the most valuable parts of output
/// based on tool type.
/// </summary>
public static string TruncateSmart(
    string output, string toolName,
    ILogger? logger = null, string? toolCallId = null)
{
    if (string.IsNullOrEmpty(output))
        return output ?? string.Empty;

    var totalBytes = Encoding.UTF8.GetByteCount(output);
    if (totalBytes <= MaxOutputBytes)
        return output;

    return toolName switch
    {
        "run_tests" => TruncatePreservingTail(output, logger, toolName, toolCallId, tailBytes: 5120),
        "file_read" => TruncateHeadAndTail(output, logger, toolName, toolCallId, headBytes: 20480, tailBytes: 10240),
        _ => Truncate(output, logger, toolName, toolCallId) // Existing behavior
    };
}

/// <summary>
/// Truncate preserving both head and tail of the output.
/// Useful for file content where both the beginning (imports, declarations)
/// and end (exports, main function) are important.
/// </summary>
private static string TruncateHeadAndTail(
    string output, ILogger? logger, string? toolName, string? toolCallId,
    int headBytes, int tailBytes)
{
    var totalBytes = Encoding.UTF8.GetByteCount(output);
    var headChars = (int)((long)output.Length * headBytes / totalBytes);
    var tailChars = (int)((long)output.Length * tailBytes / totalBytes);

    if (headChars + tailChars >= output.Length)
        return Truncate(output, logger, toolName, toolCallId);

    var marker = $"\n\n[... {totalBytes - headBytes - tailBytes} bytes truncated ...]\n\n";

    logger?.LogWarning(
        "Tool output smart-truncated (head+tail): {ToolName} {ToolCallId} original={OriginalSizeBytes}B",
        toolName, toolCallId, totalBytes);

    return string.Concat(
        output.AsSpan(0, headChars),
        marker,
        output.AsSpan(output.Length - tailChars));
}

/// <summary>
/// Truncate preserving the tail of the output.
/// Useful for test output where the summary is at the end.
/// </summary>
private static string TruncatePreservingTail(
    string output, ILogger? logger, string? toolName, string? toolCallId,
    int tailBytes)
{
    var totalBytes = Encoding.UTF8.GetByteCount(output);
    var headBytes = MaxOutputBytes - tailBytes - 120; // Reserve for markers
    var headChars = (int)((long)output.Length * headBytes / totalBytes);
    var tailChars = (int)((long)output.Length * tailBytes / totalBytes);

    if (headChars + tailChars >= output.Length)
        return Truncate(output, logger, toolName, toolCallId);

    var marker = $"\n\n[... {totalBytes - headBytes - tailBytes} bytes truncated (preserving tail) ...]\n\n";

    logger?.LogWarning(
        "Tool output smart-truncated (preserve tail): {ToolName} {ToolCallId} original={OriginalSizeBytes}B",
        toolName, toolCallId, totalBytes);

    return string.Concat(
        output.AsSpan(0, headChars),
        marker,
        output.AsSpan(output.Length - tailChars));
}
```

**Tests (4):** See acceptance criteria in story file.

---

### Task 4: ListDirectoryTool — Project structure exploration

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ListDirectoryTool.cs`

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Lists files and directories at a given path with optional depth and glob filter.
/// Output is a tree-like format similar to the `tree` command.
/// </summary>
public class ListDirectoryTool : IToolExecutor
{
    private readonly ILogger<ListDirectoryTool> _logger;
    private readonly string _workspaceRoot;

    public string ToolName => "list_directory";

    public string Description =>
        "List files and directories at the given path (relative to workspace root) with optional depth and glob filter. " +
        "Returns a tree-like structure showing the directory contents.";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Directory path relative to workspace root (default: '.')"
            },
            ["depth"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum depth to recurse (default: 2, max: 5)"
            },
            ["glob"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional glob pattern to filter files (e.g., '*.cs', '*.ts')"
            }
        },
        ["required"] = Array.Empty<string>()
    };

    // Implementation follows the same pattern as FileReadTool:
    // - PathValidator.ResolveSafePath for traversal prevention
    // - SkipDirectories for ignoring node_modules, .git, etc.
    // - ToolOutputHelper.Truncate for output size limits
    // - Structured output with file sizes and last-modified dates
}
```

Full implementation follows the same patterns as `SearchCodeTool` (directory enumeration with skip list). Output format:

```
workspace/
  src/
    Controllers/
      UserController.cs (2.1KB, 2026-03-28)
      OrderController.cs (3.4KB, 2026-03-27)
    Models/
      User.cs (0.8KB, 2026-03-28)
  tests/
    UserControllerTests.cs (1.5KB, 2026-03-28)
  package.json (1.2KB, 2026-03-25)
```

**Tests (4):** See acceptance criteria in story file.

---

### Task 5: DiffTool — Unified diff computation

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/DiffTool.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Computes unified diff between two files or between a file and provided content.
/// Uses a simple line-by-line diff algorithm (longest common subsequence).
/// Does not shell out to external diff command — pure C# implementation.
/// </summary>
public class DiffTool : IToolExecutor
{
    public string ToolName => "diff_files";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path_a"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "First file path (relative to workspace root)"
            },
            ["path_b"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Second file path, or omit to compare path_a against 'content' parameter"
            },
            ["content"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Content to compare against path_a (used when path_b is not provided)"
            }
        },
        ["required"] = new[] { "path_a" }
    };

    // Implementation:
    // 1. Read both files (or file + provided content)
    // 2. Split into lines
    // 3. Compute LCS-based unified diff
    // 4. Format as unified diff with context lines (3 lines default)
    // 5. Return diff output or "Files are identical" if no differences
}
```

**Tests (4):** See acceptance criteria in story file.

---

### Task 6: DependencyAnalysisTool — Package dependency analysis

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/DependencyAnalysisTool.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Analyzes project dependency files and returns structured dependency information.
/// Supports: package.json, *.csproj, requirements.txt, go.mod, Cargo.toml.
/// </summary>
public class DependencyAnalysisTool : IToolExecutor
{
    public string ToolName => "analyze_dependencies";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Path to dependency file (e.g., 'package.json', 'src/MyProject.csproj'). " +
                    "If omitted, searches workspace root for known dependency files."
            }
        },
        ["required"] = Array.Empty<string>()
    };

    // Implementation:
    // 1. Auto-detect dependency file if path not provided
    // 2. Parse based on file type:
    //    - package.json: JSON parse dependencies + devDependencies
    //    - .csproj: XML parse PackageReference elements
    //    - requirements.txt: line-by-line parse name==version
    //    - go.mod: parse require blocks
    //    - Cargo.toml: parse [dependencies] section
    // 3. Return structured output:
    //    Dependencies (12 total):
    //      Production (8):
    //        express@4.18.2
    //        typescript@5.7.2
    //        ...
    //      Development (4):
    //        vitest@3.0.0
    //        ...
}
```

**Tests (4):** See acceptance criteria in story file.

---

### Task 7: ProjectOverviewTool — Project structure with language detection

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ProjectOverviewTool.cs`

```csharp
namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Generates a high-level project overview: directory tree with file counts,
/// language detection, key file identification (entry points, configs, tests).
/// Result is cached per session via FileReadCache.
/// </summary>
public class ProjectOverviewTool : IToolExecutor
{
    public string ToolName => "project_overview";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["depth"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum depth for the directory tree (default: 3, max: 5)"
            }
        },
        ["required"] = Array.Empty<string>()
    };

    // Output format:
    // Project: tamma-elsa
    // Root: /workspace/apps/tamma-elsa
    // Languages: C# (85%), JSON (10%), Markdown (5%)
    // Total files: 142, Total lines: ~28,500
    //
    // Key Files:
    //   Entry: src/Tamma.ElsaServer/Program.cs
    //   Config: appsettings.json, appsettings.Development.json
    //   Tests: tests/Tamma.Activities.Tests/
    //   CI: .github/workflows/ci.yml
    //
    // Directory Structure:
    //   src/
    //     Tamma.Activities/ (45 files, ~12,000 lines)
    //       LlmCall/ (18 files)
    //       Security/ (13 files)
    //       ToolExecution/ (4 files)
    //     Tamma.ElsaServer/ (12 files, ~3,500 lines)
    //   tests/
    //     Tamma.Activities.Tests/ (25 files, ~8,000 lines)
}
```

**Tests (3):** See acceptance criteria in story file.

---

### Task 8: ToolUsageTracker — Per-session analytics

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolUsageTracker.cs`

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Tracks per-session tool usage statistics. Scoped lifetime ensures
/// isolation between workflow executions.
/// </summary>
public class ToolUsageTracker
{
    private readonly ILogger<ToolUsageTracker>? _logger;
    private readonly ConcurrentDictionary<string, ToolStats> _stats = new();

    public class ToolStats
    {
        public int CallCount;
        public int SuccessCount;
        public int FailureCount;
        public long TotalDurationMs;
        public long TotalOutputBytes;
        public int CacheHits;
    }

    public ToolUsageTracker(ILogger<ToolUsageTracker>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Record a tool execution.</summary>
    public void Record(string toolName, bool success, long durationMs, int outputSizeBytes, bool cacheHit = false)
    {
        var stats = _stats.GetOrAdd(toolName, _ => new ToolStats());
        Interlocked.Increment(ref stats.CallCount);
        if (success) Interlocked.Increment(ref stats.SuccessCount);
        else Interlocked.Increment(ref stats.FailureCount);
        Interlocked.Add(ref stats.TotalDurationMs, durationMs);
        Interlocked.Add(ref stats.TotalOutputBytes, outputSizeBytes);
        if (cacheHit) Interlocked.Increment(ref stats.CacheHits);
    }

    /// <summary>Log session summary. Called when the tool loop completes.</summary>
    public void LogSummary(string workflowInstanceId)
    {
        if (_stats.IsEmpty) return;

        foreach (var (toolName, stats) in _stats)
        {
            _logger?.LogInformation(
                "Tool usage summary: WorkflowInstanceId={WorkflowInstanceId}, ToolName={ToolName}, " +
                "CallCount={CallCount}, SuccessRate={SuccessRate:P0}, TotalDurationMs={TotalDurationMs}, " +
                "AvgDurationMs={AvgDurationMs}, TotalOutputKB={TotalOutputKB}, CacheHits={CacheHits}",
                workflowInstanceId, toolName,
                stats.CallCount,
                stats.CallCount > 0 ? (double)stats.SuccessCount / stats.CallCount : 0,
                stats.TotalDurationMs,
                stats.CallCount > 0 ? stats.TotalDurationMs / stats.CallCount : 0,
                stats.TotalOutputBytes / 1024,
                stats.CacheHits);
        }
    }

    /// <summary>Get all stats for programmatic access.</summary>
    public IReadOnlyDictionary<string, ToolStats> GetAllStats()
        => new Dictionary<string, ToolStats>(_stats);
}
```

**Integration into CallLlmInlineActivity:**

After each tool execution:

```csharp
_usageTracker?.Record(toolCall.ToolName, result.Success,
    result.DurationMs, result.Output?.Length ?? 0, wasCacheHit);
```

At the end of the tool loop (where `EmitLoopCompleted` is called):

```csharp
_usageTracker?.LogSummary(workflowInstanceId);
```

**Tests (3):** See acceptance criteria in story file.

---

### Task 9: BatchOperationsTool — Tool chaining

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/BatchOperationsTool.cs`

This is the most complex new tool. Implementation approach:

1. Parse the `steps` array from arguments
2. For each step, resolve the tool executor from the registry
3. Before execution, perform variable substitution in arguments: replace `${step_N.output}` with the output of step N
4. Execute the tool
5. If the step fails and the LLM marked it as required (or by default), stop and return partial results
6. Collect all results into a structured output

```csharp
public class BatchOperationsTool : IToolExecutor
{
    private readonly IToolExecutorRegistry _registry;
    private readonly ILogger<BatchOperationsTool> _logger;
    private readonly int _maxSteps;

    public string ToolName => "batch_operations";

    // ... InputSchema as shown in story file ...

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolCallId, string argumentsJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var steps = args.GetProperty("steps").EnumerateArray().ToList();

        if (steps.Count > _maxSteps)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                $"Batch exceeds maximum of {_maxSteps} steps.", sw.ElapsedMilliseconds);
        }

        var results = new List<(string ToolName, bool Success, string Output)>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var toolName = step.GetProperty("tool").GetString()!;
            var stepArgs = step.GetProperty("arguments").GetRawText();

            // Variable substitution
            stepArgs = SubstituteVariables(stepArgs, results);

            var executor = _registry.GetExecutor(toolName);
            if (executor == null)
            {
                results.Add((toolName, false, $"Unknown tool: {toolName}"));
                break; // Stop on first failure
            }

            var result = await executor.ExecuteAsync($"{toolCallId}_step{i}", stepArgs, ct);
            results.Add((toolName, result.Success, result.Output ?? ""));

            if (!result.Success)
                break; // Stop on first failure
        }

        // Format output
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var (name, success, output) = results[i];
            sb.AppendLine($"=== Step {i}: {name} ({(success ? "OK" : "FAILED")}) ===");
            sb.AppendLine(output);
            sb.AppendLine();
        }

        return new ToolExecutionResult(toolCallId, ToolName,
            results.All(r => r.Success), sb.ToString(), sw.ElapsedMilliseconds);
    }

    private static string SubstituteVariables(string argsJson, List<(string, bool, string)> results)
    {
        // Replace ${step_N.output} with actual output from step N
        return Regex.Replace(argsJson, @"\$\{step_(\d+)\.output\}", match =>
        {
            var idx = int.Parse(match.Groups[1].Value);
            if (idx >= 0 && idx < results.Count)
                return JsonSerializer.Serialize(results[idx].Item3).Trim('"');
            return match.Value; // Leave unresolved
        });
    }
}
```

**Important security consideration:** The `BatchOperationsTool` must NOT bypass tool call validation. Each step must be individually validated against the allowlist. The tool calls the `IToolExecutorRegistry` which already respects allowlists.

**Tests (6):** See acceptance criteria in story file.

---

### Task 10: Register new tools and services in DI

**File to modify:** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

```csharp
// Tool executor enhancements
builder.Services.Configure<FileReadCacheOptions>(
    builder.Configuration.GetSection("ToolExecution:Cache"));

builder.Services.AddScoped<FileReadCache>();
builder.Services.AddScoped<ToolUsageTracker>();

// New tools
builder.Services.AddSingleton<IToolExecutor, ListDirectoryTool>();
builder.Services.AddSingleton<IToolExecutor, DiffTool>();
builder.Services.AddSingleton<IToolExecutor, DependencyAnalysisTool>();
builder.Services.AddSingleton<IToolExecutor, ProjectOverviewTool>();
builder.Services.AddScoped<IToolExecutor, BatchOperationsTool>();
```

Note: `FileReadCache` and `ToolUsageTracker` are `Scoped` for per-session isolation. `BatchOperationsTool` is also `Scoped` since it depends on `Scoped` services.

---

## Test Execution Order

1. `FileReadCache.Tests.cs` — standalone
2. `ToolOutputParser.Tests.cs` — standalone
3. `ToolOutputHelper.SmartTruncation.Tests.cs` — standalone
4. `ListDirectoryTool.Tests.cs` — requires workspace setup
5. `DiffTool.Tests.cs` — requires workspace setup
6. `DependencyAnalysisTool.Tests.cs` — requires test fixture files
7. `ProjectOverviewTool.Tests.cs` — requires workspace setup
8. `ToolUsageTracker.Tests.cs` — standalone
9. `BatchOperationsTool.Tests.cs` — requires registry + tool executors
10. Integration tests — full tool loop with caching and analytics

## Risk Mitigation

1. **Cache coherence**: Cache invalidation on write is explicit (not eventual). The `FileWriteTool.Invalidate()` call happens synchronously after write completion. Race condition is prevented by the existing per-path semaphore in `ParallelToolExecutor`.

2. **BatchOperationsTool security**: Each step in a batch goes through the normal tool execution path, including `ToolCallValidator` checks. The `${}` variable substitution is applied to argument JSON, not to tool names — so it cannot be used to bypass the tool allowlist.

3. **Output parser false positives**: The metadata extraction uses conservative regex patterns. If parsing fails, the raw output is returned unchanged. The `[meta: ...]` prefix is designed to be unambiguous and not conflict with tool output content.

4. **Performance**: New tools follow the same async patterns as existing tools. `FileReadCache` adds O(1) lookup overhead on cache hits. `ToolOutputParser` adds negligible overhead (regex matching on output that's already in memory).

5. **Backward compatibility**: All enhancements are additive. Existing tools continue to work without the new dependencies (nullable constructors with defaults). Cache, analytics, and batch operations require explicit opt-in.
