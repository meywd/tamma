# AI Benchmark Research for AIBaaS

This directory contains research on existing AI benchmarks to inform the design of AIBaaS's own evaluation and leaderboard systems.

## Research Documents

### 01-MASK.md
**MASK (Model Alignment between Statements and Knowledge)** - Scale AI & CAIS

Key focus: Measuring **honesty** (truthfulness under pressure) vs **accuracy** (factual knowledge)

**Key Learnings**:
- Dual-prompting methodology: establish beliefs separately from pressured responses
- 7 archetypes of lying incentives (situation-provided facts, doubling down, fabrication, etc.)
- Statistical ranking with confidence intervals
- Evasion ≠ honesty (important distinction)
- Applicable to code honesty: admitting uncertainty, flagging vulnerabilities, avoiding false confidence

**Applicability**: HIGH - Direct methodology transfer to "Code Honesty Benchmark"

---

### 02-Aider.md
**Aider Code Editing Benchmark** - Polymath Systems

Key focus: Measuring **code editing accuracy** in real-world tasks

**Key Learnings**:
- Real repository editing tasks (not synthetic)
- Automated test suite verification
- Tracks edit accuracy, completion rate, cost per task
- Multi-turn conversation support

**Applicability**: MEDIUM - Useful for code generation quality metrics

---

### 03-VirologyTest.md
**Virology Test Benchmark** - [Research Paper]

Key focus: Measuring **domain expertise** in specialized fields (virology)

**Key Learnings**:
- Tests deep domain knowledge vs. surface-level understanding
- Multiple-choice format with expert-validated questions
- Prevents "memorization gaming" through reasoning requirements

**Applicability**: LOW-MEDIUM - Could adapt for "Code Security Expertise Test"

---

### 04-Hallucination-Benchmarks.md
**Hallucination Detection & Measurement** - Various sources

Key focus: Detecting and quantifying **false or fabricated information**

**Key Learnings**:
- Factuality vs. helpfulness tradeoff
- Citation-based verification methods
- Self-consistency checks
- Uncertainty quantification

**Applicability**: HIGH - Critical for code generation (prevent suggesting non-existent APIs, libraries, etc.)

---

### 05-LiveBench-LiveCodeBench.md
**LiveBench & LiveCodeBench** - Contamination-free benchmarks

Key focus: Measuring **real-time capabilities** with post-release test cases

**Key Learnings**:
- Monthly-updated test sets prevent training contamination
- LiveCodeBench: Code competition tasks (contests, code generation, test output prediction)
- Difficulty stratification (easy/medium/hard)
- Continuous monitoring of model capabilities

**Applicability**: HIGH - Contamination prevention critical for fair evaluation

---

### 07-Simple-Bench.md
**SimpleBench** - Adversarial Robustness Benchmark

Key focus: Measuring **linguistic adversarial robustness** ("trick questions") where humans dramatically outperform LLMs

**Key Learnings**:
- Humans (high school knowledge): 83.7% accuracy vs. best LLM (o1-preview): 41.7%
- 200+ questions testing spatio-temporal reasoning, social intelligence, adversarial robustness
- Coding-optimized models (GPT-4o: 13%) perform WORSE than general models
- Failure patterns: Overfitting to training data, reasoning fragmentation, missing common-sense
- Private test set (95%) prevents memorization and gaming
- 10 code-specific adversarial questions designed (misleading comments, red herrings, plausible bugs)

**Applicability**: HIGH - Reveals that code-optimized models may miss obvious bugs in review tasks

**Key Recommendation**: Create "Adversarial Code Review Benchmark" (20-25% of total AIBaaS score) with private question rotation

---

### 08-Domain-Specific-Benchmarks.md
**7 Domain-Specific Benchmarks** - Cybench, GeoBench, ForecastBench, VideoMMMU, BalrogAI, VPCT, VendingBench

Key focus: Evaluating AI capabilities in **specialized domains** (cybersecurity, geography, forecasting, video, gaming, physics, business)

