---
title: "Story 24-4: Intent Classification + Engine Integration"
sidebar:
  order: 240
---

Status: planned

## Story

As a user, I want Tamma to understand my spoken commands and execute orchestrator actions so I can control the full development pipeline by voice.

## Acceptance Criteria

1. `IntentClassifier` classifies user speech into: engine commands, questions about status, conversational feedback
2. Engine commands mapped to `EngineCommand` types: start, approve, reject (with feedback), skip, cancel
3. Questions answered by reading engine state (no command dispatched): "what's the status?", "show me the plan"
4. Conversational feedback fed into plan rejection with context: "that won't work because..."
5. Proactive spoken notifications: engine state transitions (plan generated, tests running, tests passed, PR created, merged) trigger TTS without user prompt
6. Debounce filter: only info+ level events trigger speech, not every debug log
7. Approval flow via voice: engine emits `approval.request`, user says "approve"/"reject", routed back to engine's `approvalHandler`
8. Multi-turn conversation context maintained — the LLM knows what was said in previous turns
9. Hybrid mode: user can switch between typing and talking mid-conversation, both interleave in same context
10. Unit tests for intent classifier with mock LLM

## Files

| File | Action |
|------|--------|
| `packages/voice/src/intent-classifier.ts` | CREATE |
| `packages/orchestrator/src/transports/voice.ts` | MODIFY (add proactive notifications) |

## Estimated Effort

1 week
