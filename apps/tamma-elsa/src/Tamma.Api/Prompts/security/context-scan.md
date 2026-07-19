---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase to map the security surface for a {{workItemType}} work item.

## Work Item
{{workItemJson}}

## Previous Findings
{{previousFindings}}

Focus on the trust boundaries, authentication and authorization paths, sensitive data flows, and risky dependencies the work item will touch.

Output your findings as a JSON object:
```json
{
  "relevantFiles": [{"path": "...", "reason": "..."}],
  "interfaces": [{"name": "...", "location": "...", "impact": "create|modify|consume"}],
  "dependencies": [{"name": "...", "type": "internal|external"}],
  "conventions": ["..."],
  "risks": [{"description": "...", "severity": "low|medium|high"}]
}
```