**Key Learnings**:
- **Cybench (9/10 relevance)**: Security vulnerability detection directly applicable to code review
- **VideoMMMU**: Knowledge acquisition framework (perception → comprehension → adaptation)
- **VendingBench**: Long-horizon coherence testing reveals "doom loops" in multi-day tasks
- Universal principles: Multimodal testing, simulation environments, human baselines
- Patterns to avoid: Over-specialization, brittleness without mitigation, unclear baselines

**Applicability**: HIGH for Cybench (security), MEDIUM for methodologies (VideoMMMU, VendingBench)

**Key Recommendation**: Integrate Cybench-inspired security scanning into AIBaaS code review scenarios

---

### 09-IBM-HuggingFace-Analysis.md
**IBM LLM Benchmarks Article & Hugging Face Open LLM Leaderboard v2** - Industry Standards & Adjacent Competitors

Key focus: Industry-standard **evaluation metrics** and **adjacent competitor analysis**

**Key Learnings**:
- **IBM Article**: Standardized metrics (Accuracy, Precision, Recall, F1, Perplexity, BLEU, ROUGE)
- **Testing methodologies**: Zero-shot vs Few-shot vs Fine-tuned evaluation approaches
- **Hugging Face Leaderboard v2**: 2M+ users, 6 academic benchmarks (MMLU-PRO, GPQA, MuSR, MATH, IFEval, BBH)
- **Positioning**: Hugging Face = "SAT scores for AI" (academic), AIBaaS = "Job performance reviews" (developer workflows)
- **Contamination**: Both acknowledge contamination is an unsolved problem (validates AIBaaS 3-layer defense)
- **Context window bias**: MuSR benchmark favors 10k+ token context windows (AIBaaS should test multiple sizes)

**Applicability**: MEDIUM - Validates AIBaaS strategy, identifies complementary (not competitive) positioning

**Key Recommendations**:
1. Add few-shot testing variants for code generation scenarios
2. Enhance scoring with Precision/Recall/F1 metrics (IBM standards)
3. Add context window size as test variable (prevent bias)
4. Position as complementary to Hugging Face (partnership opportunity)

---

### 10-Vellum-Evidently-LiveBench-Analysis.md
**Vellum AI, Evidently AI, and LiveBench Website Analysis** - Additional Competitive Research

Key focus: Three additional **evaluation resources** for competitive landscape assessment

**Key Learnings**:
- **Vellum AI**: LLM development platform with leaderboard feature (moderate overlap, different core business)
  - Tracks cost (static provider pricing) and latency (TTFT, throughput)
  - NO historical trends, NO API access for leaderboard, NO alerting
  - Leaderboard is a supporting feature for their workflow platform (not core product)
- **Evidently AI**: LLM observability platform (NOT competitive - different category)
  - Internal monitoring tool (production apps) vs AIBaaS (public model comparison)
  - Different lifecycle stage (post-deployment vs pre-deployment)
  - Complementary use case ("monitor your app" vs "choose the best provider")
- **LiveBench.ai**: JavaScript-required site, validates existing research (no new data)

**Applicability**: LOW-MODERATE - Validates AIBaaS positioning, no major strategic changes needed

**Key Insight**: Vellum AI is closest overlap but serves different core business (development platform vs benchmarking service). AIBaaS wins on: dynamic $/task costs, historical trends, API access, alerting system, dedicated focus.

**Positioning Updates**:
- Vellum = "Build & deploy LLM apps" (platform)
- Evidently = "Monitor your LLM app" (observability)
- AIBaaS = "Choose the best AI provider" (benchmarking)

All three serve different use cases → complementary, not competitive.

---

## Synthesis: AIBaaS Benchmark Design Recommendations

### Proposed Benchmark Suite

1. **Code Honesty Benchmark (CHB)** - Inspired by MASK
   - Tests if models admit uncertainty vs. give false confidence
   - Pressure scenarios: deadlines, authority figures, user expectations
   - Categories: Uncertainty admission, security awareness, debugging confidence, performance claims

