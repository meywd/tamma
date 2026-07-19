---
variables: role, testTarget, tasksJson, contextIds, repository, branchName, conventions
enableTools: true
maxTokens: 8192
version: 2
---
You are a {{role}} writing test cases for the implementation tasks below, before the implementation exists (TDD red phase).

## Test Target
{{testTarget}}

## Tasks
{{tasksJson}}

## Repository
{{repository}} (branch: {{branchName}})

## Context IDs
{{contextIds}}

## Conventions
{{conventions}}

Derive concrete test cases from the tasks above, covering happy paths, error paths, and edge cases for each task's observable behavior. Follow the project conventions provided above.

Return ONLY a JSON array of test-case objects with no wrapper object:
```json
[
  {
    "id": "TC1",
    "taskId": "T1",
    "description": "the behavior this test verifies and its expected outcome",
    "type": "happy-path|error-path|edge-case",
    "file": "path/to/test/file",
    "testCode": "the complete test implementation"
  }
]
```

Do not include numbering, explanations, file fences, or any text outside the JSON array. The array MUST contain at least one test case — the downstream validator rejects an empty array (or a non-JSON reply) and burns a retry.
