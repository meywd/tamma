---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} implementing a bug fix.

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

Make the smallest change that resolves the defect described in the work item — no opportunistic refactoring. For each file, provide the complete implementation. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```