2. **Code Editing Accuracy** - Inspired by Aider
   - Real repository tasks with test suite verification
   - Tracks: edit accuracy, test pass rate, code quality metrics
   - Multi-turn conversation support

3. **Security Vulnerability Detection** - Inspired by Cybench
   - CTF-style security challenges adapted for code review
   - 6 categories: Crypto flaws, Web vulnerabilities (XSS, SQL injection, CSRF), Binary analysis, Forensics, Misc exploits, Privilege escalation
   - Agent-environment interaction with Docker containers
   - Scoring: Detection accuracy + classification accuracy + fix quality
   - Human baseline: Security auditor performance

4. **Long-Horizon Coherence** - Inspired by VendingBench
   - Multi-sprint development scenarios (3-7 days simulated)
   - Measures consistency across multiple runs (variance analysis)
   - Detects "doom loops" (infinite refactoring, over-engineering, misunderstanding requirements)
   - Tests ability to maintain context over extended timeframes

5. **Knowledge Acquisition from Documentation** - Inspired by VideoMMMU
   - Tests perception → comprehension → adaptation cognitive stages
   - Given codebase + documentation, measure Δknowledge improvement
   - Scenarios: Learn new API, understand architecture patterns, adapt to coding standards
   - Human baseline: Junior/mid/senior developer performance

6. **Hallucination Detection** - Synthesized from multiple sources
   - API/library existence checks
   - Security claim verification
   - Performance assertion validation
   - Citation accuracy for documentation

7. **Adversarial Code Review** - Inspired by SimpleBench
   - Trick questions testing robustness against misleading code patterns
   - 10 categories: Security red herrings, performance anti-patterns, type confusions, concurrency traps, etc.
   - 200 total questions (40 public, 160 private with quarterly rotation)
   - Overfitting penalty: Score gap between public vs private questions
   - Human baseline: Junior/mid/senior developers (target: 85%+ accuracy)
   - Weight: 20-25% of total benchmark score

8. **Contamination-Free Evaluation** - Inspired by LiveBench/LiveCodeBench
   - Monthly-updated test sets with post-release problems
   - Prevents training contamination
   - Difficulty stratification (easy/medium/hard)
   - Continuous monitoring of model capabilities

### UI/UX Patterns to Adopt

From MASK:
- Statistical ranking with confidence intervals (not raw scores)
- Contamination warnings for post-release evaluations
- Progressive disclosure (detailed methodology in sidebar)
- Company/provider color coding
- Archetype breakdown view (per-category performance)

From Aider:
- Cost-per-task metrics
- Task completion rate visualization
- Difficulty tier indicators

From Hallucination benchmarks:
- Factuality score separate from helpfulness score
- Citation verification status
- Confidence calibration metrics

From SimpleBench:
- Human baseline validation (ensure questions are answerable by non-experts)
- Private test set majority (80%+) to prevent memorization
- Overfitting detection (public vs private score gap)
- Quarterly question rotation schedule
- Multiple-choice format for objective evaluation

### Next Steps

1. **Prototype Code Honesty Benchmark**
   - Create 50 test cases across 3 categories
   - Manual evaluation with engineering team
   - Establish baseline for Claude/GPT-4/Codex

2. **Implement LLM Judge Pipeline**
   - Train/prompt GPT-4o for honesty classification
   - Validate against human expert judgments (>90% agreement)
   - Automate scoring for scale

3. **Build Leaderboard MVP**
   - Adapt MASK's UI patterns
   - Display: Overall honesty score, per-category breakdown, confidence intervals
   - Launch internally first, then public beta

4. **Iterate Based on User Feedback**
   - Track which metrics developers actually use for model selection
   - A/B test different UI presentations
   - Add new archetypes based on common failure modes

---

## Research Methodology

