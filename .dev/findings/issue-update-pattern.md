# Issue Update Pattern

**When**: Applied across all workflow steps
**Related**: SingleIssueCycleWorkflow, all sub-workflows

## Pattern

Every step transition forks into two parallel actions:
1. **Update Issue** — post status comment + manage labels (with built-in retries)
2. **Continue** — next step or report to orchestrator

```
Step Result → Fork
  ├─ UpdateIssueStatusActivity (retries: 3, backoff: 1s/2s/4s)
  └─ Next Step or ReportCycleResultActivity
→ Join → Continue
```

## Why Fork (not sequential)

- Issue updates are non-critical — workflow should not block on a GitHub API call
- UpdateIssueStatusActivity has built-in retries (3 attempts with backoff)
- If update fails after retries, workflow continues anyway (fire-and-forget with best effort)
- The next step or report fires immediately, no waiting

## Messages per Step

| Step | Message | Labels |
|------|---------|--------|
| Validate | "🤖 Tamma is processing #{number}: {title}" | +tamma-processing |
| Validate (invalid) | "❌ Cannot process: {error}" | +tamma-error |
| Context Gathered | "📋 Context gathered. Generating plan..." | |
| Plan Generated | "📝 Implementation plan ready. Sending for review..." | |
| Plan Approved | "✅ Plan approved by panel. Creating implementation tasks..." | |
| Plan Deferred | "⏸️ Items deferred to new issues: #X, #Y. Closing." | +deferred |
| Plan Split | "🔀 Issue decomposed into: #X, #Y, #Z. Closing." | +split |
| Plan Needs Human | "🙋 Panel needs human input. See discussion below." | +needs-human |
| Tasks Created | "🔨 {N} implementation tasks created. Reviewing..." | |
| Tasks Approved | "✅ Tasks approved. Starting implementation..." | |
| Branch Created | "🌿 Branch `{name}` created." | |
| TDD Complete | "✅ TDD cycle complete. {N} tests passing." | |
| PR Created | "📦 PR #{prNumber} created." | |
| CI Passed | "✅ CI checks passed." | |
| Code Review Done | "👀 Code review complete." | |
| Merged | "🎉 PR #{prNumber} merged! Issue resolved." | +tamma-completed, -tamma-processing |
| Error | "❌ Error: {message}" | +tamma-error, -tamma-processing |

## Retry Strategy

```csharp
MaxAttempts = 3
Delays = [1s, 2s, 4s]  // exponential
FailBehavior = Ignore   // don't block workflow
```
