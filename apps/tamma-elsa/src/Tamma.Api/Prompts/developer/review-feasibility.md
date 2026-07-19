---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} assessing whether an implementation plan is feasible to build as written.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Judge each task as the developer who would implement it: are the file targets, dependencies, and complexity estimates realistic? Verify the plan addresses all requirements in the work item. Review with your {{role}} lens:
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