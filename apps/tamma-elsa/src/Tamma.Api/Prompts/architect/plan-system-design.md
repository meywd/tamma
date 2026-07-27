---
variables: role, workItemJson, contextFindings, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} planning the system design for the work item below.

## Work Item
{{workItemJson}}

## Context
{{contextFindings}}

## Conventions
{{conventions}}

Break the work item into discrete, ordered tasks, making component responsibilities, boundaries, and key design trade-offs visible in the task descriptions.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "tasks": [
    {
      "id": "T1",
      "description": "what this task delivers and the component boundary it owns",
      "files": ["src/Component/File.cs"],
      "dependsOn": [],
      "testing": "how this task is verified"
    },
    {
      "id": "T2",
      "description": "the next increment, built on T1",
      "files": ["tests/Component/FileTests.cs"],
      "dependsOn": ["T1"],
      "testing": "how this task is verified"
    }
  ],
  "files": ["src/Component/File.cs", "tests/Component/FileTests.cs"]
}
```

Requirements (the downstream validator fails closed if these are not met):
- `tasks` MUST contain at least one task; each task MUST carry a unique `id`.
- Each task's `files` MUST be a non-empty array of plain file-path strings (NOT `{path, action}` objects) naming the files the task touches.
- Each task MUST state a non-empty `testing` approach.
- `dependsOn` MAY only reference `id`s of other tasks in this plan — no self-references, no dangling ids, no cycles (the tasks must have a valid execution order).
- The root `files` is the union of every task's files, as plain path strings.
