---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} reviewing an implementation plan for technical soundness.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Scrutinize task ordering, hidden coupling between tasks, and whether complexity estimates match the real shape of the codebase. Verify the plan addresses all requirements in the work item. Review with your {{role}} lens:
   - Apply your role-specific expertise to the plan

Output as JSON:
```json
{
  "issues": [
    {
      "task": "T1|General",
      "severity": "critical|major|minor|suggestion",
      "category": "...",
      "issue": "...",
      "recommendation": "..."
    }
  ],
  "verdict": {
    "decision": "APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION",
    "summary": "...",
    "blockingIssues": []
  }
}
```