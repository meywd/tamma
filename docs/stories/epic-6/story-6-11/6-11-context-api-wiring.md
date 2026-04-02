# Story 6-11: Context API Wiring — Connect C# Activities to TypeScript Packages

**Epic**: Epic 6 - Knowledge Base & Context Management
**Priority**: Critical
**Status**: Drafted

## Problem

The C# Elsa activities (`RoleScanActivity`, `StoreFindingsActivity`, `POContextReviewActivity`, `FetchUntriagedItemsActivity`) call HTTP endpoints that don't exist:
- `POST /api/engine/store-context`
- `GET /api/engine/context/:issueNumber`
- `POST /api/engine/execute-task` (partial)
- `GET /api/engine/issues`
- `GET /api/engine/security-alerts`
- `POST /api/engine/issue-comment`
- `POST /api/engine/issue-labels`
- `POST /api/engine/create-issue`
- `POST /api/engine/cycle-result`
- `POST /api/engine/trigger-ci`

The TypeScript implementations exist but have no API routes:
- `packages/intelligence/src/indexer/` — CodebaseIndexer
- `packages/intelligence/src/vector-store/` — ChromaDB, PgVector
- `packages/intelligence/src/rag/` — RAG pipeline
- `packages/intelligence/src/context/` — Context aggregator
- `packages/platforms/src/github/` — GitHub API client

## Solution

### New API Routes (in packages/api/src/routes/)

#### Context & Storage
```
POST /api/engine/store-context
  Body: { repository, issueNumber, findings: { dev, qa, security, devops, architect } }
  → Chunks findings → embeds via EmbeddingService → stores in vector DB
  → Also saves raw JSON to PostgreSQL (cycle_context table)
  Returns: { contextIds: [...] }

GET /api/engine/context/:issueNumber
  → Retrieves stored context by issue number
  Returns: { findings, contextIds, storedAt }

POST /api/engine/query-context
  Body: { contextIds, query, role, maxTokens }
  → RAG pipeline retrieves relevant chunks by role
  Returns: { chunks: [...], totalTokens }
```

#### LLM Execution
```
POST /api/engine/execute-task
  Body: { prompt, role, repository, enableTools }
  → Resolves agent via RoleBasedAgentResolver
  → Executes with tool loop if enableTools=true
  Returns: { output, tokensUsed, costUsd, toolCalls }
```

#### GitHub Integration
```
GET /api/engine/issues
  Query: repo, labels, state
  → GitHubPlatform.listIssues()

GET /api/engine/security-alerts
  Query: repo, type (dependabot|codeql)
  → GitHub API: /repos/{owner}/{repo}/dependabot/alerts
  → GitHub API: /repos/{owner}/{repo}/code-scanning/alerts

POST /api/engine/issue-comment
  Body: { repository, issueNumber, body }
  → GitHubPlatform.addIssueComment()

POST /api/engine/issue-labels
  Body: { repository, issueNumber, labels }
  → GitHub API: /repos/{owner}/{repo}/issues/{number}/labels

POST /api/engine/create-issue
  Body: { repository, title, body, labels }
  → GitHubPlatform.createIssue()

POST /api/engine/cycle-result
  Body: { exitReason, issueNumber, error }
  → Store in event store, update metrics

POST /api/engine/trigger-ci
  Body: { repository, branchName, workflowFile }
  → GitHub API: workflow_dispatch
```

### Database Schema

```sql
CREATE TABLE cycle_context (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  issue_number INT NOT NULL,
  repository TEXT NOT NULL,
  role TEXT NOT NULL,
  findings JSONB NOT NULL,
  context_ids TEXT[] DEFAULT '{}',
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_cycle_context_issue ON cycle_context(repository, issue_number);
```

## Acceptance Criteria

- [ ] All listed API routes implemented and tested
- [ ] `store-context` chunks and embeds findings via CodebaseIndexer
- [ ] `store-context` also saves raw JSON to PostgreSQL
- [ ] `query-context` retrieves via RAG pipeline with role filtering
- [ ] `execute-task` wires to RoleBasedAgentResolver with tool loop
- [ ] `issues` and `security-alerts` wire to GitHubPlatform
- [ ] `issue-comment`, `issue-labels`, `create-issue` wire to GitHubPlatform
- [ ] `cycle-result` stores in event store
- [ ] `trigger-ci` dispatches GitHub Actions workflow
- [ ] All endpoints have error handling and structured logging

## Dependencies

- Story 6-1: Codebase Indexer (done)
- Story 6-2: Vector DB Integration (in-progress)
- Story 6-3: RAG Pipeline (done)
- Story 1-5: GitHub Platform (done)
- Story 9-8: Role-Based Agent Resolver (done)
- Story 1.5-4: Web Server API (done)
