# AI Provider Comprehensive Test Bed

This directory contains a comprehensive benchmarking suite to empirically test ALL AI providers across standardized scenarios with automated scoring.

**This is NOT just a simple POC** - it's a full-featured test bed that:
- Tests 5+ providers (Gemini, OpenAI, Anthropic, OpenRouter, Ollama)
- Runs same prompts across ALL providers for fair comparison
- Executes multiple iterations (3-5) for statistical reliability
- Automatically scores results using objective criteria
- Generates comparative analysis reports with rankings

## Quick Start

### 1. Install Dependencies

```bash
# Install TypeScript runner globally (if not already installed)
npm install -g tsx

# Install TypeScript for code validation (used by scorers)
npm install -g typescript
```

### 2. Run Quick Test (Recommended First Step)

```bash
cd .dev/spikes

# Test with Google Gemini only (easiest setup, most generous free tier)
export GOOGLE_AI_API_KEY="your-key-here"
tsx run-benchmark.ts --quick --providers gemini

# Or test all free providers at once
tsx run-benchmark.ts --quick
```

This will:
- Run 1 iteration per scenario
- Test only free tier providers (Gemini, OpenRouter, Ollama if available)
- Take ~2-3 minutes
- Generate comparison report in `results/`

### 3. Run Full Benchmark

```bash
# Set up API keys for providers you want to test
export GOOGLE_AI_API_KEY="your-key-here"
export OPENAI_API_KEY="your-key-here"
export ANTHROPIC_API_KEY="your-key-here"
export OPENROUTER_API_KEY="your-key-here"  # Optional

# Run full benchmark (3 iterations, all available providers)
tsx run-benchmark.ts

# Or customize
tsx run-benchmark.ts --providers gemini,openai --iterations 5
```

Full benchmark takes ~10-15 minutes depending on number of providers.

## Automated Scoring

Each test is automatically scored on a 0-10 scale using objective criteria:

### Issue Analysis (0-10 points)
- **Structure (3 pts)**: Has requirements, ambiguities, scope sections
- **Completeness (3 pts)**: Covers all key requirements (JWT, auth, registration, login, expiry)
- **Ambiguity Detection (2 pts)**: Identifies unclear requirements
- **Scope Clarity (2 pts)**: Explicitly states in/out of scope

### Code Generation (0-10 points)
- **Syntax Validity (3 pts)**: Valid TypeScript that compiles
- **Type Safety (2 pts)**: Proper TypeScript types, no `any`
- **Error Handling (2 pts)**: Try-catch blocks and input validation
- **Functionality (2 pts)**: Implements required JWT validation logic
- **Code Quality (1 pt)**: Clean code, good naming

### Test Generation (0-10 points)
- **Syntax Validity (3 pts)**: Valid Vitest syntax (describe, it, expect)
- **Coverage (3 pts)**: Tests all 4 required cases (happy path, missing params, invalid format, malformed)
- **Assertions (2 pts)**: Appropriate expect() matchers
- **Structure (2 pts)**: Well-organized describe blocks

### Code Review (0-10 points)
- **Issue Detection (4 pts)**: Identifies planted issues (any type, no error handling, etc.)
- **Severity Accuracy (2 pts)**: Appropriate severity levels (critical/high/medium/low)
- **Recommendations (2 pts)**: Actionable fixes provided
- **False Positives (2 pts)**: Penalty for incorrect issues flagged

## Output Files

After running the benchmark, you'll get:

### 1. JSON Results (`results/batch-results-<timestamp>.json`)
Complete test data including:
- Individual test results with scores, tokens, response times
- Aggregated statistics (mean, min, max, std dev)
- Raw API responses for debugging

### 2. Markdown Report (`results/benchmark-report-<timestamp>.md`)
Comprehensive comparison including:
- **Executive Summary**: Best overall, cheapest, fastest
- **Overall Rankings**: All providers ranked by score
- **Per-Scenario Analysis**: Best provider for each scenario
- **Cost Analysis**: Cost per test, cost/quality ratio
- **Performance Analysis**: Response times, efficiency scores
- **Recommendations**: Which provider to use for each use case

### Example Output
```
📊 Quick Summary:
   Best Overall: Anthropic Claude (claude-3-5-sonnet) - 8.7/10
   Most Cost-Effective: OpenRouter (mistral-7b-instruct:free) - $0.000000
   Fastest: Google Gemini (gemini-1.5-flash) - 845ms
```

## API Key Setup

Choose one or more providers to test:

#### Option A: Google Gemini (Recommended - Most Generous Free Tier)

