# Spike: AI Provider Comprehensive Test Bed

**Date**: 2024-10-30
**Author**: Development Team
**Related Story**: Story 1-0 (AI Provider Strategy Research)
**Status**: In Progress

## Purpose

Create a comprehensive test bed to empirically benchmark ALL AI providers across standardized scenarios. This addresses the critical gap identified in Story 1.0 where capability scores and token usage were estimates rather than measured values.

This is a **benchmarking suite**, not just a simple test - it runs the same prompts across all providers multiple times and scores results using automated criteria.

## Goals

1. Test ALL 8+ providers from Story 1.0 research (commercial + free/local)
2. Run SAME prompts across ALL providers for fair comparison
3. Execute MULTIPLE test runs (3-5 iterations) for statistical reliability
4. AUTOMATE scoring using objective criteria (not manual assessment)
5. Measure actual token usage, response time, and cost
6. Generate comparative analysis reports with rankings
7. Validate or refute Story 1.0 capability score estimates

## Providers to Test

All providers from Story 1.0 research, prioritized by free tier availability:

### Tier 1: Commercial with Free Tiers/Trials
1. **Google Gemini 2.5 Flash** - 60 requests/min free tier
2. **Google Gemini 2.5 Pro** - Limited free tier
3. **OpenAI GPT-4o-mini** - $5 free trial credits
4. **OpenAI GPT-4o** - Free trial (limited)
5. **Anthropic Claude 3.5 Sonnet** - Console credits
6. **Anthropic Claude 3 Haiku** - Console credits
7. **GitHub Copilot** - Free tier (2000 completions/month)

### Tier 2: Aggregator Services (Access Multiple Models)
8. **OpenRouter** - Free models: mixtral-8x7b, mistral-7b, llama2
9. **z.ai** - If free tier available
10. **Zen MCP** - If free tier available

### Tier 3: Local/Self-Hosted (100% Free)
11. **Ollama** - codellama:7b
12. **Ollama** - mistral:7b
13. **Ollama** - deepseek-coder:6.7b
14. **Ollama** - qwen2.5-coder:7b

### Tier 4: Open Source via Hugging Face
15. **HF Inference API** - CodeLlama-7B
16. **HF Inference API** - StarCoder

**Testing Strategy**: Start with Tier 1 (easiest setup), then Tier 2, then Tier 3 (requires local install)

## Test Scenarios

Based on Tamma workflow steps identified in Story 1.0:

### Scenario 1: Issue Analysis
**Input**: GitHub issue description for "Add user authentication with JWT tokens"
**Expected Output**: Requirements breakdown, ambiguity detection, scope identification
**Metrics**: Token usage, response quality (1-10 scale), response time

### Scenario 2: Code Generation
**Input**: "Write a TypeScript function to validate JWT tokens"
**Expected Output**: Complete function with error handling
**Metrics**: Token usage, code correctness, best practices adherence

### Scenario 3: Test Generation
**Input**: Code snippet from Scenario 2
**Expected Output**: Unit tests with Vitest
**Metrics**: Token usage, test coverage, test quality

### Scenario 4: Code Review
**Input**: Sample code with intentional issues (missing error handling, no input validation)
**Expected Output**: Identified issues with severity and recommendations
**Metrics**: Token usage, issue detection accuracy, false positives

## Automated Scoring Mechanism

Each scenario has objective scoring criteria (0-10 scale):

### Scenario 1: Issue Analysis Scoring
- **Structure (3 pts)**: Has requirements, ambiguities, scope sections
- **Completeness (3 pts)**: Covers JWT auth, registration, login, token expiry
- **Ambiguity Detection (2 pts)**: Identifies unclear requirements (e.g., password rules, user schema)
- **Scope Clarity (2 pts)**: Explicitly states in/out of scope items

**Auto-scoring**: Parse response for keywords, section headers, specific requirements

### Scenario 2: Code Generation Scoring
- **Syntax Validity (3 pts)**: Code parses as valid TypeScript (automated check)
- **Type Safety (2 pts)**: Has TypeScript types for function signature and return
- **Error Handling (2 pts)**: Has try-catch or throws for invalid inputs
- **Required Functionality (2 pts)**: Validates token format, decodes payload
- **Code Quality (1 pt)**: No `any` types, proper naming conventions

