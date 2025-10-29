# Story 1-10: Additional AI Provider Implementations

Status: ready-for-dev

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:
- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

As a **Tamma operator**,
I want support for multiple AI providers (OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, and local LLMs),
so that I can choose the optimal provider based on cost, capability, and deployment requirements.

## Acceptance Criteria

1. OpenAI provider implements IAIProvider interface with support for GPT-4, GPT-3.5-turbo, and o1 models
2. GitHub Copilot provider implements IAIProvider interface with Copilot API integration
3. Google Gemini provider implements IAIProvider interface with support for Gemini Pro and Ultra models
4. OpenCode provider implements IAIProvider interface with OpenCode API integration
5. z.ai provider implements IAIProvider interface with z.ai API integration
6. Zen MCP provider implements IAIProvider interface with Model Context Protocol support
7. OpenRouter provider implements IAIProvider interface with multi-model routing support
8. Local LLM provider implements IAIProvider interface with support for Ollama, LM Studio, and vLLM backends
9. Each provider includes comprehensive error handling, retry logic, and streaming support
10. Provider selection configurable via config file or environment variables
11. Integration tests validate each provider with real API calls (or mocked for local LLMs)
12. Documentation includes provider comparison matrix and setup instructions for each provider

## Prerequisites

- Story 1-0: AI Provider Strategy Research (must identify which providers to prioritize)
- Story 1-1: AI Provider Interface Definition (interface must exist)
- Story 1-2: Claude Code Provider Implementation (reference implementation pattern established)
- Story 1-3: Provider Configuration Management (configuration system ready)

## Tasks / Subtasks

### Task 1: OpenAI Provider Implementation (AC: 1, 7, 9)

- [ ] Subtask 1.1: Create OpenAIProvider class implementing IAIProvider
- [ ] Subtask 1.2: Integrate `openai` SDK (^4.x) for API access
- [ ] Subtask 1.3: Implement model selection (GPT-4, GPT-3.5-turbo, o1-preview, o1-mini)
- [ ] Subtask 1.4: Add streaming support with Server-Sent Events
- [ ] Subtask 1.5: Implement function calling for tool integration
- [ ] Subtask 1.6: Add error handling (rate limits, token limits, content policy violations)
- [ ] Subtask 1.7: Implement retry logic with exponential backoff
- [ ] Subtask 1.8: Add telemetry (latency, token usage, cost tracking)
- [ ] Subtask 1.9: Write unit and integration tests

### Task 2: GitHub Copilot Provider Implementation (AC: 2, 7, 9)

- [ ] Subtask 2.1: Create GitHubCopilotProvider class implementing IAIProvider
- [ ] Subtask 2.2: Integrate GitHub Copilot API (authentication via GitHub App or OAuth)
- [ ] Subtask 2.3: Implement code completion and generation endpoints
- [ ] Subtask 2.4: Add streaming support for real-time completions
- [ ] Subtask 2.5: Implement context awareness (file context, repository context)
- [ ] Subtask 2.6: Add error handling (rate limits, auth failures, quota exceeded)
- [ ] Subtask 2.7: Implement retry logic with exponential backoff
- [ ] Subtask 2.8: Add telemetry (request counts, completion quality metrics)
- [ ] Subtask 2.9: Write unit and integration tests

### Task 3: Google Gemini Provider Implementation (AC: 3, 7, 9)

- [ ] Subtask 3.1: Create GeminiProvider class implementing IAIProvider
- [ ] Subtask 3.2: Integrate `@google/generative-ai` SDK for Gemini API
- [ ] Subtask 3.3: Implement model selection (Gemini Pro, Gemini Ultra, Gemini 1.5)
- [ ] Subtask 3.4: Add streaming support with SSE
- [ ] Subtask 3.5: Implement function calling for tool integration
- [ ] Subtask 3.6: Add multimodal support (if needed for code analysis)
- [ ] Subtask 3.7: Add error handling (rate limits, quota, safety filters)
- [ ] Subtask 3.8: Implement retry logic with exponential backoff
- [ ] Subtask 3.9: Add telemetry (latency, token usage, model version tracking)
- [ ] Subtask 3.10: Write unit and integration tests