1. Go to https://aistudio.google.com/app/apikey
2. Click "Create API Key"
3. Copy the key and set environment variable:

```bash
export GOOGLE_AI_API_KEY="your-key-here"
```

**Free Tier Limits**: 60 requests/minute, 1,500 requests/day

#### Option B: OpenAI GPT-4o-mini

1. Go to https://platform.openai.com/signup
2. Create account (gets $5 free trial credits)
3. Create API key at https://platform.openai.com/api-keys
4. Set environment variable:

```bash
export OPENAI_API_KEY="your-key-here"
```

**Free Trial**: $5 credit (expires after 3 months)

#### Option C: Anthropic Claude Haiku

1. Go to https://console.anthropic.com/
2. Sign up for account
3. Navigate to API Keys section
4. Create new key and set:

```bash
export ANTHROPIC_API_KEY="your-key-here"
```

**Free Tier**: Limited free usage via console credits

### 3. Run Your First Test

```bash
# Test Google Gemini with issue analysis scenario
tsx test-providers-poc.ts --provider gemini --scenario issue-analysis

# Test OpenAI with code generation scenario
tsx test-providers-poc.ts --provider openai --scenario code-generation

# Test Anthropic with code review scenario
tsx test-providers-poc.ts --provider anthropic --scenario code-review
```

## Available Test Scenarios

1. **issue-analysis** - Analyze GitHub issue, identify requirements and ambiguities
2. **code-generation** - Generate TypeScript function for JWT validation
3. **test-generation** - Generate Vitest tests for given code
4. **code-review** - Review code and identify issues with severity levels

## Results

Results are saved to `results/` directory as JSON files with format:
```
result-{provider}-{scenario}-{timestamp}.json
```

Example: `result-gemini-issue-analysis-2024-10-30T12-34-56-789Z.json`

Each result contains:
- **testRun**: Metadata (timestamp, provider, scenario)
- **metrics**: Token counts, response time, estimated cost
- **quality**: Manual quality score (to be filled in)
- **response**: Full AI response text
- **rawResponse**: Complete API response for debugging

## Comparing Results

To compare providers across the same scenario:

```bash
# Run same scenario with different providers
tsx test-providers-poc.ts --provider gemini --scenario code-generation
sleep 2  # Brief delay to avoid rate limits
tsx test-providers-poc.ts --provider openai --scenario code-generation
sleep 2
tsx test-providers-poc.ts --provider anthropic --scenario code-generation

# Review results in results/ directory
ls -lh results/result-*-code-generation-*.json
```

## Quality Assessment

After running tests, manually review results and update the `quality.score` field:

```json
{
  "quality": {
    "score": 8,  // Rate 1-10
    "notes": "Good code structure, missed error handling for expired tokens"
  }
}
```

## Cost Tracking

The script calculates estimated costs based on current pricing:

- **Google Gemini Flash**: $0.075/MTok input, $0.30/MTok output
- **OpenAI GPT-4o-mini**: $0.15/MTok input, $0.60/MTok output
- **Anthropic Claude Haiku**: $0.25/MTok input, $1.25/MTok output

All costs are tracked in the `metrics.estimatedCost` field.

## Troubleshooting

### Error: "API key not set"
```bash
# Make sure environment variable is set
echo $GOOGLE_AI_API_KEY
# If empty, export it:
export GOOGLE_AI_API_KEY="your-key-here"
```

### Error: "Rate limit exceeded"
- Wait 60 seconds and try again
- Google Gemini: Max 60 requests/minute
- Add delays between tests: `sleep 2`

### Error: "Insufficient credits" (OpenAI)
- Free trial credits expired or depleted
- Switch to Google Gemini (most generous free tier)

### Error: Module not found
```bash
# Install tsx globally
npm install -g tsx

# Or use npx
npx tsx test-providers-poc.ts --provider gemini --scenario issue-analysis
```

## Next Steps

1. **Run all scenarios** with your preferred provider
2. **Assess quality** manually and update scores
3. **Compare results** across providers
4. **Document findings** in the spike document
5. **Update Story 1.0** research if significant discrepancies found

## Security Notes

- **Never commit API keys** to Git
- API keys in environment variables only
- Add `results/*.json` to .gitignore (may contain sensitive responses)
- Use free tier limits to avoid unexpected charges

## References

- Spike Document: `2024-10-30-ai-provider-free-tier-poc.md`
- Related Story: `docs/stories/1-0-ai-provider-strategy-research.md`
- Research Findings: `docs/research/ai-provider-strategy-2025-10.md`