For each benchmark, we analyzed:
1. **Methodology**: How does it measure the target capability?
2. **Scoring System**: What metrics are used and why?
3. **UI/UX**: How is the leaderboard/results presented?
4. **Data Collection**: Scale, quality controls, test case design
5. **Unique Features**: What innovations can we learn from?
6. **Applicability to AIBaaS**: How can we adapt this for code-related tasks?

---

## Files

- `01-MASK.md` - MASK honesty benchmark research (29 KB)
- `02-Aider.md` - Aider code editing benchmark research (29 KB)
- `03-VirologyTest.md` - Virology domain expertise research (46 KB)
- `04-Hallucination-Benchmarks.md` - Hallucination detection research (51 KB)
- `05-LiveBench-LiveCodeBench.md` - Contamination-free benchmarks research (43 KB)
- `06-HLE-ARC.md` - HLE and ARC Prize advanced reasoning benchmarks (51 KB)
- `07-Simple-Bench.md` - SimpleBench adversarial robustness research with 10 code-specific trick questions (34 KB)
- `08-Domain-Specific-Benchmarks.md` - 7 domain-specific benchmarks comparative analysis (20 KB)
- `09-IBM-HuggingFace-Analysis.md` - IBM metrics + Hugging Face Leaderboard v2 competitive analysis (26 KB)
- `10-Vellum-Evidently-LiveBench-Analysis.md` - Vellum AI, Evidently AI, LiveBench website analysis
- `11-Artificial-Analysis.md` - Artificial Analysis competitive leaderboard (100+ models, general AI focus) ← NEW
- `mask-leaderboard-full.png` - Screenshot of MASK leaderboard (1.6 MB)
- `README.md` - This file

---

---

### 11-Artificial-Analysis.md
**Artificial Analysis** - General AI Model Leaderboard

Key focus: Comprehensive **general AI benchmarking** with pricing and performance tracking

**Key Learnings**:
- Tracks 100+ models from all major providers (OpenAI, Google, DeepSeek, xAI, etc.)
- Quality metrics: AIME (math), GPQA (science), Humaneval (coding), LiveCodeBench, MMLU Pro
- Performance metrics: Tokens/second, TTFT (latency), context window (128K-400K)
- Economic metrics: $/1M tokens (multi-provider pricing)
- Free access, sortable tables, filter by provider/size/reasoning type

**Applicability**: MEDIUM - Adjacent competitor (general AI vs our developer focus)

**Key Insight**: They focus on general intelligence (math, science), we focus on developer workflows (code gen, test gen, review, refactoring, debugging, security, docs). Different target audiences → complementary, not competitive.

**What We Do Better**:
- 7 languages (vs 1 - Python only)
- Developer-specific scenarios (vs general AI benchmarks)
- Multi-judge scoring (vs automated only)
- $/task pricing (vs $/1M tokens)
- Historical trends + alerts (vs static leaderboard)
- Private test bank with rotation (vs public benchmarks)

**What We Can Learn**:
- Sortable table UI (click columns to sort)
- Filter by provider (OpenAI, Anthropic, Google, etc.)
- Track context window size (important for large codebases)
- Track knowledge cutoff date (contamination prevention)
- Visual indicators (bars, colors for quick comparison)

**Positioning**: "Artificial Analysis for developers" - they tell you if a model is smart, we tell you if a model can write production code.

---

**Research Date**: November 2, 2024 (Updated: Added Artificial Analysis competitive leaderboard)
**Researcher**: Claude Code
**Status**: Comprehensive research phase complete, ready for benchmark design and prototyping

**Key Insight from SimpleBench**: Coding-optimized models (GPT-4o: 13%) dramatically underperform general models (o1: 41.7%) on adversarial reasoning tasks. This suggests code-specialized models may miss obvious bugs during code review. AIBaaS should test both generation accuracy AND adversarial robustness.

**Next Research Areas**:
- Industry-standard code quality benchmarks (SonarQube metrics, CodeClimate patterns)
- Human developer performance baselines (junior/mid/senior benchmarks)
- Real-world deployment metrics (production incident correlation with code quality scores)
