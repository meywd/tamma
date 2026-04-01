---
title: "Workflow: Blocker Diagnosis"
---

**Definition ID:** `blocker-diagnosis`
**Class:** `BlockerDiagnosisWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs`

## Purpose

The Blocker Diagnosis workflow detects what is blocking a junior developer and applies **progressive resolution** across 4 escalation levels. It collects signals from multiple sources, uses AI to diagnose the blocker type and severity, then works through increasingly direct intervention.

## Flow Diagram

```
+--------------------+
| Capture Inputs     |
| (session, story,   |
|  junior, skill,    |
|  repo, branch)     |
+---------+----------+
          |
          v
+--------------------+
| Collect Signals    |
| (parallel)         |
|                    |
| +----------------+ |
| | Git Activity   | |
| +----------------+ |
| | CI Status      | |
| +----------------+ |
| | Inactivity     | |
| +----------------+ |
| | Communication  | |
| +----------------+ |
+---------+----------+
          |
          v
+--------------------+
| Aggregate Signals  |
+---------+----------+
          |
          v
+--------------------+
| AI Diagnosis       |
| (llm-call)         |
+---------+----------+
          |
          v
+--------------------+
| Classify Blocker   |
| (ClassifyBlocker   |
|  Activity)         |
+---------+----------+
          |
          v
+--------------------+
| Determine Start    |
| Level              |
| (skill 1-2: skip   |
|  to Guidance)      |
+---------+----------+
          |
          v
+--------------------+
| Level 1: Hint      |
| (Socratic method)  |
| [if applicable]    |
+---------+----------+
          |
          v
+--------------------+
| Level 2: Guidance  |
| (direct steps)     |
| [if not resolved]  |
+---------+----------+
          |
          v
+--------------------+
| Level 3: Assistance|
| (code examples)    |
| [if not resolved]  |
+---------+----------+
          |
          v
+--------------------+
| Level 4: Escalation|
| (senior developer) |
| [if not resolved]  |
+---------+----------+
          |
          v
+--------------------+
| Output: Blocker    |
| Resolution         |
+--------------------+
```

## Signal Collection

Four signals are collected in parallel:

| Signal | Activity | What It Measures |
|--------|----------|-----------------|
| **Git Activity** | `CollectGitActivityActivity` | Recent commits, files changed, time since last commit |
| **CI Status** | `CollectCIStatusActivity` | Build status, test pass/fail counts, build errors, failing tests |
| **Inactivity** | `CollectInactivityActivity` | Time since last activity, inactivity flag |
| **Communication** | `CollectCommunicationActivity` | Recent messages, questions asked |

The `AggregatedSignals` object tracks how many collectors succeeded (out of 4).

## AI Diagnosis

The aggregated signals are formatted into a prompt and sent to the [LLM Call](Workflow-LLM-Call) workflow. The LLM classifies the blocker into one of 8 categories:

| Category | Description |
|----------|-------------|
| `ConceptualMisunderstanding` | Developer doesn't understand the concept |
| `TechnicalKnowledgeGap` | Missing technical knowledge |
| `EnvironmentIssue` | Development environment problems |
| `DesignDecisionParalysis` | Stuck choosing between approaches |
| `DebuggingStuck` | Can't find or fix a bug |
| `IntegrationIssue` | Problems integrating components |
| `ExternalDependency` | Blocked by external service/library |
| `PersonalBlocker` | Non-technical blocker |

The LLM returns JSON with: `blocker_type`, `confidence` (0-1), `root_cause`, `evidence[]`, `recommended_approach`.

## 4-Level Progressive Resolution

### Level 1: Hint (Socratic Method)

**Skipped for skill level 1-2** (too frustrating for beginners).

- Generates Socratic guiding questions via LLM (role: `analyst`)
- Waits for progress via `DetectProgressActivity` (bookmark)
- Wait time: **15 minutes** (30 minutes for skill level 4-5)
- If progress detected: resolved
- If no progress: escalates to Level 2

### Level 2: Direct Guidance

- Generates step-by-step instructions via LLM (role: `analyst`)
- Waits for progress via `DetectProgressActivity` (bookmark)
- Wait time: **30 minutes**
- If progress detected: resolved
- If no progress: escalates to Level 3

### Level 3: Code Assistance

- Generates working code examples with explanations via LLM (role: `implementer`)
- Waits for progress via `DetectProgressActivity` (bookmark)
- Wait time: **45 minutes**
- If progress detected: resolved
- If no progress: escalates to Level 4

### Level 4: Senior Escalation

- Compiles a context dump with all signals, diagnosis, and previous attempts
- `EscalateToSeniorActivity` sends notification and suspends (bookmark)
- Waits for senior developer intervention

## Skill Level Adaptation

| Skill Level | Start Level | Hint Wait | Notes |
|-------------|-------------|-----------|-------|
| 1-2 | Guidance | (skipped) | Beginners skip Socratic hints |
| 3 | Hint | 15 min | Standard progression |
| 4-5 | Hint | 30 min | Extended wait (advanced students benefit from thinking time) |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier |
| `storyId` | string | Story being worked on |
| `juniorId` | string | Junior developer ID |
| `skillLevel` | int | Developer skill level (1-5) |
| `blockerContext` | string? | Additional context about the blocker |
| `repository` | string | Repository URL |
| `branchName` | string | Feature branch name |

## Output

The workflow produces a single `BlockerResolution` output containing:

| Field | Type | Description |
|-------|------|-------------|
| `Status` | enum | `Resolved` or `Escalated` |
| `BlockerType` | enum | One of the 8 blocker categories |
| `BlockerSeverity` | enum | Low, Medium, High |
| `Attempts` | int | Number of resolution attempts |
| `ResolutionLevel` | enum | Hint, Guidance, Assistance, or Escalation |
| `ResolutionTime` | TimeSpan | Total time from start to resolution |
| `DiagnosisDetails` | string | Root cause hypothesis |
| `FeedbackProvided` | List\<string\> | All feedback messages delivered |

## Security

All dynamic content included in LLM prompts (blocker context, signals, diagnosis details) is sanitized via `SecurityHelpers.SanitizeForPrompt()`.

---

_See also: [Mentorship](Workflow-Mentorship) | [Debugging](Workflow-Debugging) | [Workflows Index](Workflows)_
