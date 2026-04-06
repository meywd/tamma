---
title: "Workflow: Assessment"
---

**Definition ID:** `assessment`
**Class:** `AssessmentWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`

## Purpose

The Assessment workflow evaluates a junior developer's understanding of story requirements through AI-generated questions, bookmark-based response waiting, and skill profiling. It gathers context, generates targeted questions, delivers them to the junior, waits for a response (with timeout), analyzes the response via AI, classifies the result, and updates the developer's skill profile.

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (sessionId,      |
|  storyId, etc.)  |
+--------+---------+
         |
         v
+------------------+
| Gather Context   |
| (context-        |
|  gathering)      |
+--------+---------+
         |
         v
+------------------+
| Store Context    |
| Result           |
+--------+---------+
         |
         v
+------------------+
| Generate         |
| Questions        |
| (GenerateQs      |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Store Questions  |
+--------+---------+
         |
         v
+------------------+
| Deliver          |
| Questions        |
| (DeliverQs       |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Wait For         |
| Response         |
| (bookmark)       |
+--+------------+--+
   |            |
 Responded   Timeout
   |            |
   v            v
+----------+ +------------------+
| Store    | | Set Timeout      |
| Response | | Result           |
+----+-----+ +--------+---------+
     |                 |
     v                 v
+----------+ +------------------+
| Analyze  | | Update Skill     |
| Response | | Profile (Timeout)|
+----+-----+ +--------+---------+
     |                 |
     v                 v
+----------+ +------------------+
| Store    | | Set Output       |
| Analysis | | (Timeout)        |
+----+-----+ +--------+---------+
     |                 |
     v                 v
+----------+ +------------------+
| Classify | | Expose Output    |
| Result   | | (Timeout)        |
+----+-----+ +------------------+
     |
     v
+------------------+
| Store            |
| Classification   |
+--------+---------+
         |
         v
+------------------+
| Update Skill     |
| Profile          |
+--------+---------+
         |
         v
+------------------+
| Set Output       |
| Result           |
+--------+---------+
         |
         v
+------------------+
| Expose Output    |
| Response         |
+------------------+
```

## Bookmark Points

| Bookmark | Activity | Waits For | Outcomes |
|----------|----------|-----------|----------|
| Response wait | `WaitForResponseActivity` | Junior developer's response submission | `Responded`, `Timeout` |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier |
| `storyId` | string | Story being assessed |
| `juniorId` | string | Junior developer ID |
| `skillLevel` | int | Current skill level of the junior |
| `previousAttemptJson` | string | JSON of previous attempt (for retry scenarios) |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `storyContext` | string | Context gathered from the context-gathering workflow |
| `questionsJson` | string | Generated questions serialized as JSON |
| `juniorResponse` | string | The junior's response text |
| `analysisResultJson` | string | AI analysis result as JSON |
| `responseReceived` | bool | Whether a response was received (vs timeout) |
| `assessmentStatus` | AssessmentOutcomeStatus | Classified outcome status |
| `confidence` | decimal | Confidence score from classification |
| `nextState` | MentorshipState | Recommended next mentorship state |
| `gapsJson` | string | Identified knowledge gaps as JSON array |
| `strengthsJson` | string | Identified strengths as JSON array |
| `attemptNumber` | int | Current attempt number (derived from previousAttemptJson) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `assessmentResult` | string | Full assessment result as JSON |
| `nextState` | string | Recommended next mentorship state |
| `status` | string | Assessment outcome status |
| `skillLevel` | int | Assessed skill level (1-5, mapped from confidence) |

## Skill Level Mapping

Confidence is mapped to a 1-5 skill level:

| Confidence | Skill Level |
|------------|-------------|
| >= 0.8 | 5 |
| >= 0.6 | 4 |
| >= 0.4 | 3 |
| >= 0.2 | 2 |
| < 0.2 | 1 |

## Timeout Behavior

On timeout, the workflow sets:
- `status` = `Timeout`
- `confidence` = 0
- `nextState` = `DIAGNOSE_BLOCKER`
- `skillLevel` = 1 (lowest)
- Gaps include "No response received within timeout window"

---

_See also: [Context Gathering](/workflows/context-gathering) | [Mentorship](/workflows/mentorship) | [Workflows Index](/workflows)_
