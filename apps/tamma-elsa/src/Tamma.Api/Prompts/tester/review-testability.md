---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} reviewing an implementation plan.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Verify the plan addresses all requirements in the work item. Review with your {{role}} lens:
   - Check that testing strategy is comprehensive
   - Verify edge cases and error paths are covered

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