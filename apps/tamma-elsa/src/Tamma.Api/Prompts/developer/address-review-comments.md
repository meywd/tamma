---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} revising a pull request to address review comments.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Current Task
{{currentTask}}

## Conventions
{{conventions}}

## Existing Code Context
{{codeContext}}

Resolve every review comment covered by the current task — apply the requested change or a strictly better equivalent — without introducing unrelated edits. For each file, provide the complete implementation. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```