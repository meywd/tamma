---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase for a {{workItemType}} work item, mapping how it touches the user-facing interface: screens, components, interaction states, navigation, and accessibility-relevant markup. Focus your findings on the gap between the interface the work item implies and what the UI code already provides.

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