**Auto-scoring**: Run through TypeScript compiler, check for keywords (try/catch, throw, type annotations)

### Scenario 3: Test Generation Scoring
- **Syntax Validity (3 pts)**: Valid Vitest syntax (describe, it, expect)
- **Coverage (3 pts)**: Tests all 4 required cases (happy path, missing params, invalid format, malformed)
- **Assertions (2 pts)**: Uses appropriate expect() matchers
- **Structure (2 pts)**: Proper describe blocks and test organization

**Auto-scoring**: Parse test file, count test cases, check for Vitest imports and structure

### Scenario 4: Code Review Scoring
- **Issue Detection (4 pts)**: Identifies 4 planted issues (any type, missing error handling, unsafe fetch, no return await)
- **Severity Accuracy (2 pts)**: Assigns appropriate severity levels
- **Recommendations (2 pts)**: Provides actionable fixes
- **False Positives (2 pts)**: Deduct for incorrect issues flagged

**Auto-scoring**: Parse response for issue count, severity keywords, check against known issues

### Overall Quality Score
```
Overall Score = (
  Issue Analysis Score +
  Code Generation Score +
  Test Generation Score +
  Code Review Score
) / 4
```

### Statistical Aggregation
For N runs per provider (N=3-5):
- **Mean Score**: Average across all runs
- **Std Deviation**: Consistency measure
- **Min/Max**: Range of performance
- **Confidence**: High if std dev < 1.0, Medium if < 2.0, Low if >= 2.0

## Test Script Architecture

See implementation files:
- `providers/` - Provider implementations (16 providers)
- `scorers/` - Automated scoring logic per scenario
- `runners/` - Batch test execution
- `reporters/` - Analysis and comparison reports

## Results Format

Results will be captured in JSON format:

```json
{
  "testRun": {
    "timestamp": "2024-10-30T12:00:00.000Z",
    "scenario": "ISSUE_ANALYSIS",
    "provider": "google-gemini-flash"
  },
  "metrics": {
    "inputTokens": 234,
    "outputTokens": 456,
    "responseTimeMs": 1234,
    "cost": 0.0001
  },
  "quality": {
    "score": 8,
    "notes": "Good requirements breakdown, missed edge case for token expiration"
  },
  "response": "..."
}
```

## Setup Instructions

### Google Gemini (Recommended Starting Point)
```bash
# Get API key from https://aistudio.google.com/app/apikey
export GOOGLE_AI_API_KEY="your-key-here"
```

### Anthropic Claude
```bash
# Free tier via console: https://console.anthropic.com/
export ANTHROPIC_API_KEY="your-key-here"
```

### OpenAI
```bash
# Free trial: https://platform.openai.com/signup
export OPENAI_API_KEY="your-key-here"
```

### Ollama (Local)
```bash
# Install Ollama: https://ollama.ai/download
ollama pull codellama:7b
ollama pull mistral:7b
```

## Implementation Plan

1. **Phase 1**: Simple test script for Scenario 1 (Issue Analysis) with Gemini
2. **Phase 2**: Add OpenAI and Anthropic providers
3. **Phase 3**: Add local Ollama models
4. **Phase 4**: Expand to all 4 scenarios
5. **Phase 5**: Analyze results and update Story 1.0 findings

## Success Criteria

- [ ] Test script runs successfully with at least 2 providers
- [ ] Actual token usage measured and compared to estimates
- [ ] Quality scores assigned based on objective criteria
- [ ] Results documented in structured format
- [ ] Findings compared to Story 1.0 research estimates

## Risk Assessment

**Low Risk**:
- Free tier rate limits → Use delays between requests
- API key management → Use .env file, add to .gitignore
- Cost overruns → Stick to free tiers only

## Next Steps

1. Create minimal test script for Phase 1
2. Test with Google Gemini (most generous free tier)
3. Validate results format and metrics collection
4. Expand to additional providers
5. Document findings and update Story 1.0 if needed

## Notes

- This is exploratory work to validate research, not production implementation
- Focus on simple, working POC rather than comprehensive testing
- Results will inform decision on whether Story 1.0b (full empirical validation) is needed
- Keep test scenarios simple to stay within free tier limits
