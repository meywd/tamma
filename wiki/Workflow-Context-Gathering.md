# Workflow: Context Gathering

**Definition ID:** `context-gathering`
**Class:** `ContextGatheringWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`

## Purpose

The Context Gathering workflow collects contextual data from **6 sources in parallel** using ELSA's Parallel activity (fan-out/fan-in). It assembles gathered data and applies priority-based budget trimming to stay within token limits.

## Flow Diagram

```
+-----------------------+
| Initialize Inputs     |
| (SessionId, StoryId,  |
|  RepositoryUrl,       |
|  TargetFiles,         |
|  MaxContextSize,      |
|  Purpose)             |
+-----------+-----------+
            |
            v
+-----------------------+
| Phase 1: Parallel     |
| Fetches (independent) |
|                       |
| +-------------------+ |
| | Fetch Story       | |
| | Metadata          | |
| +-------------------+ |
| | Fetch Recent      | |
| | Commits           | |
| +-------------------+ |
| | Fetch Test        | |
| | Results           | |
| +-------------------+ |
| | Fetch Session     | |
| | History           | |
| +-------------------+ |
+-----------+-----------+
            |
            v
+-----------------------+
| Story Metadata OK?    |
+--+----------------+---+
  YES                NO
   |                  |
   v                  v
+-------------------+ +-----------------------+
| Track Phase 1     | | Fault: No Metadata    |
| Failures          | | (abort workflow)      |
+--------+----------+ +-----------------------+
         |
         v
+-----------------------+
| Phase 2: Parallel     |
| Fetches (dependent)   |
|                       |
| +-------------------+ |
| | Fetch File        | |
| | Contents          | |
| +-------------------+ |
| | Fetch Similar     | |
| | Patterns          | |
| +-------------------+ |
+-----------+-----------+
            |
            v
+-----------------------+
| Track Phase 2         |
| Failures              |
+-----------+-----------+
            |
            v
+-----------------------+
| Assemble Context      |
| (AssembleContext       |
|  Activity)            |
+-----------+-----------+
            |
            v
+-----------------------+
| Apply Budget          |
| (ApplyBudget          |
|  Activity)            |
+-----------+-----------+
            |
            v
+-----------------------+
| Set Outputs           |
| (contextJson,         |
|  success,             |
|  failedSources)       |
+-----------------------+
```

## Two-Phase Parallel Fetching

### Phase 1: Independent Fetches

These sources can be fetched concurrently with no dependencies:

| Source | Activity | Description |
|--------|----------|-------------|
| Story Metadata | `FetchStoryMetadataActivity` | Story title, description, tags, acceptance criteria |
| Recent Commits | `FetchRecentCommitsActivity` | Recent commits on the branch with files changed |
| Test Results | `FetchTestResultsActivity` | Latest test run results |
| Session History | `FetchSessionHistoryActivity` | Previous session context and decisions |

**Story Metadata is critical** -- if it fails, the workflow aborts with a Fault. Other Phase 1 failures are tracked but do not block the workflow.

### Phase 2: Dependent Fetches

These sources depend on Phase 1 results (story description, commit file lists):

| Source | Activity | Depends On |
|--------|----------|------------|
| File Contents | `FetchFileContentsActivity` | Story description (for relevance), commit files (for scope) |
| Similar Patterns | `FetchSimilarPatternsActivity` | Story title and tags (for similarity search) |

Phase 2 failures are tracked but do not block the workflow.

## Context Assembly

The `AssembleContextActivity` takes all fetched data and organizes it into a structured `AssembledContext` object with sections prioritized by the `Purpose` parameter:

| Purpose | Priority Order |
|---------|---------------|
| Assessment | Story metadata > test results > file contents > patterns |
| Implementation | File contents > story metadata > commits > patterns |
| Debugging | Test results > file contents > commits > session history |

## Budget Trimming

The `ApplyBudgetActivity` enforces the `MaxContextSize` limit (default: 50,000 characters) by:
1. Measuring the total assembled context size
2. Trimming lowest-priority sections first
3. Truncating individual sections if needed
4. Producing a `ContextGatheringOutput` with the final trimmed context and metadata

## Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `SessionId` | Guid | required | Session identifier |
| `StoryId` | string | required | Story identifier |
| `RepositoryUrl` | string | `""` | Repository URL |
| `TargetFiles` | List\<string\>? | null | Specific files to fetch |
| `MaxContextSize` | int | 50000 | Maximum context size in characters |
| `Purpose` | ContextPurpose | Assessment | Context purpose (affects priority) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `contextJson` | string | Serialized context gathering output JSON |
| `success` | bool | Whether context gathering succeeded |
| `failedSources` | string | JSON array of source names that failed |

## Failure Tracking

Failed sources are tracked in a JSON string array. Each phase adds any failed sources to the list. This allows the parent workflow to know which context was unavailable.

Example: `["RecentCommits", "SimilarPatterns"]` means those two fetches failed but the rest succeeded.

## Usage

Context Gathering is invoked by:
- **Single Issue Cycle** -- During step 2, before plan generation
- **Mentorship** -- During initialization phase
- **Assessment** -- Before generating questions

---

_See also: [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Mentorship](Workflow-Mentorship) | [Assessment](Workflow-Mentorship#assessment) | [Workflows Index](Workflows)_
