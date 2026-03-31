# Story 12.6: Tool Executor Enhancements — Output Parsing, Chaining, Caching, and New Tools

Status: ready-for-dev

## Story

As a **platform engineer**,
I want structured tool output parsing, tool output chaining, read caching, and additional tool executors for dependency analysis and web search,
so that the agentic tool loop operates more efficiently (fewer redundant operations, better context utilization) and has access to broader capabilities needed for autonomous development tasks.

## Motivation

The current tool executor system (Stories 12.1–12.4) provides a solid foundation with 6 tools (file_read, file_write, shell_execute, git_operations, search_code, run_tests), parallel execution, and context compaction. However, the deep audit reveals several efficiency gaps and missing capabilities:

### Audit Findings — Efficiency Gaps

1. **No structured output parsing**: All tool results are raw text strings. When the LLM reads a file, the entire content is returned as a flat string. When tests run, the entire stdout/stderr is returned. The LLM must re-parse this text to extract relevant information, wasting tokens and increasing error rates. Structured extraction (e.g., test result summaries, file metadata, search result counts) would reduce token consumption and improve LLM decision quality.

2. **No tool output chaining**: Common multi-tool patterns require the LLM to manually coordinate outputs:
   - "Read file X, find function Y, then read the test file for Y" requires 3 separate LLM turns
   - "Search for pattern, then read each matching file" requires N+1 turns
   - "Run tests, then read the failing test file" requires 2 turns
   These patterns could be optimized with declarative chaining, or at minimum with a "batch" tool that combines multiple operations.

3. **No file read caching**: The LLM frequently re-reads the same file across multiple tool loop turns (e.g., reads `src/foo.cs` in turn 1 to understand it, then re-reads in turn 5 to verify changes). Each read is a full disk I/O operation and consumes context window tokens. A per-session read cache would eliminate redundant reads.

4. **Missing tools for autonomous development**:
   - **Web search / documentation lookup**: The LLM cannot look up API documentation, search for error messages, or find relevant examples. This is critical for autonomous coding tasks where the LLM encounters unfamiliar libraries or APIs.
   - **Dependency analysis**: No tool to analyze project dependencies (package.json, .csproj, requirements.txt), detect outdated packages, or check for known vulnerabilities.
   - **Architecture/structure overview**: No tool to generate a project structure tree or dependency graph, forcing the LLM to do many file_read + search_code calls to understand project layout.
   - **Diff/patch tool**: No dedicated tool for viewing diffs between files or applying patches. The LLM must use `git diff` or read entire files to compare.

5. **No tool usage analytics**: There is no visibility into which tools are used most frequently, which have the highest failure rates, which consume the most time, or which patterns of tool usage are most common. This data is needed to optimize tool implementations and identify missing capabilities.

6. **Output size issues**: `ToolOutputHelper.Truncate()` uses a flat 50KB limit for all tools. But different tools have different output characteristics:
   - `file_read` on a large file should truncate from the middle or end, not just chop at 50KB
   - `search_code` results are more valuable in the first few hundred lines
   - `run_tests` output should preserve the failure summary at the end, not truncate it
   - `shell_execute` output importance varies by command

## Acceptance Criteria

### Structured Output Parsing

1. `ToolOutputParser` class extracts structured metadata from tool results based on tool type:
   - `file_read`: `{ fileSize, lineCount, language, hasCredentials }` prepended to output
   - `search_code`: `{ totalMatches, filesMatched, truncated }` prepended to output
   - `run_tests`: `{ totalTests, passed, failed, skipped, duration, failingSummary }` extracted from output
   - `shell_execute`: `{ exitCode, stdoutLines, stderrLines }` structured header
   - `git_operations`: `{ subcommand, success, changedFiles }` structured header
2. Structured metadata is prepended as a compact JSON header before the raw output, so the LLM can quickly parse key information without reading the entire output

### Tool Output Chaining

3. A new `batch_operations` tool accepts an array of tool calls and executes them sequentially, passing results between steps:
   - Supports variable substitution: `${step_0.output}` references the output of step 0
   - Maximum chain length: 5 steps (configurable)
   - If any step fails, subsequent steps are skipped and the partial results are returned
