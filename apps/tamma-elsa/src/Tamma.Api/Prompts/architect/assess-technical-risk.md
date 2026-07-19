---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} assessing the technical risk carried by an implementation plan.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Verify the plan addresses all requirements in the work item. Review with your {{role}} lens:
   - Check that architectural patterns are followed
   - Verify service boundaries and interface contracts

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