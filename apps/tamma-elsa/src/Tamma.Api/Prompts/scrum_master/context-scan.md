---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase and its delivery history for a {{workItemType}} work item, mapping how it touches team cadence: sprint scope, in-flight work, blockers, and delivery risk. Focus your findings on what the work item means for the team's commitments and where impediments are likely to surface.

## Work Item
{{workItemJson}}

## Previous Findings
{{previousFindings}}

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