4. Common pattern shortcuts:
   - `search_and_read`: searches for a pattern, then reads the first N matching files
   - `read_and_diff`: reads two files and returns a unified diff

### File Read Caching

5. `FileReadCache` maintains a per-session cache of file contents keyed by absolute path
6. Cache is invalidated when `file_write` writes to a cached path (cross-tool coordination via `IFileSystemTool`)
7. Cache entries include: content, read timestamp, file size, last-modified timestamp
8. Cache hit returns content without disk I/O; logs "cache hit" for analytics
9. Cache has a configurable max size (default: 10MB total, 50 entries) with LRU eviction
10. Cache is scoped per workflow execution (no cross-session leakage)

### New Tools

11. **ListDirectoryTool** (`list_directory`): Lists files and directories at a given path with optional depth, glob filter, and file metadata (size, modified date). Helps the LLM understand project structure without reading individual files.

12. **DiffTool** (`diff_files`): Computes a unified diff between two files or between a file and provided content. Useful for reviewing changes before committing.

13. **DependencyAnalysisTool** (`analyze_dependencies`): Parses project dependency files (package.json, *.csproj, requirements.txt, go.mod, Cargo.toml) and returns structured dependency information: name, current version, latest version (if available via shell), direct vs transitive.

14. **ProjectOverviewTool** (`project_overview`): Generates a project structure tree (directories and files) with language detection, line counts per directory, and key file identification (entry points, config files, test directories). Cached per session.

### Tool Usage Analytics

15. `ToolUsageTracker` records per-session statistics: call count per tool, total execution time per tool, success/failure rate, average output size, cache hit rate
16. Statistics are logged at the end of each tool loop session (when `EmitLoopCompleted` fires)
17. Statistics are available via a method for programmatic access (e.g., for the dashboard)

### Smart Output Truncation

18. `ToolOutputHelper.Truncate()` enhanced with tool-aware truncation strategies:
   - `run_tests`: Preserves the last 5KB of output (contains test summary) and the first 5KB (contains early failures), truncating the middle
   - `search_code`: Preserves all results, truncates individual match context lines
   - `file_read`: For files >50KB, returns first 20KB + last 10KB with a truncation marker in the middle
   - Other tools: Current behavior (truncate from end)

## Technical Context

### Architecture

New tools follow the existing `IToolExecutor` pattern and are registered in DI:

```
LlmCall/Tools/
  ListDirectoryTool.cs       -- project structure exploration
  DiffTool.cs                -- unified diff computation
  DependencyAnalysisTool.cs  -- package analysis
  ProjectOverviewTool.cs     -- project structure tree
  BatchOperationsTool.cs     -- tool chaining
  ToolOutputParser.cs        -- structured metadata extraction
  FileReadCache.cs           -- per-session read cache
  ToolUsageTracker.cs        -- per-session analytics
```

### FileReadCache Design

```csharp
public class FileReadCache
{
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;
    private readonly long _maxTotalSizeBytes;
    private long _currentSizeBytes;

    public record CacheEntry(
        string Content,
        DateTimeOffset ReadAt,
        long FileSizeBytes,
        DateTimeOffset LastModified);

    public bool TryGet(string absolutePath, out string content);
    public void Set(string absolutePath, string content, long fileSize, DateTimeOffset lastModified);
    public void Invalidate(string absolutePath);
    public void InvalidateAll();
}
```

Cache coordination: `FileWriteTool` calls `_cache.Invalidate(path)` after successful writes. This requires `FileReadCache` to be injected into both `FileReadTool` and `FileWriteTool`.

### BatchOperationsTool Design

```csharp
public class BatchOperationsTool : IToolExecutor
{
    public string ToolName => "batch_operations";

    public Dictionary<string, object> InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["steps"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Array of tool calls to execute sequentially",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["tool"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Tool name to execute"
                        },
                        ["arguments"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["description"] = "Arguments for the tool (supports ${step_N.output} references)"
                        }
                    }
                }
            }
        },
        ["required"] = new[] { "steps" }
    };
}
```

### Smart Truncation Design

