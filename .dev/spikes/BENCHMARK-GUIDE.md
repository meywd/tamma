# AI Provider Comprehensive Benchmark Guide

## Overview

This test bed provides a rigorous, automated benchmarking system for AI providers to validate the research findings from Story 1.0. It addresses the critical gap where capability scores and costs were estimates rather than measured values.

## What Makes This a "Test Bed" Not Just a "POC"

### Comprehensive Coverage
- **5+ Providers**: Google Gemini, OpenAI, Anthropic, OpenRouter (aggregator), Ollama (local)
- **Multiple Models per Provider**: Tests both fast/cheap and high-quality models
- **4 Scenarios**: Issue analysis, code generation, test generation, code review
- **Statistical Rigor**: 3-5 iterations per test for reliability metrics

### Automated Quality Scoring
- **Objective Criteria**: No manual assessment required
- **0-10 Scale**: Consistent scoring across all providers
- **Detailed Breakdowns**: Per-criterion scores for debugging
- **Statistical Analysis**: Mean, std dev, min/max, confidence levels

### Fair Comparison
- **Same Prompts**: Identical input across all providers
- **Controlled Variables**: Same temperature (0.7), max tokens (2048)
- **Rate Limit Handling**: Delays between requests
- **Error Handling**: Graceful failures with retries

### Comprehensive Reporting
- **JSON Results**: Machine-readable data for further analysis
- **Markdown Reports**: Human-readable comparison tables
- **Multiple Perspectives**: Rankings by quality, cost, speed, value

## Architecture

```
.dev/spikes/
├── providers/              # Provider implementations
│   ├── base-provider.ts    # Base interface
│   ├── gemini-provider.ts
│   ├── openai-provider.ts
│   ├── anthropic-provider.ts
│   ├── openrouter-provider.ts
│   ├── ollama-provider.ts
│   └── index.ts            # Provider registry
│
├── scorers/                # Automated scoring logic
│   ├── issue-analysis-scorer.ts
│   ├── code-generation-scorer.ts
│   ├── test-generation-scorer.ts
│   ├── code-review-scorer.ts
│   └── index.ts
│
├── runners/                # Test execution
│   └── batch-runner.ts     # Multi-iteration batch runner
│
├── reporters/              # Analysis and reporting
│   └── comparison-reporter.ts
│
├── results/                # Output directory
│   ├── batch-results-*.json
│   └── benchmark-report-*.md
│
├── run-benchmark.ts        # Main CLI entry point
├── README-POC.md           # Quick start guide
└── BENCHMARK-GUIDE.md      # This file
```

## Scenarios Explained

### 1. Issue Analysis
**Goal**: Test ability to understand requirements and identify ambiguities

**Input**: GitHub issue requesting JWT authentication

**What We Score**:
- Does it break down requirements into clear items?
- Does it identify ambiguities (password rules, user schema)?
- Does it define scope boundaries?

**Why This Matters**: Tamma needs to analyze issues autonomously without human clarification

### 2. Code Generation
**Goal**: Test ability to write correct, type-safe, production-quality code

**Input**: Request to write JWT validation function

**What We Score**:
- Is it valid TypeScript?
- Does it have proper types (no `any`)?
- Does it handle errors?
- Does it implement the required functionality?

**Why This Matters**: Generated code must compile and work without manual fixes

### 3. Test Generation
**Goal**: Test ability to write comprehensive, valid test suites

**Input**: Code snippet + request to generate Vitest tests

**What We Score**:
- Is it valid Vitest syntax?
- Does it cover all required cases?
- Does it use appropriate assertions?
- Is it well-structured?

**Why This Matters**: Tamma must generate tests that actually run and provide coverage

### 4. Code Review
**Goal**: Test ability to identify real issues without false positives

**Input**: Intentionally flawed code with 5 planted issues

**What We Score**:
- How many of the 5 issues does it find?
- Does it assign correct severity levels?
- Does it provide actionable fixes?
- Does it flag issues that don't exist (false positives)?

**Why This Matters**: Code review must catch real bugs without creating busywork

## Scoring Methodology

### Objectivity
All scoring is automated using pattern matching, syntax validation, and keyword detection. No human judgment required.

### Granularity
Each scenario has 4-5 sub-criteria worth 1-4 points each, totaling 10 points per scenario.

### Repeatability
Same response always gets same score. Statistical variation comes from provider, not scorer.

### Validation
Scoring logic includes:
- TypeScript compiler for syntax validation
- Regex for keyword detection
- Structural parsing for organization
- Coverage analysis for completeness

## Usage Patterns

### Pattern 1: Quick Validation
```bash
# Test one provider quickly
tsx run-benchmark.ts --quick --providers gemini
```
**Use when**: Validating setup, debugging provider config, quick sanity check

### Pattern 2: Provider Comparison
```bash
# Compare 2-3 providers head-to-head
tsx run-benchmark.ts --providers gemini,openai,anthropic --iterations 3
```
**Use when**: Making provider selection decision, validating research findings

