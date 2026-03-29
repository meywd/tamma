# Tamma Explainer Video Plan

This directory contains the complete production plan for two explainer videos about Tamma, the autonomous development platform.

## Videos

### 1. ELI5 Version (~75 seconds, 10 scenes)
**File**: [eli5-script.md](./eli5-script.md)
**Audience**: Anyone -- developers, managers, investors, curious people
**Tone**: Simple, friendly, slightly fun
**Goal**: Understand what Tamma does and why it matters in under 90 seconds

### 2. Deep Dive Version (~4 minutes, 18 scenes)
**File**: [deep-dive-script.md](./deep-dive-script.md)
**Audience**: Developers, engineering managers, DevOps teams, technical evaluators
**Tone**: Professional, detailed, compelling
**Goal**: Understand the architecture, differentiation, and real-world value of Tamma

## Supporting Files

| File | Purpose |
|------|---------|
| [style-guide.md](./style-guide.md) | Visual consistency guide for all generated images |
| [storyboard.md](./storyboard.md) | Scene-by-scene storyboard with timing and transitions |

## Production Pipeline

```
1. Review scripts and image prompts (this plan)
2. Generate images using Nano Banana MCP tool (one per scene)
3. Record narration (text-to-speech or human voiceover)
4. Stitch images + narration + transitions using Video LLM
5. Add background music and sound effects
6. Final review and export
```

## Key Facts About Tamma (for reference)

- **Name origin**: Arabic "tamm" -- "it is done", "it is complete"
- **What it does**: Autonomous development -- from issue assignment to merged PR, without human intervention for 70%+ of tasks
- **14-step loop**: Issue -> Plan -> Design -> Code -> Build -> Test -> Push -> CI/CD -> Review -> Fix comments -> Verify -> Merge -> Deploy -> Next issue
- **8+ AI providers**: Claude, GPT-4, Gemini, OpenRouter, OpenCode, Zen MCP, z.ai, local LLMs
- **7+ Git platforms**: GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git
- **Self-maintaining**: Tamma develops features for itself -- the ultimate validation
- **Dual-stack architecture**: TypeScript (Node.js) for providers/CLI/API, C# (.NET 8) ELSA Workflows for orchestration
- **Event sourcing**: Complete audit trail with time-travel debugging
- **Brand colors**: Purple #7B61FF, Green #10b981
- **Website**: tamma.dev
- **Repo**: github.com/meywd/tamma
- **Status**: Active implementation with 16 epics completed, 24 total epics planned
