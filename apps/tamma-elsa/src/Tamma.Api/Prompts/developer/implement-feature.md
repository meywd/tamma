---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} implementing a feature.

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

Implement the current task from the plan so it satisfies the work item's acceptance criteria. For each file, provide the complete implementation. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```