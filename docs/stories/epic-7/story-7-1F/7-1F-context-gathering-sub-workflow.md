# Story 7-1F: Context Gathering Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that collects contextual data from multiple sources in parallel (story metadata, commits, files, test results, session history, patterns) so that downstream workflows (assessment, planning, review, diagnosis) have rich, relevant context without exceeding token budgets.

## Description

Implement an ELSA code-first workflow (`ContextGatheringWorkflow`) that gathers project context from multiple sources using ELSA's `Fork`/`Join` parallel execution. Each source fetch is a separate ELSA activity — visible, auditable, and independently retryable. The workflow assembles the gathered data, applies priority-based budget trimming to fit within token limits, and returns a structured `CodeContextOutput`.

This workflow is called by assessment (7-1E), blocker diagnosis (7-1G), debugging (7-1I), and TDD (7-1H) sub-workflows. It does NOT call the LLM Call sub-workflow (7-1B) — it purely gathers raw context.

**Enhances**: Story 7-3 (Context Gathering Activity)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<ContextGatheringWorkflow>()`
- [ ] Visible in ELSA Studio as "Context Gathering" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `purpose` (enum: `Assessment`, `Planning`, `Review`, `Diagnosis`, `Implementation`) — determines context priority
  - `maxContextSize` (int, default: 50000) — max characters in output
  - `repositoryUrl` (string, optional) — override repo URL
- [ ] **Outputs**: `CodeContextOutput` record containing:
  - `story` — story metadata (title, description, acceptance criteria, labels)
  - `files` — relevant file contents (path, content, language, lastModified)
  - `patterns` — detected code patterns and similar implementations
  - `tests` — test file contents and recent test results
  - `history` — session history (previous states, decisions, feedback)
  - `commits` — recent commit log (last 7 days)
  - `summary` — auto-generated context summary
  - `totalCharacters` (int) — actual size of assembled context
  - `trimmedSources` (string[]) — sources that were trimmed to fit budget

### AC3: Parallel Source Fetching
- [ ] ELSA `Fork` activity launches all source fetches in parallel
- [ ] ELSA `Join` activity waits for all to complete (with timeout)
- [ ] Each source fetch is a custom activity (independently visible in Studio):
  1. `FetchStoryMetadata` — reads from mentorship session DB
  2. `FetchRecentCommits` — GitHub/Git API, last 7 days (configurable)
  3. `FetchFileContents` — up to 10 relevant files from repository
  4. `FetchTestResults` — latest CI results if available
  5. `FetchSessionHistory` — from `mentorship_events` table
  6. `FetchSimilarPatterns` — keyword-based search (Epic 6 indexer if available)
- [ ] Fork/Join timeout: 30 seconds (configurable)

### AC4: Graceful Degradation
- [ ] If any source fetch fails, the workflow continues with other sources
- [ ] Failed sources recorded in `failedSources[]` output field
- [ ] Minimum viable context: story metadata alone is sufficient to proceed
- [ ] Only fault if story metadata fetch fails (nothing else to work with)
- [ ] Each source fetch wrapped in try/catch with structured error logging

### AC5: Purpose-Based Priority
- [ ] Context priority order varies by `purpose`:
  - **Assessment**: story > history > files > patterns > commits > tests
  - **Planning**: story > files > patterns > tests > commits > history
  - **Review**: files > tests > story > commits > patterns > history
  - **Diagnosis**: files > tests > commits > history > story > patterns
  - **Implementation**: files > patterns > story > tests > commits > history
- [ ] Higher-priority sources get more of the budget allocation
- [ ] Priority order is configurable via `appsettings.json`

### AC6: Budget Enforcement
- [ ] `AssembleContext` activity merges all sources into unified output
- [ ] `ApplyBudget` activity trims to `maxContextSize`:
  - Sources trimmed in reverse priority order (lowest priority trimmed first)
  - Within a source, older/less-relevant items trimmed first
  - File contents: large files truncated with `[... truncated N chars ...]` marker
  - Commit history: oldest commits dropped first
  - Never trim story metadata (always included in full)
- [ ] `trimmedSources` output lists which sources were reduced and by how much

### AC7: File Relevance Scoring
- [ ] `FetchFileContents` uses relevance scoring to select the 10 most relevant files:
  - Files mentioned in story description: +10 points
  - Files in recent commits: +5 points per commit
  - Test files for mentioned source files: +8 points
  - Files in same directory as mentioned files: +3 points
  - Configuration files (when story mentions config): +6 points
- [ ] File list sorted by relevance score, top 10 fetched
- [ ] Max file size: 10KB per file (configurable), larger files truncated

### AC8: Observability
- [ ] Each source fetch logs: source name, fetch time, bytes retrieved, success/failure
- [ ] Total context gathering time logged
- [ ] Budget utilization logged: `{totalChars}/{maxContextSize} ({percentage}%)`
- [ ] Source contribution breakdown logged: `story=2KB, files=30KB, commits=5KB, ...`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: ContextGatheringWorkflow
├── ValidateInputs (fault if sessionId/storyId missing)
├── ResolvePriority (look up priority order for purpose)
├── Fork:
│   ├── FetchStoryMetadata → storyData
│   ├── FetchRecentCommits → commitsData
│   ├── FetchFileContents → filesData
│   ├── FetchTestResults → testsData
│   ├── FetchSessionHistory → historyData
│   └── FetchSimilarPatterns → patternsData
├── Join (wait all, timeout 30s)
├── CheckMinimumViable (fault if no story metadata)
├── AssembleContext (merge all sources)
├── ApplyBudget (trim to maxContextSize by priority)
├── GenerateSummary (brief auto-summary of gathered context)
└── SetOutputs (CodeContextOutput)
```

### Custom Activities

