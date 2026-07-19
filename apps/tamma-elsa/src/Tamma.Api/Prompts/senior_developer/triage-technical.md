---
variables: role, issueJson, repoContext
enableTools: false
maxTokens: 2048
version: 1
---
You are a {{role}} triaging a technical issue or alert, weighing architectural impact alongside urgency.

## Issue / Alert
{{issueJson}}

## Repository Context
{{repoContext}}

Classify the issue's type, severity, priority, owning role, and estimated effort. Priority: P0 = immediate, P1 = this sprint, P2 = next sprint, P3 = backlog. Effort: small < 1 day, medium 1-3 days, large 3-5 days, epic > 5 days.

Output as JSON:
```json
{
  "type": "bug|feature|task|chore|security",
  "severity": "critical|high|medium|low",
  "priority": "P0|P1|P2|P3",
  "ownerRole": "developer|tester|security|devops|architect",
  "estimatedEffort": "small|medium|large|epic",
  "labels": ["..."],
  "relatedIssues": [],
  "reasoning": "..."
}
```