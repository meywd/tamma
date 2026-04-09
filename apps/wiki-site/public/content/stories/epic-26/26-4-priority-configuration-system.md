---
title: "Story 26-4: Priority Configuration System"
sidebar:
  order: 260
---

**Epic**: Epic 26 - Project Management & Triage
**Priority**: High
**Status**: Drafted

## Summary

A configuration system that maps labels, sources, and issue properties to priorities. Used by both the Triage Workflow (to assign labels) and the ADL Orchestrator (to pick the next work item).

## Configuration Schema

```json
{
  "priority": {
    "sources": {
      "security-alert-critical": "urgent",
      "security-alert-high": "urgent",
      "security-alert-medium": "high",
      "security-alert-low": "normal",
      "ci-failure-main": "urgent",
      "ci-failure-branch": "normal",
      "issue": "normal",
      "stale-pr": "low"
    },

    "labels": {
      "priority-critical": "urgent",
      "priority-high": "high",
      "priority-medium": "normal",
      "priority-low": "low",
      "bug": "high",
      "security": "urgent",
      "hotfix": "urgent",
      "blocked": "skip"
    },

    "complexity": {
      "complexity-trivial": 1,
      "complexity-simple": 2,
      "complexity-medium": 3,
      "complexity-complex": 5,
      "complexity-epic": "decompose"
    },

    "ordering": {
      "primary": "priority",
      "secondary": "complexity",
      "tertiary": "created_at",
      "direction": "asc"
    },

    "filters": {
      "includeLabels": ["tamma-auto"],
      "excludeLabels": ["blocked", "wontfix", "needs-human"],
      "maxComplexity": "complex",
      "onlyAssignable": true
    }
  }
}
```

## Priority Levels

| Level | Numeric | Meaning | SLA |
|-------|---------|---------|-----|
| urgent | 0 | Production impact, security vuln | < 1 hour |
| high | 1 | Significant impact, bugs | < 4 hours |
| normal | 2 | Standard work | < 24 hours |
| low | 3 | Nice to have | Best effort |
| skip | -1 | Do not process | N/A |

## Resolution Order

When multiple items have the same priority:
1. **Complexity** — simpler items first (quick wins)
2. **Age** — oldest first (prevent starvation)
3. **Dependencies** — items with no blockers first

## Integration Points

### Triage Workflow (26-1)
- Reads config to know which labels to apply
- LLM uses the label definitions as context for classification

### ADL Orchestrator (workflow #1)
- Reads config to filter and sort candidates
- `SelectWorkItemActivity` uses this config

### Scrum Workflow (26-2)
- Uses complexity values for velocity calculation
- Priority determines sprint inclusion order

## Storage

Config stored in:
1. `.tamma/priority.json` in the repository (repo-specific)
2. `~/.tamma/priority.json` global defaults
3. API endpoint for runtime updates

Layered: repo config overrides global defaults.

## Acceptance Criteria

- [ ] Priority config schema with sources, labels, complexity mappings
- [ ] Config loading from repo file (`.tamma/priority.json`)
- [ ] Global defaults fallback
- [ ] `PriorityResolver` service used by Triage and ADL
- [ ] Label-to-priority mapping
- [ ] Source-to-priority mapping
- [ ] Complexity scoring for velocity tracking
- [ ] Filter configuration (include/exclude labels)
- [ ] Sort order configuration
- [ ] Events: `PRIORITY.CONFIG.LOADED`

## Dependencies

- Story 1-3: Provider Configuration Management (config pattern)
- Story 1-7: Git Platform Configuration Management