### Task 4: OpenCode Provider Implementation (AC: 4, 9, 11)

- [ ] Subtask 4.1: Research OpenCode API capabilities and authentication
- [ ] Subtask 4.2: Create OpenCodeProvider class implementing IAIProvider
- [ ] Subtask 4.3: Integrate OpenCode SDK or REST API client
- [ ] Subtask 4.4: Implement model selection (if multiple models available)
- [ ] Subtask 4.5: Add streaming support (if available)
- [ ] Subtask 4.6: Implement tool integration (if supported)
- [ ] Subtask 4.7: Add error handling (API-specific errors, rate limits)
- [ ] Subtask 4.8: Implement retry logic with exponential backoff
- [ ] Subtask 4.9: Add telemetry (latency, usage tracking, cost if applicable)
- [ ] Subtask 4.10: Write unit and integration tests

### Task 5: z.ai Provider Implementation (AC: 5, 9, 11)

- [ ] Subtask 5.1: Research z.ai API capabilities and authentication
- [ ] Subtask 5.2: Create ZAIProvider class implementing IAIProvider
- [ ] Subtask 5.3: Integrate z.ai SDK or REST API client
- [ ] Subtask 5.4: Implement model selection (if multiple models available)
- [ ] Subtask 5.5: Add streaming support (if available)
- [ ] Subtask 5.6: Implement tool integration (if supported)
- [ ] Subtask 5.7: Add error handling (API-specific errors, rate limits)
- [ ] Subtask 5.8: Implement retry logic with exponential backoff
- [ ] Subtask 5.9: Add telemetry (latency, usage tracking)
- [ ] Subtask 5.10: Write unit and integration tests

### Task 6: Zen MCP Provider Implementation (AC: 6, 9, 11)

- [ ] Subtask 6.1: Research Zen MCP protocol and authentication requirements
- [ ] Subtask 6.2: Create ZenMCPProvider class implementing IAIProvider
- [ ] Subtask 6.3: Integrate Model Context Protocol (MCP) SDK
- [ ] Subtask 6.4: Implement MCP server connection and model selection
- [ ] Subtask 6.5: Add streaming support via MCP protocol
- [ ] Subtask 6.6: Implement MCP tool/resource integration for context access
- [ ] Subtask 6.7: Add error handling (MCP-specific errors, connection failures)
- [ ] Subtask 6.8: Implement retry logic with exponential backoff
- [ ] Subtask 6.9: Add telemetry (latency, MCP message counts, context usage)
- [ ] Subtask 6.10: Write unit and integration tests with MCP server mock

### Task 7: OpenRouter Provider Implementation (AC: 7, 9, 11)

- [ ] Subtask 7.1: Create OpenRouterProvider class implementing IAIProvider
- [ ] Subtask 7.2: Integrate OpenRouter API (unified API for multiple models)
- [ ] Subtask 7.3: Implement model routing (support for 100+ models: GPT, Claude, Llama, etc.)
- [ ] Subtask 7.4: Add streaming support with SSE
- [ ] Subtask 7.5: Implement cost-optimized model selection (per-request cost awareness)
- [ ] Subtask 7.6: Add error handling (model unavailability, rate limits, auth failures)
- [ ] Subtask 7.7: Implement retry logic with model fallback on failure
- [ ] Subtask 7.8: Add telemetry (per-model latency, cost tracking, model selection decisions)
- [ ] Subtask 7.9: Write unit and integration tests

### Task 8: Local LLM Provider Implementation (AC: 8, 9, 11)

