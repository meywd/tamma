---
variables: role, workItemJson, findings, conventions
enableTools: false
maxTokens: 4096
version: 1
---
You are a {{role}} decomposing a complex issue into an ORDERED set of smaller, independently implementable sub-tasks so the team can deliver it incrementally with continuous integration.

## Issue / Work Item
{{workItemJson}}

## Gathered Context (codebase / prior art)
{{findings}}

## Conventions
{{conventions}}

Break the work into sub-tasks each sized ROUGHLY 2-8 hours with a clear definition of done; together the sub-tasks must fully deliver the parent issue's intent. Base the breakdown on the issue and context provided — do NOT invent scope the issue does not call for, and do NOT fabricate dependencies. Only reference sub-task ids you actually define.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "summary": "1-3 sentence overview of the breakdown and how it preserves the issue's intent",
  "subtasks": [
    {
      "id": "ST-1",
      "title": "short headline for the sub-task",
      "description": "what to implement in this sub-task",
      "acceptanceCriteria": "the definition of done for this sub-task",
      "estimateHours": 4,
      "complexity": "low|medium|high",
      "dependsOn": []
    },
    {
      "id": "ST-2",
      "title": "the next sub-task, built on ST-1",
      "description": "what to implement in this sub-task",
      "acceptanceCriteria": "the definition of done for this sub-task",
      "estimateHours": 4,
      "complexity": "low|medium|high",
      "dependsOn": ["ST-1"]
    }
  ]
}
```

Requirements (the downstream parser fails closed if these are not met):
- `summary` MUST be a non-empty overview — it is load-bearing (it records intent preservation).
- `subtasks` MUST contain at least one sub-task; each MUST carry a non-empty `id` and at least a `title` or `description`.
- `id`s MUST be unique within the decomposition.
- `estimateHours` is a number (rough hours); `complexity` is one of `low`, `medium`, `high`.
- Every entry in `dependsOn` MUST be the `id` of another sub-task in this decomposition (no self-references, no dangling ids).
- Order `subtasks` so each sub-task's prerequisites appear before it.