# Story 26-3: Release Management Workflow

**Epic**: Epic 26 - Project Management & Triage
**Priority**: Medium
**Status**: Drafted

## Summary

An ELSA workflow that manages releases — changelog generation, version bumping, release notes, deployment coordination, and post-release monitoring.

## Trigger

- Milestone completed (all issues closed)
- Manual dispatch with version number
- Scheduled (e.g., weekly release train)

## Flow

```
Trigger → Validate Release Readiness
  → All milestone issues closed?
  → CI passing on main?
  → No critical security alerts?
  → All PRs merged?
  ├─ Not Ready → Report Blockers → Wait
  └─ Ready → Generate Changelog
       → LLM Changelog
            → Group by: breaking changes, features, bug fixes, chores
            → Credit contributors
            → Highlight security fixes
       → Bump Version (semver based on changes)
            → Breaking → major
            → Feature → minor
            → Fix → patch
       → Create Release PR
            → Update CHANGELOG.md
            → Update version in package.json / csproj
       → Wait for CI
       → Merge Release PR
       → Create GitHub Release
            → Tag, release notes, binaries
       → Deploy (trigger deploy workflow)
       → Post-Release Monitoring
            → Watch for regressions (error rate, crash reports)
            → 24h monitoring window
       → Post Release Summary
```

## Changelog Format

```markdown
# v1.2.0 (2026-04-01)

## Breaking Changes
- None

## Features
- Priority-based work item selection (#234)
- Issue triage workflow with LLM classification (#245)

## Bug Fixes
- Fix RedactSecrets not being called on tool outputs (#325)
- Fix DebuggingWorkflow context gathering dead code (#325)
- Fix ciRetryCount persistence across re-entries (#325)

## Security
- Content sanitizer enhanced with 10 secret patterns
- Tool output redaction for all LLM context paths

## Chores
- Sprint status audit across all 26 epics
- Wiki site rewritten as Vite + React SPA
```

## Acceptance Criteria

- [ ] Release readiness validation (CI, issues, PRs, security)
- [ ] LLM-generated changelog grouped by type
- [ ] Semver version bump based on change types
- [ ] Release PR creation with changelog + version updates
- [ ] GitHub Release creation with tag and notes
- [ ] Binary artifact attachment (from release workflow)
- [ ] Post-release monitoring window
- [ ] Events: `RELEASE.STARTED/VALIDATED/PUBLISHED/MONITORING`

## Dependencies

- Story 8-2: npm publish pipeline
- Story 8-4: install scripts / releases
- Story 1-5: GitHub Platform Implementation
