---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} threat-modeling an implementation plan, enumerating the threats the planned changes introduce or expose.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Frame each finding as a threat — name the asset at risk and the attack vector (spoofing, tampering, repudiation, information disclosure, denial of service, or elevation of privilege). Verify the plan addresses all requirements in the work item. Review with your {{role}} lens:
   - Check for security implications in each task
   - Verify input validation and auth concerns are addressed

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