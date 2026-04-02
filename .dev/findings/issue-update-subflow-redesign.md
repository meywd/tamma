# Issue Update Sub-Workflow Redesign

**When**: During UpdateIssueStatusWorkflow optimization
**Related**: SingleIssueCycleWorkflow, all steps

## Current
Simple fire-and-forget that posts a hardcoded message string to the issue.

## Redesigned

The update-issue-status sub-workflow:

1. **Receives** raw data (plan JSON, review log, task list, error details, etc.)
2. **Saves** full raw data to storage (vector DB or KV, keyed by issue/cycle ID)
3. **Calls tech writer LLM** to summarize the raw data into a clean issue comment
4. **Posts** the summarized comment to the issue
5. **Manages labels** (add/remove as needed)

### Flow
```
Receive Data → Save to Storage → LLM Summarize → Post Comment → Manage Labels
```

### Tech Writer LLM
- Role: `tech_writer`
- Input: raw data + step name + issue context
- Output: markdown summary suitable for a GitHub issue comment
- Prompt: "Summarize this {step} result for a GitHub issue comment. Be concise, use markdown. Include key decisions, numbers, and links. Max 500 words."

### Example Output (Plan Review)
```markdown
## Plan Review Complete ✅

**Decision**: Approved with minor adjustments

**Panel Summary**:
- 🏗️ **Architect**: Approved — design follows existing patterns
- 👨‍💻 **Developer**: Approved — complexity estimate realistic
- 🧪 **QA**: Concern addressed — added integration test for edge case
- 🔒 **Security**: Approved — no new attack surface
- 🚀 **DevOps**: Approved — no infrastructure changes
- 📋 **PO**: Approved — scope matches requirements
- 🤖 **Orchestrator**: Approved — task dependencies correct

**Tasks**: 5 implementation tasks created ([view details](https://app.tamma.dev/cycles/123/tasks))

**Changes from review**:
- Added error handling for null response (from QA concern)
- Updated test strategy to include integration tests

[Full discussion log](https://app.tamma.dev/cycles/123/review)
```

### Storage
Data saved with key: `cycle:{issueNumber}:{stepName}`
- `cycle:123:context` — gathered context
- `cycle:123:plan` — implementation plan
- `cycle:123:review` — review discussion log
- `cycle:123:tasks` — task breakdown
- `cycle:123:tdd` — TDD results
- `cycle:123:pr` — PR details
- `cycle:123:ci` — CI results

### Inputs to Sub-Workflow
```
repository: string
issueNumber: int
stepName: string          # "context", "plan", "review", etc.
rawDataJson: string       # full raw data to save + summarize
addLabels: string[]?
removeLabels: string[]?
```

### Retry
- LLM call: 2 retries
- GitHub API: 3 retries with backoff
- If LLM summarization fails: fall back to a simple template-based message
- If GitHub API fails after retries: log and continue (don't block)
