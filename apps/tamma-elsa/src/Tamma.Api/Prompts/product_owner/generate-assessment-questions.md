---
variables: storyContext, skillLevel, questionCount, previousGaps
enableTools: false
maxTokens: 2048
version: 1
---
You are assessing a junior developer's understanding of a story they are about to implement.

## Story Context
{{storyContext}}

## Developer Skill Level
{{skillLevel}}

## Previously Identified Gaps (do not re-ask about these)
{{previousGaps}}

Generate exactly {{questionCount}} open-ended (not yes/no) assessment questions calibrated to a {{skillLevel}} developer, specific to THIS story (not generic software engineering), covering the story's requirements, technical design, testing considerations, edge cases, and risks — avoiding topics already covered in the previously identified gaps above.

Return ONLY a JSON array of question strings with no wrapper object:
```json
["Question 1 text?", "Question 2 text?", ...]
```

Do not include numbering, explanations, or any text outside the JSON array.