- [ ] Subtask 8.1: Create LocalLLMProvider class implementing IAIProvider
- [ ] Subtask 8.2: Add Ollama backend integration (HTTP API at localhost:11434)
- [ ] Subtask 8.3: Add LM Studio backend integration (OpenAI-compatible API)
- [ ] Subtask 8.4: Add vLLM backend integration (OpenAI-compatible API)
- [ ] Subtask 8.5: Implement model discovery (list available local models)
- [ ] Subtask 8.6: Add streaming support (SSE from local server)
- [ ] Subtask 8.7: Implement function calling (if supported by local model)
- [ ] Subtask 8.8: Add error handling (server unreachable, model not loaded, OOM errors)
- [ ] Subtask 8.9: Implement retry logic (with model restart on OOM)
- [ ] Subtask 8.10: Add telemetry (inference latency, VRAM usage, model performance)
- [ ] Subtask 8.11: Add model recommendation guide (hardware requirements, recommended models for code generation)
- [ ] Subtask 8.12: Write unit and integration tests (mock local server)

### Task 9: Provider Selection and Configuration (AC: 10)

- [ ] Subtask 9.1: Extend ProviderConfigManager to support new providers
- [ ] Subtask 9.2: Add provider auto-detection (try providers in priority order)
- [ ] Subtask 9.3: Implement provider fallback strategy (if primary fails, try secondary)
- [ ] Subtask 9.4: Add per-workflow-step provider selection (e.g., use GPT-4 for analysis, local LLM for code completion)
- [ ] Subtask 9.5: Add cost-aware provider selection (switch to cheaper provider for simple tasks)
- [ ] Subtask 9.6: Document configuration examples for each provider

### Task 10: Documentation and Provider Comparison (AC: 12)

- [ ] Subtask 10.1: Create provider comparison matrix (cost, speed, quality, features)
- [ ] Subtask 10.2: Write setup guide for OpenAI provider
- [ ] Subtask 10.3: Write setup guide for GitHub Copilot provider
- [ ] Subtask 10.4: Write setup guide for Google Gemini provider
- [ ] Subtask 10.5: Write setup guide for OpenCode provider
- [ ] Subtask 10.6: Write setup guide for z.ai provider
- [ ] Subtask 10.7: Write setup guide for Zen MCP provider
- [ ] Subtask 10.8: Write setup guide for OpenRouter provider
- [ ] Subtask 10.9: Write setup guide for local LLM provider (Ollama/LM Studio/vLLM installation)
- [ ] Subtask 10.10: Create troubleshooting guide for common provider issues
- [ ] Subtask 10.11: Document cost projection for each provider (per workflow, per month)
- [ ] Subtask 10.12: Create provider selection decision tree (help users choose optimal provider)

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Requirements Context Summary

**Epic 1 Extension:** This story extends Epic 1 with support for multiple AI providers beyond the initial reference implementation. Each provider follows the same IAIProvider interface pattern established in Story 1-2, ensuring consistent behavior across all providers.

**Strategic Importance:** Multi-provider support enables:
- **Cost Optimization**: Use cheaper providers for simple tasks, premium providers for complex tasks
- **Vendor Flexibility**: Avoid lock-in, switch providers based on pricing/availability
- **Deployment Flexibility**: Support air-gapped environments with local LLMs
- **Quality Optimization**: Use best-in-class provider for each workflow step

**Provider Priority:** Story 1-0 research findings will determine implementation order. Likely priority:
1. OpenAI (broad adoption, mature API)
2. Local LLMs (cost-sensitive users, air-gapped deployments)
3. GitHub Copilot (GitHub users, IDE integration)
4. Google Gemini (long context windows, multimodal)
5. OpenRouter (cost optimization, model aggregation)
6. Zen MCP (Model Context Protocol for tool-rich workflows)
7. OpenCode (TBD - research required)
8. z.ai (depending on research findings)

**Implementation Pattern:** Each provider follows the same structure as Story 1-2:
1. Class implementing IAIProvider interface
2. SDK/API integration with authentication
3. Model selection and capability discovery
4. Streaming support
5. Error handling and retry logic
6. Telemetry and monitoring
7. Unit and integration tests

### Project Structure Notes

