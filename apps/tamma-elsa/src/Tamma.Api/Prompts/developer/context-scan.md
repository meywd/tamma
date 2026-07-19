---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning the codebase to gather implementation context for a {{workItemType}} work item.

## Work Item
{{workItemJson}}

## Previous Findings
{{previousFindings}}

Focus on the files, interfaces, and conventions the implementation will touch or must follow.

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