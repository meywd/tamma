# Story 26-1: Issue Triage Workflow

**Epic**: Epic 26 - Project Management & Triage
**Priority**: High
**Status**: Drafted

## Summary

An ELSA workflow triggered by GitHub webhooks (issue created/updated) that uses an LLM to read, classify, label, and prioritize incoming issues.

## Trigger

- `issues.opened` webhook — new issue created
- `issues.edited` webhook — re-triage on significant changes
- **ADL Orchestrator** — when no `tamma-auto` issues found, triggers triage on untriaged issues
- Manual dispatch

## Flow

```
Issue Webhook → Fetch Issue Details → Already Triaged?
  ├─ Yes (no changes) → Skip
  └─ No → LLM Triage
       → Read issue title, body, comments
       → Classify: bug / feature / chore / question / security / docs
       → Assess complexity: trivial / simple / medium / complex / epic
       → Assess priority: critical / high / medium / low
       → Assess autonomy: can-tamma-handle / needs-human / unclear
       → Apply Labels
       → Assign to Milestone (if configured)
       → Assign to Project Board (if configured)
       → Post Triage Comment (summary of classification)
       → Save Triage Result
```

## LLM Triage Prompt

The LLM receives:
- Issue title and body
- Repository context (CLAUDE.md, recent PRs, tech stack)
- Existing labels on the repo
- Milestone list
- Recent similar issues (for consistency)

It returns structured JSON:
```json
{
  "type": "bug",
  "priority": "high",
  "complexity": "medium",
  "canAutomate": true,
  "reasoning": "Database connection leak causes 500 errors under load",
  "suggestedLabels": ["bug", "priority-high", "complexity-medium", "tamma-auto"],
  "suggestedMilestone": "v1.2",
  "relatedIssues": [45, 67]
}
```

## Labels System

### Type Labels
- `bug` — something is broken
- `feature` — new functionality
- `chore` — maintenance, refactoring, deps
- `question` — needs clarification
- `security` — vulnerability or security concern
- `docs` — documentation

### Priority Labels
- `priority-critical` — production down, security vulnerability
- `priority-high` — significant impact, needs attention soon
- `priority-medium` — normal priority (default)
- `priority-low` — nice to have, no urgency

### Complexity Labels
- `complexity-trivial` — < 1 hour, single file change
- `complexity-simple` — 1-4 hours, few files
- `complexity-medium` — 4-16 hours, multiple components
- `complexity-complex` — 16+ hours, architectural changes
- `complexity-epic` — needs decomposition into sub-issues

### Automation Labels
- `tamma-auto` — Tamma can handle this autonomously
- `tamma-assist` — Tamma can help but needs human review
- `needs-human` — requires human decision/judgment

## Triage Comment

Posted on the issue after triage:
```markdown
**Triage Summary**

| Field | Value |
|-------|-------|
| Type | Bug |
| Priority | High |
| Complexity | Medium |
| Autonomous | Yes — Tamma can handle this |

**Reasoning**: Database connection leak causes 500 errors under load.
Similar to #45 (fixed in v1.1).

Labels applied: `bug`, `priority-high`, `complexity-medium`, `tamma-auto`
```

## Re-triage

Issues are re-triaged when:
- Issue body is significantly edited (> 20% change)
- New comments add context that changes priority
- Manual re-triage requested via `/triage` command in comments

## Acceptance Criteria

- [ ] ELSA workflow triggered by issue webhooks
- [ ] LLM reads issue content and classifies type/priority/complexity
- [ ] Labels applied automatically via GitHub API
- [ ] Milestone assignment (if configured)
- [ ] Triage comment posted on issue
- [ ] Triage results cached (don't re-triage unchanged issues)
- [ ] Re-triage on significant edits
- [ ] `tamma-auto` label enables ADL to pick up the issue
- [ ] Events: `TRIAGE.ISSUE.STARTED/COMPLETED`

## Dependencies

- Story 1-5: GitHub Platform Implementation
- Story 10-9: TammaActivity base class
- Story 12-5: Prompt engineering framework
