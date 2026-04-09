# Workflow: Context Gathering

**Definition ID:** `context-gathering`
**Class:** `ContextGatheringWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`

## Purpose

The Context Gathering workflow performs **sequential role-based codebase scanning** via the LLM Call sub-workflow. Each role scans the codebase from its perspective, accumulating findings that subsequent roles can see. Results are stored in the vector DB and a Product Owner summarizes everything into a **Minimum Viable Context**.

## Design Principles

- **No inline prompts** -- Every LLM call uses `role + action + variables` resolved from the Prompt Registry (Story 12-5)
- **Sequential accumulation** -- Each role sees all previous findings, building a progressively richer picture
- **LlmCallWorkflow dispatch** -- Each scan dispatches the `llm-call` sub-workflow (not direct HTTP calls)
- **Per-role vector DB storage** -- Each role's findings are stored immediately after extraction, so partial results persist even if later scans fail
- **PO summarization** -- The Product Owner produces a concise summary with links

## Flow Diagram

```
+---------------------+
| Initialize          |
+---------+-----------+
          |
          v
+---------------------+     +-------------------+
| Dev Scan            | --> | Store Dev (VecDB) |
+---------+-----------+     +--------+----------+
                                     |
          +-------------------------+
          v
+---------------------+     +-------------------+
| QA Scan             | --> | Store QA (VecDB)  |
| sees: dev findings  |     +--------+----------+
+---------+-----------+              |
          +-------------------------+
          v
+---------------------+     +-------------------+
| Security Scan       | --> | Store Sec (VecDB) |
| sees: dev, qa       |     +--------+----------+
+---------+-----------+              |
          +-------------------------+
          v
+---------------------+     +----------------------+
| DevOps Scan         | --> | Store DevOps (VecDB) |
| sees: dev, qa, sec  |     +--------+-------------+
+---------+-----------+              |
          +-------------------------+
          v
+---------------------+     +-------------------+
| Architect Scan      | --> | Store Arch (VecDB)|
| sees: all previous  |     +--------+----------+
+---------+-----------+              |
          +-------------------------+
          v
+---------------------+
| PO Review           |
| (summarize all)     |
+---------+-----------+
          |
          v
+---------------------+
| Set Outputs         |
+---------+-----------+
          |
          v
+---------------------+
| Complete            |
+---------------------+
```

## Sequential Role-Based Scanning

Each role dispatches `LlmCallWorkflow` with these inputs:

| Step | Role | Action | Previous Findings | Tools |
|------|------|--------|-------------------|-------|
| 1 | `developer` | `context-scan` | None | Enabled |
| 2 | `tester` | `context-scan` | Dev findings | Enabled |
| 3 | `security` | `context-scan` | Dev + QA findings | Enabled |
| 4 | `devops` | `context-scan` | Dev + QA + Security findings | Enabled |
| 5 | `architect` | `context-scan` | All previous findings | Enabled |

Each scan passes:
- `workItemJson` -- The full work item description
- `workItemType` -- Detected type (feature/bug/security/test/docs)
- `previousFindings` -- JSON object with all prior role findings
- `repository` -- Repository identifier

### Work Item Type Detection

The workflow auto-detects the work item type from the JSON content:
- `"type":"bug"` -- bug
- `"type":"security"` -- security
- `"type":"test"` -- test
- `"type":"docs"` -- docs
- Default -- feature

## Vector DB Storage

Each role's findings are stored **immediately** via `StoreRoleFindingActivity` after extraction. This ensures:
- **Partial results persist** -- if scan 3 crashes, scans 1 and 2 are already in the vector DB
- **Progressive context** -- later scans could query stored findings via RAG
- **Fault tolerance** -- no single point of failure for all 5 scans

The API endpoint (`POST /api/engine/store-context`) supports:
- Storing findings keyed by role
- Retrieving by issue number
- RAG query with role filtering and token budget

## PO Review (Minimum Viable Context)

The Product Owner LLM call receives all findings and produces:
- **Summary** -- Concise description of what the context reveals
- **Links** -- References to relevant files, docs, or external resources
- **Context IDs** -- References to stored vector DB entries

## Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `repository` | string | required | Repository identifier |
| `issueNumber` | int | required | Issue number |
| `workItemJson` | string | required | Full work item JSON |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `summary` | string | PO-generated Minimum Viable Context summary |
| `contextIds` | string | JSON array of vector DB context IDs |
| `links` | string | JSON array of relevant links extracted by PO |

## Prompt Resolution

All prompts are resolved from the **Prompt Registry** (Story 12-5) using the `(role, action)` key pair. The registry provides:
- Template with `{{variable}}` placeholders
- System prompt (role identity)
- Tool enablement flag
- Max token budget

This means prompt text is never hardcoded in workflow code. Templates can be updated via the Prompt Registry API (`PUT /api/prompts/:role/:action`) without redeploying workflows.

## Usage

Context Gathering is invoked by:
- **Single Issue Cycle** -- During step 2, before plan generation
- **Mentorship** -- During initialization phase
- **Assessment** -- Before generating questions

---

_See also: [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Issue Triage](Workflow-Triage) | [LLM Call](Workflow-LLM-Call) | [Prompt Registry (Story 12-5)](Stories#epic-12-agentic-tool-loop-completed) | [Workflows Index](Workflows)_
