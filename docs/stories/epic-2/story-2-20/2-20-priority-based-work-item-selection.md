# Story 2-20: Priority-Based Work Item Selection

**Epic**: Epic 2 - Autonomous Development Loop
**Priority**: High
**Status**: Drafted

## Summary

Replace the single-source issue selection with a priority-based work item selector that pulls from multiple sources (security alerts, CI failures, issues, stale PRs) and picks the highest priority item.

## Problem

The current `SelectIssueActivity` only looks at GitHub issues with matching labels. It ignores:
- Security vulnerabilities (Dependabot, CodeQL alerts)
- Failed CI on the main branch
- Stale PRs needing attention
- Priority labels on issues

The ADL Orchestrator's job is maintaining a healthy repo — not just processing labeled issues.

## Solution

### Work Item Sources (in priority order)

| Priority | Source | Trigger |
|----------|--------|---------|
| URGENT | Security alerts (critical/high) | Dependabot, CodeQL |
| URGENT | Failed CI on main | GitHub Actions status |
| HIGH | Security alerts (medium/low) | Dependabot, CodeQL |
| NORMAL | Issues by label | Configurable labels (e.g., `tamma-auto`) |
| LOW | Stale PRs | Review requested, no response > 24h |

### SelectWorkItemActivity

Replaces `SelectIssueActivity`. Queries each source in priority order:

```
interface WorkItem {
  type: 'security-alert' | 'ci-failure' | 'issue' | 'stale-pr';
  priority: 'urgent' | 'high' | 'normal' | 'low';
  source: string;           // e.g., "dependabot", "codeql", "github-issues"
  identifier: string;       // e.g., issue number, alert ID
  title: string;
  metadata: Record<string, unknown>;
}
```

Returns the highest priority work item, or null if nothing needs attention.

### Priority Configuration

```json
{
  "workItemSources": {
    "securityAlerts": { "enabled": true, "minSeverity": "medium" },
    "ciFailures": { "enabled": true, "branch": "main" },
    "issues": { "enabled": true, "labels": ["tamma-auto"], "excludeLabels": ["blocked"] },
    "stalePRs": { "enabled": true, "staleAfterHours": 24 }
  },
  "priorityOverrides": {
    "labels": {
      "priority-critical": "urgent",
      "priority-high": "high",
      "bug": "high"
    }
  }
}
```

### Impact on SingleIssueCycle

SingleIssueCycle receives a `WorkItem` instead of selecting its own issue. It needs to handle different work item types:
- Security alert → generate fix PR
- CI failure → diagnose and fix
- Issue → current flow (plan, implement, test, PR)
- Stale PR → review and respond

## Acceptance Criteria

- [ ] `SelectWorkItemActivity` replaces `SelectIssueActivity` in ADL Orchestrator
- [ ] Queries security alerts via GitHub API
- [ ] Queries CI status via GitHub Actions API
- [ ] Queries issues with configurable label filters
- [ ] Queries stale PRs
- [ ] Priority ordering: urgent > high > normal > low
- [ ] Priority labels on issues override default priority
- [ ] Configurable source enable/disable
- [ ] Events emitted: `ADL.WORKITEM.SELECTED.STARTED/COMPLETED`
- [ ] SingleIssueCycle accepts WorkItem input

## Dependencies

- Story 1-5: GitHub Platform Implementation (API access)
- Story 10-9: TammaActivity base class (events)