### Pattern 3: Scenario-Specific Testing
```bash
# Deep dive into one scenario
tsx run-benchmark.ts --scenarios code-generation --iterations 5
```
**Use when**: Debugging scoring logic, understanding provider strengths/weaknesses

### Pattern 4: Full Benchmark
```bash
# Complete statistical analysis
tsx run-benchmark.ts --iterations 5
```
**Use when**: Creating final research data, publishing results

## Interpreting Results

### Confidence Levels
- **High** (std dev < 1.0): Provider is very consistent
- **Medium** (std dev < 2.0): Some variation but acceptable
- **Low** (std dev >= 2.0): Highly variable, unreliable

### Cost/Quality Ratio
Lower is better. Measures dollars spent per quality point.

Example:
- Provider A: $0.01 cost, 8/10 score → Ratio: $0.00125
- Provider B: $0.001 cost, 6/10 score → Ratio: $0.00016 (better value)

### Efficiency Score
Quality per second of response time. Higher is better.

Example:
- Provider A: 8/10 score, 2000ms → Efficiency: 4.0 pts/sec
- Provider B: 7/10 score, 1000ms → Efficiency: 7.0 pts/sec (more efficient)

## Extending the Test Bed

### Adding a New Provider
1. Create `providers/my-provider.ts` implementing `BaseProvider`
2. Add to `providers/index.ts`
3. Update `.env.example` with API key variable
4. Test with `--providers my-provider`

### Adding a New Scenario
1. Add prompt to `SCENARIOS` in `batch-runner.ts`
2. Create `scorers/my-scenario-scorer.ts`
3. Add to scoring switch in `scorers/index.ts`
4. Test with `--scenarios my-scenario`

### Adjusting Scoring Criteria
1. Edit relevant scorer in `scorers/`
2. Adjust point allocations (must total 10)
3. Run benchmark to validate changes

## Validating Against Story 1.0 Research

### Comparison Checklist

The benchmark results can be compared against Story 1.0 estimates:

- [ ] **Capability Scores**: Compare research scores (Claude 9/10, GPT 7/10) to actual
- [ ] **Token Usage**: Compare estimated vs actual input/output tokens
- [ ] **Costs**: Validate pricing calculations with real API calls
- [ ] **Quality Rankings**: Confirm or refute provider ranking

### Expected Findings

**Hypothesis**: Research estimates are directionally correct but may differ quantitatively

**Validation Approach**:
1. Run full benchmark with 5 iterations
2. Generate comparison report
3. Create side-by-side table: Research Estimate vs Measured Value
4. Calculate error margins
5. Update Story 1.0 with actual data

## Best Practices

### 1. Start Small
Run `--quick` mode first to validate setup before full benchmark

### 2. Use Free Tiers Wisely
- Gemini: 60 req/min, 1500/day
- OpenRouter: Unlimited for free models
- Ollama: Unlimited (local)

### 3. Monitor Rate Limits
The benchmark includes automatic delays (2s default). If you hit rate limits:
- Increase `--delayMs` in code
- Reduce iterations
- Test fewer providers at once

### 4. Save Results
JSON files contain all raw data. Keep them for:
- Reproducing issues
- Deeper analysis
- Comparing across benchmark runs over time

### 5. Version Control
- **DO** commit: Code, scorers, documentation
- **DON'T** commit: .env, results/*.json, API responses

## Troubleshooting

### "No available providers found"
- Check environment variables are set
- Run `echo $GOOGLE_AI_API_KEY` to verify
- Try `--providers gemini` explicitly

### "Ollama API error"
- Start Ollama: `ollama serve`
- Pull models: `ollama pull codellama:7b`
- Set `OLLAMA_ENABLED=true`

### Scoring seems wrong
- Check `scorers/*.ts` logic
- Look at `details` array in results
- Adjust criteria if needed
- Re-run with `--iterations 1` to debug

### Tests timeout
- Increase timeout in provider code
- Check network connectivity
- Try different provider

## Future Enhancements

Potential additions (not yet implemented):

1. **More Providers**: GitHub Copilot, z.ai, Zen MCP
2. **More Scenarios**: Documentation generation, refactoring, PR descriptions
3. **Visual Reports**: Charts and graphs in HTML
4. **CI Integration**: Run benchmark on schedule
5. **Historical Tracking**: Compare results over time
6. **Custom Scenarios**: User-defined test cases
7. **Weighted Scoring**: Different weights per scenario
8. **Multi-Language**: Test providers on Python, Java, Go, etc.

## Contributing

To improve the test bed:

1. Add new providers following existing patterns
2. Enhance scoring logic with better validation
3. Add new scenarios for Tamma workflows
4. Improve statistical analysis
5. Create visualizations
6. Optimize for speed

## References

- **Story 1.0**: `docs/stories/1-0-ai-provider-strategy-research.md`
- **Research Findings**: `docs/research/ai-provider-strategy-2025-10.md`
- **Cost Analysis**: `docs/research/ai-provider-cost-analysis-2025-10.md`
- **Integration Guide**: `packages/providers/INTEGRATION.md`

---

**Last Updated**: 2024-10-30
**Version**: 1.0.0
**Maintainer**: Tamma Development Team
