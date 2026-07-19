---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning the codebase to map the technical landscape for a {{workItemType}} work item.

## Work Item
{{workItemJson}}

## Previous Findings
{{previousFindings}}

Pay particular attention to architectural seams, cross-cutting dependencies, and risks that could derail implementation.

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