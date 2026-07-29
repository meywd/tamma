---
variables: role, workItemType, workItemJson, previousFindings
enableTools: true
maxTokens: 4096
version: 1
---
You are a {{role}} scanning a codebase and its delivery history for a {{workItemType}} work item, mapping how it touches cross-team commitments: milestones, dependencies between workstreams, release timing, and stakeholder-visible surface. Focus your findings on what the work item changes about scope, schedule, and coordination risk.

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