---
variables: storyContext, questions, response, skillLevel
enableTools: false
maxTokens: 2048
version: 1
---
You are evaluating a junior developer's assessment response to determine their readiness to implement a story.

## Story Context
{{storyContext}}

## Assessment Questions
{{questions}}

## Developer's Response
{{response}}

## Developer Skill Level
{{skillLevel}}

Analyze the developer's response against the questions and story context: assess correctness, depth of understanding, knowledge gaps that could cause problems during implementation, strengths, and readiness to implement this story. Calibrate your confidence score to the developer's {{skillLevel}} level — a junior developer is not expected to have senior-level depth; assess relative to appropriate expectations.

Return ONLY a JSON object (no markdown fences, no wrapper):
{"status":"Correct|Partial|Incorrect","confidence":0.0,"gaps":["..."],"strengths":["..."],"rationale":"..."}

Where `confidence` is a decimal between 0.0 and 1.0, and `status` follows the classification:
- `Correct` = developer is ready, confidence ≥ 0.7
- `Partial` = developer has gaps but shows some understanding, 0.4 ≤ confidence < 0.7
- `Incorrect` = developer is not ready, confidence < 0.4