**Package Location:** `packages/providers/src/` with separate files per provider:
- `openai-provider.ts` - OpenAI implementation
- `github-copilot-provider.ts` - GitHub Copilot implementation
- `gemini-provider.ts` - Google Gemini implementation
- `opencode-provider.ts` - OpenCode implementation
- `zai-provider.ts` - z.ai implementation
- `zen-mcp-provider.ts` - Zen MCP implementation
- `openrouter-provider.ts` - OpenRouter implementation
- `local-llm-provider.ts` - Local LLM implementation (Ollama/LM Studio/vLLM)

**Dependencies:**
- `openai@^4.67.0` - OpenAI SDK
- `@google/generative-ai@^0.21.0` - Google Gemini SDK
- `@modelcontextprotocol/sdk@^1.0.0` - Model Context Protocol SDK (for Zen MCP)
- `axios@^1.7.9` - HTTP client for OpenRouter, OpenCode, z.ai, local LLMs
- Provider-specific SDKs as needed (GitHub Copilot, OpenCode, z.ai)

**Configuration Schema:** Extend `~/.tamma/providers.json`:
```json
{
  "providers": [
    {
      "name": "openai",
      "enabled": true,
      "apiKey": "${OPENAI_API_KEY}",
      "model": "gpt-4o",
      "options": {
        "temperature": 0.7,
        "maxTokens": 4096
      }
    },
    {
      "name": "github-copilot",
      "enabled": false,
      "apiKey": "${GITHUB_TOKEN}",
      "options": {
        "contextLength": 8192
      }
    },
    {
      "name": "gemini",
      "enabled": false,
      "apiKey": "${GEMINI_API_KEY}",
      "model": "gemini-1.5-pro",
      "options": {
        "temperature": 0.7
      }
    },
    {
      "name": "openrouter",
      "enabled": false,
      "apiKey": "${OPENROUTER_API_KEY}",
      "model": "anthropic/claude-3.5-sonnet",
      "options": {
        "fallbackModels": ["openai/gpt-4", "google/gemini-pro"]
      }
    },
    {
      "name": "local-llm",
      "enabled": false,
      "backend": "ollama",
      "baseUrl": "http://localhost:11434",
      "model": "codellama:34b",
      "options": {
        "numGpu": 1,
        "contextLength": 16384
      }
    }
  ],
  "defaultProvider": "anthropic-claude",
  "fallbackProvider": "openai",
  "perWorkflowProviders": {
    "issue-analysis": "openai",
    "code-generation": "anthropic-claude",
    "test-generation": "local-llm",
    "code-review": "gemini"
  }
}
```

**Testing Strategy:**
- **Unit Tests**: Mock API responses for each provider
- **Integration Tests**: Real API calls with test accounts (rate limited)
- **Local LLM Tests**: Mock localhost server, document manual testing steps
- **Provider Fallback Tests**: Simulate provider failures, verify fallback works

### Provider-Specific Implementation Notes

**OpenAI:**
- Use `openai` SDK v4 (major rewrite from v3)
- Support GPT-4o, GPT-4-turbo, GPT-3.5-turbo, o1-preview, o1-mini
- Function calling for tool integration
- Streaming via Server-Sent Events
- Rate limits: 10,000 TPM (tokens per minute) for GPT-4 (tier 1)

**GitHub Copilot:**
- Requires GitHub App or OAuth authentication
- May require GitHub Copilot Business/Enterprise subscription for API access
- API endpoint: `https://api.github.com/copilot/`
- Context awareness via repository indexing
- Rate limits: TBD (check GitHub Copilot API docs)

**Google Gemini:**
- Use `@google/generative-ai` SDK
- Support Gemini 1.5 Pro (long 1M context), Gemini Pro
- Function calling for tool integration
- Multimodal support (code + diagrams if needed)
- Rate limits: 60 requests/minute (free tier), higher for paid

