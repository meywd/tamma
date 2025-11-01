# Testing the AI Provider Benchmark

This guide explains how to run the benchmark both locally and as an automated GitHub check.

## Table of Contents

1. [Local Testing](#local-testing)
2. [GitHub Actions (Automated)](#github-actions-automated)
3. [Setting Up Secrets](#setting-up-secrets)
4. [Interpreting Results](#interpreting-results)
5. [Troubleshooting](#troubleshooting)

---

## Local Testing

### Prerequisites

```bash
# Install dependencies
npm install -g tsx typescript

# Verify installation
tsx --version
tsc --version
```

### Quick Test (2-3 minutes)

```bash
# Navigate to spike directory
cd .dev/spikes

# Set API key (Google Gemini recommended for free tier)
export GOOGLE_AI_API_KEY="your-api-key-here"

# Run quick benchmark (1 iteration, Gemini only)
tsx run-benchmark.ts --quick --providers gemini
```

**Output:**
- `results/batch-results-<timestamp>.json` - Full test data
- `results/benchmark-report-<timestamp>.md` - Comparison report

### Full Local Benchmark (10-15 minutes)

```bash
# Set up all API keys you want to test
export GOOGLE_AI_API_KEY="..."
export OPENAI_API_KEY="..."
export ANTHROPIC_API_KEY="..."
export OPENROUTER_API_KEY="..."

# Run full benchmark (3 iterations, all providers)
tsx run-benchmark.ts

# Or customize
tsx run-benchmark.ts --providers gemini,openai --iterations 5
```

### Using .env File

```bash
# Copy example
cp .env.example .env

# Edit .env and add your API keys
nano .env

# Export variables
export $(cat .env | xargs)

# Run benchmark
tsx run-benchmark.ts --quick
```

### Available Options

```bash
# Get help
tsx run-benchmark.ts --help

# Test specific providers
tsx run-benchmark.ts --providers gemini,openai

# Test specific scenarios
tsx run-benchmark.ts --scenarios code-generation,test-generation

# Custom iterations
tsx run-benchmark.ts --iterations 5

# Quick mode (1 iteration, free providers only)
tsx run-benchmark.ts --quick
```

---

## GitHub Actions (Automated)

### Method 1: Manual Trigger via GitHub UI

1. **Navigate to Actions tab**
   - Go to: `https://github.com/meywd/tamma/actions`
   - Select "AI Provider Benchmark" workflow

2. **Click "Run workflow"**
   - Choose branch: `story/1-0-ai-provider-strategy-research`
   - Configure inputs:
     - **Providers**: `gemini,openai,anthropic` (or leave default)
     - **Iterations**: `3` (or your preference)
     - **Scenarios**: `all` (or specific scenarios)

3. **Click "Run workflow"** button

4. **Wait for completion** (~5-10 minutes)

5. **View results**
   - Check workflow summary for executive summary
   - Download artifacts for full JSON/MD reports

### Method 2: Triggered by PR

The benchmark automatically runs when:
- Opening/updating PR to `story/1-0-ai-provider-strategy-research` branch
- Changes affect files in `.dev/spikes/**` or `docs/research/**`

Results will be:
- Posted as PR comment with executive summary
- Available as workflow artifacts
- Shown in workflow summary

### Method 3: Using gh CLI

```bash
# Install gh CLI: https://cli.github.com/

# Authenticate
gh auth login

# Trigger workflow
gh workflow run ai-provider-benchmark.yml \
  --ref story/1-0-ai-provider-strategy-research \
  -f providers=gemini \
  -f iterations=3 \
  -f scenarios=all

# Check status
gh run list --workflow=ai-provider-benchmark.yml

# View logs
gh run view --log
```

---

## Setting Up Secrets

To run benchmarks in GitHub Actions, you need to set up API keys as repository secrets.

### Step 1: Get API Keys

1. **Google Gemini** (Free - 60 req/min)
   - Visit: https://aistudio.google.com/app/apikey
   - Click "Create API Key"
   - Copy the key

2. **OpenAI** (Free trial - $5 credits)
   - Visit: https://platform.openai.com/api-keys
   - Create new key
   - Copy the key

3. **Anthropic Claude** (Limited free credits)
   - Visit: https://console.anthropic.com/
   - Navigate to API Keys
   - Create new key

4. **OpenRouter** (Free models available)
   - Visit: https://openrouter.ai/
   - Sign up and get API key

### Step 2: Add Secrets to GitHub

1. **Navigate to repository settings**
   - Go to: `https://github.com/meywd/tamma/settings/secrets/actions`

2. **Click "New repository secret"**

3. **Add each API key:**
   - Name: `GOOGLE_AI_API_KEY`
   - Secret: `your-actual-api-key-here`
   - Click "Add secret"

4. **Repeat for other providers:**
   - `OPENAI_API_KEY`
   - `ANTHROPIC_API_KEY`
   - `OPENROUTER_API_KEY`

5. **Verify secrets are added**
   - You should see them listed (values hidden)

### Security Notes

- ✅ Secrets are encrypted and never exposed in logs
- ✅ Only accessible to GitHub Actions workflows
- ✅ Not visible to collaborators or in PR logs
- ⚠️ NEVER commit API keys to code or .env files
- ⚠️ NEVER share secrets in issues or discussions

---

## Interpreting Results

### Workflow Summary

After the workflow completes, check the Summary tab:

```
📊 Benchmark Results

Best Overall Quality
Google Gemini (gemini-1.5-pro) - 8.4/10 average score
- Confidence: high
- Range: 8.1-8.7

Most Cost-Effective
OpenRouter (mistral-7b-instruct:free) - $0.000000 total cost

Fastest Response
Google Gemini (gemini-1.5-flash) - 645ms average
```

### Artifacts

Download from workflow run:

1. **benchmark-results-json**
   - `batch-results-<timestamp>.json`
   - Complete test data
   - Individual test results
   - Aggregated statistics

2. **benchmark-report-md**
   - `benchmark-report-<timestamp>.md`
   - Formatted comparison tables
   - Rankings and recommendations

### PR Comments

When triggered by PR, the workflow posts:
- Executive summary in comment
- Collapsible full report
- Link to artifacts

### Understanding Scores

**Quality Score (0-10)**
- 9-10: Excellent - Production ready
- 7-8: Good - Minor issues only
- 5-6: Fair - Some significant issues
- 3-4: Poor - Major issues
- 0-2: Failed - Unusable output

**Confidence Level**
- High: Std dev < 1.0 (very consistent)
- Medium: Std dev < 2.0 (some variation)
- Low: Std dev >= 2.0 (highly variable)

**Cost/Quality Ratio**
- Lower is better
- Measures dollars per quality point
- Useful for comparing value

---

## Troubleshooting

### Local Issues

**Problem: "tsx: command not found"**
```bash
# Install tsx globally
npm install -g tsx

# Or use npx
npx tsx run-benchmark.ts --quick
```

**Problem: "No available providers found"**
```bash
# Check environment variables
echo $GOOGLE_AI_API_KEY

# If empty, export it
export GOOGLE_AI_API_KEY="your-key-here"

# Or create .env file
cp .env.example .env
# Edit .env with your keys
export $(cat .env | xargs)
```

**Problem: "Rate limit exceeded"**
```bash
# Reduce iterations
tsx run-benchmark.ts --iterations 1

# Test fewer providers
tsx run-benchmark.ts --providers gemini

# Increase delay (edit run-benchmark.ts)
# Change delayMs from 2000 to 5000
```

**Problem: "TypeScript errors in scorers"**
```bash
# Install TypeScript globally
npm install -g typescript

# Verify installation
tsc --version
```

### GitHub Actions Issues

**Problem: Workflow not appearing**
```bash
# Make sure workflow file is on the branch
git checkout story/1-0-ai-provider-strategy-research
ls .github/workflows/ai-provider-benchmark.yml

# Push if missing
git add .github/workflows/ai-provider-benchmark.yml
git commit -m "Add benchmark workflow"
git push
```

**Problem: "No API key found" in workflow**
- Check secrets are added: Settings → Secrets → Actions
- Verify secret names match exactly:
  - `GOOGLE_AI_API_KEY` (not `GOOGLE_API_KEY`)
  - `OPENAI_API_KEY`
  - `ANTHROPIC_API_KEY`

**Problem: Workflow timeout**
- Default timeout: 30 minutes
- If testing many providers, increase timeout:
  ```yaml
  timeout-minutes: 60
  ```

**Problem: Artifacts not uploading**
- Check results directory exists
- Look for errors in "Run benchmark" step
- Verify files were created: `ls .dev/spikes/results/`

### Test Without API Costs

Use Ollama (local) for testing without API costs:

```bash
# Install Ollama
curl -fsSL https://ollama.ai/install.sh | sh

# Pull models
ollama pull codellama:7b
ollama pull mistral:7b

# Start Ollama
ollama serve

# Run benchmark (in new terminal)
cd .dev/spikes
export OLLAMA_ENABLED=true
tsx run-benchmark.ts --providers ollama
```

---

## Quick Reference

### Local Commands

```bash
# Quick test (recommended first)
tsx run-benchmark.ts --quick

# Full benchmark
tsx run-benchmark.ts

# Custom test
tsx run-benchmark.ts --providers gemini,openai --iterations 5 --scenarios code-generation
```

### GitHub Actions

```bash
# Manual trigger via gh CLI
gh workflow run ai-provider-benchmark.yml -f providers=gemini -f iterations=3

# View recent runs
gh run list --workflow=ai-provider-benchmark.yml

# View logs
gh run view --log
```

### Files Created

```
.dev/spikes/results/
├── batch-results-2024-10-30T12-34-56.json
└── benchmark-report-2024-10-30T12-34-56.md
```

---

## Next Steps

1. **Run quick test locally** to validate setup
2. **Set up GitHub secrets** for automated testing
3. **Trigger workflow** via GitHub UI
4. **Review results** in artifacts and PR comments
5. **Compare to Story 1.0** research estimates

---

**Last Updated**: 2024-10-30
**Related Files**:
- Workflow: `.github/workflows/ai-provider-benchmark.yml`
- Benchmark Script: `.dev/spikes/run-benchmark.ts`
- Documentation: `.dev/spikes/BENCHMARK-GUIDE.md`
