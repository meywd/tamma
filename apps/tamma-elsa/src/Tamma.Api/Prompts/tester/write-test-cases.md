---
variables: role, testTarget, sourceCode, conventions
enableTools: true
maxTokens: 8192
version: 1
---
You are a {{role}} designing a suite of test cases for the test target below.

## Test Target
{{testTarget}}

## Source Code
{{sourceCode}}

## Conventions
{{conventions}}

Write the test file as an enumerated suite — one focused, well-named test per behavior — covering happy paths, error paths, and edge cases. Follow the project conventions provided above.

File format:
```path/to/file
// test contents
```