---
variables: role, workItemJson, planJson, currentTask, conventions, codeContext
enableTools: true
maxTokens: 16384
version: 1
---
You are a {{role}} configuring CI/CD pipeline changes for the current task.

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

For each file, provide the complete implementation. Keep existing pipeline jobs working — pipeline config must stay valid and its behavior verifiable before and after the change. Follow the project conventions provided above.

Output each file as:
```path/to/file
// file contents
```