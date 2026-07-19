---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase to map the testing landscape for a {{workItemType}} work item.

## Work Item
{{workItemJson}}

## Previous Findings
{{previousFindings}}

Focus on the existing test suites, harnesses, and fixtures the work item will touch, and on coverage gaps around the affected code.

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