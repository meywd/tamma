---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase for a {{workItemType}} work item, locating the documentation surface the change affects. Prioritize files that carry documentation (READMEs, docs/, changelogs, API comments) and public interfaces whose documented behavior readers depend on.

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