**z.ai:**
- Research API capabilities (Story 1-0 should investigate)
- If API not public, defer this provider
- Document authentication and endpoint details
- Unknown rate limits (TBD)

**OpenRouter:**
- Unified API for 100+ models from multiple providers
- API endpoint: `https://openrouter.ai/api/v1/`
- OpenAI-compatible API format
- Per-model pricing (cost tracking important)
- Rate limits: Varies per upstream provider
- Fallback strategy: If model fails, try different model

**Local LLMs:**
- **Ollama**: HTTP API at `http://localhost:11434`, pull models with `ollama pull codellama`
- **LM Studio**: OpenAI-compatible API at `http://localhost:1234/v1/`
- **vLLM**: OpenAI-compatible API, requires GPU, high performance
- Recommended models for code generation:
  - CodeLlama 34B (requires 24GB VRAM)
  - DeepSeek Coder 33B (requires 24GB VRAM)
  - Mistral 7B (requires 8GB VRAM, lower quality)
  - Llama 3.1 70B (requires 48GB VRAM, highest quality)
- No rate limits (hardware limited)
- Latency: 10-50 tokens/sec depending on hardware

### Cost Comparison (Estimated)

**Per 1M Tokens** (Input/Output):
- **OpenAI GPT-4o**: $2.50 / $10.00
- **OpenAI GPT-3.5-turbo**: $0.50 / $1.50
- **Anthropic Claude 3.5 Sonnet**: $3.00 / $15.00
- **Google Gemini 1.5 Pro**: $1.25 / $5.00
- **OpenRouter (varies)**: $0.50 - $10.00 (depends on model)
- **Local LLM**: $0.00 (upfront hardware cost: $1,000-$5,000)

**Subscription Plans:**
- **GitHub Copilot Business**: $19/user/month (includes API access?)
- **OpenAI Teams**: $25/user/month (does NOT include API credits)
- **Google Workspace AI**: $30/user/month (includes Gemini)

**Cost Optimization Strategy:**
- Use local LLM for simple tasks (code completion, documentation)
- Use GPT-3.5-turbo for medium tasks (test generation)
- Use GPT-4o/Claude for complex tasks (architecture analysis, code review)
- Use OpenRouter for cost-aware routing

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions

- [OpenAI API Documentation](https://platform.openai.com/docs)
- [GitHub Copilot API Documentation](https://docs.github.com/en/copilot)
- [Google Gemini API Documentation](https://ai.google.dev/docs)
- [OpenRouter API Documentation](https://openrouter.ai/docs)
- [Ollama Documentation](https://ollama.ai/docs)
- [LM Studio Documentation](https://lmstudio.ai/docs)
- [vLLM Documentation](https://docs.vllm.ai/)
- [Source: docs/tech-spec-epic-1.md#AI-Provider-Abstraction](F:\Code\Repos\Tamma\docs\tech-spec-epic-1.md)
- [Source: docs/stories/1-0-ai-provider-strategy-research.md](F:\Code\Repos\Tamma\docs\stories\1-0-ai-provider-strategy-research.md)

## Change Log

| Date | Version | Changes | Author |
|------|---------|----------|--------|
| 2025-10-28 | 1.0.0 | Initial story creation for multi-provider support | Bob (Scrum Master) |

## Dev Agent Record

### Context Reference

- docs/stories/1-10-additional-ai-provider-implementations.context.xml

### Agent Model Used

TBD - assigned during development

### Debug Log References

TBD - added during development

### Completion Notes List

- [ ] All 6 providers implemented and tested
- [ ] Provider comparison matrix validated with real usage data
- [ ] Cost projections confirmed with actual API usage
- [ ] Local LLM setup guide validated on multiple hardware configurations
- [ ] Integration tests passing for all providers

### Key Decisions

- [ ] Provider implementation order determined (based on Story 1-0 research)
- [ ] Per-workflow-step provider selection strategy finalized
- [ ] Provider fallback logic implemented (primary → secondary → tertiary)
- [ ] Cost optimization rules defined (when to use which provider)
