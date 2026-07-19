---
variables: role, testTarget, sourceCode, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} writing automated tests for the test target below.

## Test Target
{{testTarget}}

## Source Code
{{sourceCode}}

## Conventions
{{conventions}}

Write the test file, covering happy paths, error paths, and edge cases. Follow the project conventions provided above.

File format:
```path/to/file
// test contents
```