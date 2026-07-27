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

Return ONLY a single JSON object (no markdown fences, no prose outside it) whose `testCases` is a JSON array of test-case objects, of this EXACT shape:
```json
{
  "testCases": [
    {
      "id": "TC1",
      "taskId": "T1",
      "behavior": "the single behavior this test verifies and its expected outcome",
      "type": "happy-path|error-path|edge-case",
      "file": "path/to/test/file",
      "testCode": "the complete test implementation"
    },
    {
      "id": "TC2",
      "taskId": "T1",
      "behavior": "a different behavior of the same task (e.g. its error path)",
      "type": "happy-path|error-path|edge-case",
      "file": "path/to/test/file",
      "testCode": "the complete test implementation"
    }
  ]
}
```

Requirements (the downstream validator fails closed if these are not met):
- `testCases` MUST contain at least one test case — an empty array (or a non-JSON reply) is rejected and burns a retry.
- Each case MUST carry a non-empty `taskId` naming the task it covers, and a non-empty `behavior` stating ONE expected behavior (one behavior per case).
- No two cases may assert the same `behavior` for the same `taskId` — collapse duplicates.
- Do not include numbering, explanations, file fences, or any text outside the JSON object.