```csharp
public static class ToolOutputHelper
{
    public static string Truncate(string output, string toolName, /* existing params */)
    {
        return toolName switch
        {
            "run_tests" => TruncateTestOutput(output),
            "file_read" => TruncateFileContent(output),
            "search_code" => TruncateSearchResults(output),
            _ => TruncateDefault(output) // Current behavior
        };
    }

    private static string TruncateTestOutput(string output)
    {
        // Preserve first 5KB + last 5KB, truncate middle
        // Test runners put the summary at the end
    }

    private static string TruncateFileContent(string output)
    {
        // For large files: first 20KB + truncation marker + last 10KB
    }
}
```

### Configuration

```json
{
  "ToolExecution": {
    "Cache": {
      "Enabled": true,
      "MaxEntries": 50,
      "MaxTotalSizeBytes": 10485760
    },
    "BatchOperations": {
      "MaxSteps": 5,
      "MaxTotalTimeoutMs": 120000
    },
    "Analytics": {
      "Enabled": true,
      "LogOnCompletion": true
    },
    "SmartTruncation": {
      "Enabled": true,
      "TestOutputPreserveEndBytes": 5120,
      "FileReadPreserveEndBytes": 10240
    }
  }
}
```

## Dependencies

- Story 12.1 (IToolExecutor, IToolExecutorRegistry — already implemented)
- Story 12.2 (AgenticToolLoop — already implemented)
- Story 12.4 (ParallelToolExecutor, IFileSystemTool — already implemented)

## Testing Strategy

### Unit Tests (40+)

**ToolOutputParser (8 tests)**:
- Parses `run_tests` output to extract pass/fail/skip counts
- Parses `search_code` output to extract match count
- Parses `file_read` output to extract line count and language
- Handles malformed output gracefully (returns raw output)
- Handles empty output
- Parses `git_operations` output to extract changed files
- Parses `shell_execute` output to extract exit code
- Performance: parses 50KB output in under 1ms

**FileReadCache (8 tests)**:
- Cache miss returns false
- Cache hit returns previously cached content
- Cache invalidation removes entry
- Write to cached path invalidates cache
- LRU eviction when max entries exceeded
- Total size limit triggers eviction of oldest entries
- Thread-safe concurrent access (parallel reads and writes)
- Cache is isolated per instance (no cross-session leakage)

**BatchOperationsTool (6 tests)**:
- Executes 3-step chain successfully
- Variable substitution replaces `${step_0.output}` with actual output
- Fails on step 2, returns partial results with error
- Rejects chains longer than max steps
- Handles invalid tool name in chain
- Timeout per chain execution

**ListDirectoryTool (4 tests)**:
- Lists files in workspace root
- Respects depth parameter
- Applies glob filter
- Blocks paths outside workspace

**DiffTool (4 tests)**:
- Computes diff between two workspace files
- Computes diff between file and provided content
- Handles identical files (empty diff)
- Blocks paths outside workspace

**DependencyAnalysisTool (4 tests)**:
- Parses package.json dependencies
- Parses .csproj PackageReference elements
- Parses requirements.txt entries
- Handles missing dependency file gracefully

**ProjectOverviewTool (3 tests)**:
- Generates project tree with correct depth
- Detects languages from file extensions
- Caches result per session

**ToolUsageTracker (3 tests)**:
- Records call count and duration per tool
- Computes success/failure rates
- Reports cache hit rate

**Smart Truncation (4 tests)**:
- `run_tests` preserves summary at end of output
- `file_read` preserves head and tail of large files
- `search_code` preserves all result lines, truncates context
- Default truncation behavior unchanged for unknown tools

### Integration Tests (4+)

- End-to-end: LLM reads file, file is cached, re-read hits cache, write invalidates cache
- End-to-end: LLM uses batch_operations to search and read matching files in one tool call
- End-to-end: Tool loop completes, usage analytics logged with correct counts
- End-to-end: Large test output is smart-truncated with summary preserved

## Estimation

**Size**: XL (5-7 days)
**Risk**: Medium — new tools need careful path validation; cache invalidation logic is subtle
**Confidence**: High — all patterns follow existing IToolExecutor interface

## Out of Scope

- Web search tool (requires external API integration — separate story)
- Architecture diagram generation (requires graphviz or mermaid rendering — separate story)
- Cross-session caching (requires external cache like Redis)
- Tool recommendation engine (ML-based tool selection)
- Streaming tool output (real-time output from long-running commands)