```csharp
// New activities in Tamma.Activities/Context/
[Activity("Tamma.Context", "Fetch Story Metadata", "Read story details from session database")]
public class FetchStoryMetadataActivity : CodeActivity<StoryMetadata> { ... }

[Activity("Tamma.Context", "Fetch Recent Commits", "Get commit history from Git platform")]
public class FetchRecentCommitsActivity : CodeActivity<CommitHistory> { ... }

[Activity("Tamma.Context", "Fetch File Contents", "Retrieve relevant source files")]
public class FetchFileContentsActivity : CodeActivity<FileContents> { ... }

[Activity("Tamma.Context", "Fetch Test Results", "Get latest CI/test results")]
public class FetchTestResultsActivity : CodeActivity<TestResults> { ... }

[Activity("Tamma.Context", "Fetch Session History", "Read mentorship session event log")]
public class FetchSessionHistoryActivity : CodeActivity<SessionHistory> { ... }

[Activity("Tamma.Context", "Fetch Similar Patterns", "Find similar code patterns")]
public class FetchSimilarPatternsActivity : CodeActivity<PatternMatches> { ... }

[Activity("Tamma.Context", "Assemble Context", "Merge all sources into unified context")]
public class AssembleContextActivity : CodeActivity<CodeContextOutput> { ... }

[Activity("Tamma.Context", "Apply Budget", "Trim context to fit within token budget")]
public class ApplyBudgetActivity : CodeActivity<CodeContextOutput> { ... }
```

### Output Schema

```csharp
public record CodeContextOutput
{
    public StoryMetadata Story { get; init; } = new();
    public List<FileEntry> Files { get; init; } = new();
    public List<PatternMatch> Patterns { get; init; } = new();
    public List<TestResult> Tests { get; init; } = new();
    public List<SessionEvent> History { get; init; } = new();
    public List<CommitEntry> Commits { get; init; } = new();
    public string Summary { get; init; } = string.Empty;
    public int TotalCharacters { get; init; }
    public List<string> TrimmedSources { get; init; } = new();
    public List<string> FailedSources { get; init; } = new();
}

public record StoryMetadata
{
    public string StoryId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> AcceptanceCriteria { get; init; } = new();
    public List<string> Labels { get; init; } = new();
    public int? SkillLevel { get; init; }
}

public record FileEntry
{
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public int RelevanceScore { get; init; }
    public bool Truncated { get; init; }
}
```

## Dependencies

- `Tamma.Data.Repositories.IMentorshipSessionRepository` (existing)
- `Tamma.Activities.Integration.GitHubActivity` (existing — for commits and files)
- `IHttpClientFactory` for Git platform API calls
- `IConfiguration` for priority and budget settings
- ELSA 3.x `Fork`, `Join`, `Flowchart` activities
- No dependency on LLM Call sub-workflow (7-1B) — this is pure data gathering

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs` | Create | Code-first workflow definition |
| `Tamma.Activities/Context/FetchStoryMetadataActivity.cs` | Create | Story metadata fetch |
| `Tamma.Activities/Context/FetchRecentCommitsActivity.cs` | Create | Git commit history fetch |
| `Tamma.Activities/Context/FetchFileContentsActivity.cs` | Create | Relevant file contents fetch |
| `Tamma.Activities/Context/FetchTestResultsActivity.cs` | Create | CI/test results fetch |
| `Tamma.Activities/Context/FetchSessionHistoryActivity.cs` | Create | Session event log fetch |
| `Tamma.Activities/Context/FetchSimilarPatternsActivity.cs` | Create | Pattern matching fetch |
| `Tamma.Activities/Context/AssembleContextActivity.cs` | Create | Context merging logic |
| `Tamma.Activities/Context/ApplyBudgetActivity.cs` | Create | Budget trimming logic |
| `Tamma.Activities/Context/Models/` | Create | DTOs (CodeContextOutput, etc.) |
| `Tamma.ElsaServer/Program.cs` | Modify | Register `ContextGatheringWorkflow` |

## Testing Strategy

### Unit Tests
- Priority resolution: correct order for each purpose value
- Budget trimming: verify lowest-priority sources trimmed first
- File relevance scoring: mentioned files rank higher, test files paired
- Graceful degradation: one source fails, others succeed
- Minimum viable check: story metadata missing → fault

### Integration Tests
- Full workflow with mock Git API (WireMock.Net) and mock DB
- Parallel fetch timing: 6 sources complete within 30s timeout
- Budget enforcement: 100KB raw context trimmed to 50KB correctly
- Standalone invocation via ELSA REST API
- Child workflow invocation from test parent

### Performance Tests
- Context gathering overhead (excluding API calls): <500ms
- Budget trimming for 100KB context: <50ms
- File relevance scoring for 100 candidates: <100ms

## Configuration

```json
{
  "ContextGathering": {
    "MaxContextSize": 50000,
    "ForkTimeoutSeconds": 30,
    "MaxFiles": 10,
    "MaxFileSizeBytes": 10240,
    "CommitHistoryDays": 7,
    "PriorityOrder": {
      "Assessment": ["story", "history", "files", "patterns", "commits", "tests"],
      "Planning": ["story", "files", "patterns", "tests", "commits", "history"],
      "Review": ["files", "tests", "story", "commits", "patterns", "history"],
      "Diagnosis": ["files", "tests", "commits", "history", "story", "patterns"],
      "Implementation": ["files", "patterns", "story", "tests", "commits", "history"]
    }
  }
}
```

## Success Metrics

- All 6 sources fetchable in parallel within 30s timeout
- Context assembly overhead <500ms
- Budget trimming never exceeds `maxContextSize` by >1%
- Graceful degradation: workflow succeeds with only story metadata
- All fetch activities individually visible in ELSA Studio execution log
