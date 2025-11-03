# Domain-Specific Benchmarks: Comparative Analysis

**Research Date**: November 1, 2025
**Researcher**: Claude (Anthropic)
**Purpose**: Evaluate 7 domain-specific benchmarks from Reddit's r/LLMBenchmarks for AIBaaS relevance

## Executive Summary

This analysis examines 7 specialized benchmarks testing AI capabilities in cybersecurity, geography, forecasting, video understanding, gaming, physics, and business simulation. **Key finding**: Only **Cybench (cybersecurity)** has high relevance to software development (9/10), while others provide insights into multimodal testing and simulation methodologies that could inform AIBaaS benchmark design.

### Recommendation for AIBaaS Marketing

**Reference in marketing materials**:
1. **Cybench** - Direct relevance to code security and vulnerability detection
2. **VideoMMMU** - Example of knowledge acquisition testing methodology
3. **VendingBench** - Long-horizon coherence testing pattern applicable to multi-day development tasks

**Avoid referencing**:
- GeoBench, ForecastBench, BalrogAI, VPCT - Too domain-specific, low developer relevance

---

## Comparative Analysis Table

| Benchmark | Domain Focus | Test Methodology | Scoring System | Top Model Performance | Human Baseline | Dev Relevance (0-10) | Key Insight for AIBaaS |
|-----------|-------------|------------------|----------------|----------------------|----------------|---------------------|------------------------|
| **Cybench** | Cybersecurity (CTF challenges) | Agent-environment interaction with Kali Linux container, 40 professional-level CTF tasks across 6 categories | 4 metrics: Unguided % Solved, Subtask-Guided % Solved, Subtasks % Solved, Most Difficult Task Solved | Claude Sonnet 4.5: **71.8%** (doubled from 35.9% in Feb 2025) | Professional CTF "first solve time" from competitions | **9/10** | Security scanning is critical for code review; could integrate security benchmarks into AIBaaS |
| **GeoBench** | Location identification (GeoGuessr-style) | Photo-based location prediction with lat/long coordinates + country guess | Distance-based scoring (5000 max points for <25m or 0.001% of max distance), exponential decay penalty | Data not publicly available on leaderboard | Not specified | **1/10** | Demonstrates visual recognition + spatial reasoning, but no software dev application |
| **ForecastBench** | Prediction markets & forecasting | Dynamic contamination-free forecasting with 2 tracks (Baseline = raw model, Tournament = enhanced) | Difficulty-adjusted Brier score (lower is better) | GPT-4.5: **0.101** Brier score | Superforecasters: **0.081** Brier score (still leading) | **3/10** | Long-term prediction accuracy could inform sprint planning AI, but too specialized |
| **VideoMMMU** | Video-based knowledge acquisition | 300 expert-level lecture videos + 900 questions across 6 disciplines (Art, Business, Science, Medicine, Humanities, Engineering) | Δknowledge = (Acc_after - Acc_before) / (100% - Acc_before) × 100% | GPT-5-thinking: **84.6%** overall, Claude 3.5 Sonnet: **65.78%** + 11.4% Δknowledge | Humans: **33.1%** Δknowledge (3x better than models) | **2/10** | Knowledge acquisition testing methodology applicable to documentation comprehension |
| **BalrogAI** | Videogame playing (7 game environments) | Long-horizon agentic tasks testing planning, spatial reasoning, exploration in procedurally generated worlds | % Progress (average completion across environments) | LLM: Grok-4 **43.6%**, VLM: Gemini 2.5 Pro **35.7%**, Claude 3.5 Sonnet **35.5%** | Not specified | **4/10** | Long-horizon planning + procedural generation parallels code generation, but gaming is too indirect |
| **VPCT** | Physics puzzles (visual prediction) | 100 problems: predict which bucket a ball falls into given ramps/obstacles | Accuracy % across 100 problems | GPT-5 Pro High: **71%**, Claude Sonnet 4.5: **39.8-63%** (conflicting reports) | **100%** (humans find trivial) | **2/10** | Reveals brittleness in grounded physical reasoning, but not applicable to software development |
| **VendingBench** | Business simulation (vending machine) | Manage inventory, ordering, pricing, cash flow over ~325 simulated days (5 runs per model) | Net worth (mean/min), units sold (mean/min), days until sales stop | Grok 4: **$4,694** mean, GPT-5: **$3,578**, Claude Opus 4: **$2,077** | Human: **$844** (1 participant, 67 days) | **5/10** | Long-term coherence testing applicable to multi-sprint development, high variance reveals brittleness |

---

## Deep Dive: Cybench (Cybersecurity Benchmark)

### Why Cybench Matters for AIBaaS

**Direct relevance to code review**: Cybersecurity is a critical quality gate in software development. AIBaaS could integrate security-focused scenarios inspired by Cybench to test models' ability to:

1. **Detect vulnerabilities** (SQL injection, XSS, CSRF)
2. **Identify cryptographic implementation flaws**
3. **Reverse engineer binaries** (malware analysis)
4. **Perform forensic analysis** (log inspection, data extraction)
5. **Exploit/patch privilege escalation bugs**

### Cybench Task Categories (6 domains)

| Category | Description | AIBaaS Parallel |
|----------|-------------|----------------|
| **Crypto** | Cryptographic implementation flaws | Detecting weak encryption in codebase |
| **Web** | XSS, CSRF, SQL injection vulnerabilities | Security code review for web apps |
| **Rev** | Binary reverse engineering | Analyzing compiled artifacts for backdoors |
| **Forensics** | Data extraction and analysis | Log analysis for security incidents |
| **Misc** | Unconventional vulnerability exploitation | Edge case security testing |
| **Pwn** | Privilege escalation and code execution | Identifying privilege bugs in backend code |

### Performance Trajectory (Claude Models)

| Model | Release Date | Unguided % Solved | Improvement |
|-------|-------------|------------------|-------------|
| Claude 3.5 Sonnet | Mid-2024 | 17.5% | Baseline |
| Claude Sonnet 3.7 | Feb 2025 | 35.9% | +105% |
| **Claude Sonnet 4.5** | **Oct 2025** | **71.8%** | **+100%** (6 months) |

**Key insight**: Cybench performance doubled in 6 months, demonstrating rapid improvement in security capabilities. This suggests models are becoming viable for automated security scanning in code review.

### Cybench Methodology: Agent-Environment Interaction

**Setup**:
- Agent receives task description + starter files
- Interacts with Kali Linux container via bash commands and network calls
- Builds memory from observations before submitting answer

**Scoring**:
1. **Unguided % Solved**: Success without subtask guidance (primary metric)
2. **Subtask-Guided % Solved**: Success with intermediate step hints
3. **Subtasks % Solved**: Macro-averaged subtask completion rates
4. **Most Difficult Task Solved**: Highest first-solve time among completed challenges

### Integration into AIBaaS Code Review Scenario

**Proposal**: Add a "Security Scanning" gate to AIBaaS benchmark

**Scenario**:
1. Provide model with codebase containing known vulnerabilities (e.g., OWASP Top 10)
2. Model must:
   - Identify vulnerability type (XSS, SQL injection, etc.)
   - Explain security impact
   - Propose code fix
3. Scoring:
   - Detection accuracy (did it find the vulnerability?)
   - Classification accuracy (correct vulnerability type?)
   - Fix quality (does the patch eliminate the vulnerability without breaking functionality?)

**Example benchmark task** (inspired by Cybench "Web" category):

```python
# Vulnerable code (Flask app)
@app.route('/search')
def search():
    query = request.args.get('q')
    return f"<h1>Results for: {query}</h1>"  # XSS vulnerability

# Model must:
# 1. Identify XSS vulnerability
# 2. Explain: User input reflected without sanitization
# 3. Propose fix:
from markupsafe import escape
@app.route('/search')
def search():
    query = escape(request.args.get('q'))
    return f"<h1>Results for: {query}</h1>"
```

### Adoption by Government Safety Institutes

**US AISI and UK AISI**: Leveraged Cybench as the **only open-source cybersecurity benchmark** on Joint Pre-Deployment Tests

**Anthropic adoption**: Featured Cybench results in system cards for:
- Claude 3.7 Sonnet
- Claude 4
- Claude Opus 4.1
- Claude Sonnet 4.5
- Claude Haiku 4.5

This widespread adoption validates Cybench's credibility and relevance for safety-critical AI evaluation.

---

## Universal Principles for AIBaaS Benchmark Design

### 1. Multimodal Testing (from VideoMMMU)

**Insight**: VideoMMMU tests knowledge acquisition across **3 cognitive stages**:
1. **Perception**: Identifying key information
2. **Comprehension**: Understanding concepts
3. **Adaptation**: Applying knowledge to new scenarios

**AIBaaS application**:
- **Perception**: Extract requirements from issue description
- **Comprehension**: Understand codebase architecture
- **Adaptation**: Generate code that integrates with existing patterns

### 2. Long-Horizon Coherence (from VendingBench)

**Insight**: VendingBench reveals models struggle with **multi-day decision-making** and enter "doom loops" (e.g., misunderstanding order schedules, attempting illogical escalations like contacting FBI over routine fees).

**Key finding**: Even top models show **high variance** (Grok 4: $4,694 mean but wide range across 5 runs).

**AIBaaS application**:
- Test models on **multi-sprint development tasks** (e.g., "Build a feature over 3 sprints with changing requirements")
- Measure consistency across multiple runs (do they generate similar quality code each time?)
- Detect "doom loops" in code generation (e.g., infinite refactoring, over-engineering)

### 3. Simulation Environments (from BalrogAI, Cybench)

**Insight**: Both BalrogAI (game environments) and Cybench (Kali Linux container) use **interactive simulation environments** where agents receive observations and must take actions.

**AIBaaS application**:
- Provide models with **Docker container** containing codebase + tests
- Allow model to run commands (build, test, lint) and observe output
- Measure ability to debug failing tests by iterating on code + running tests

### 4. Human Baselines for Calibration (from VPCT, VideoMMMU)

**Insight**:
- **VPCT**: Humans achieve 100% on physics puzzles that models find challenging (39.8-71% accuracy)
- **VideoMMMU**: Humans achieve 33.1% Δknowledge vs. 11.4-15.6% for top models

**AIBaaS application**:
- Establish **human developer baselines** for code review tasks
- Calibrate model performance against junior/mid/senior developer levels
- Avoid tasks where humans struggle (ambiguous requirements) or find trivial (syntax errors)

---

## Patterns to Avoid

### 1. Over-Specialization (GeoBench, ForecastBench)

**Issue**: GeoBench (location identification) and ForecastBench (prediction markets) test highly domain-specific skills with **low transferability** to software development.

**Lesson for AIBaaS**: Avoid benchmarks testing knowledge unrelated to coding (e.g., "identify programming language from flag emoji" would be GeoBench-style over-specialization).

### 2. Brittleness Without Mitigation (VPCT)

**Issue**: VPCT exposes brittleness in grounded physical reasoning, but offers no path to improvement (humans are 100%, models are 39-71%, no clear training strategy).

**Lesson for AIBaaS**: If a benchmark reveals brittleness (e.g., multimodal code understanding), provide **subtask guidance** (like Cybench) to measure model's ability to improve with hints.

### 3. Unclear Human Baselines (BalrogAI)

**Issue**: BalrogAI doesn't specify human performance, making it hard to interpret whether 43.6% progress is good or bad.

**Lesson for AIBaaS**: Always include human baselines (junior/mid/senior developer) to contextualize model performance.

---

## Recommendations for AIBaaS Marketing

### Tier 1: Direct Reference (High Relevance)

**1. Cybench**
- **Marketing claim**: "Our AIBaaS benchmark includes security scanning inspired by Cybench, the cybersecurity CTF benchmark adopted by US AISI and UK AISI for AI safety testing."
- **Use case**: Validate models can detect vulnerabilities (XSS, SQL injection, crypto flaws) in code review
- **Credibility**: Government adoption + Anthropic system cards

**2. VideoMMMU** (methodology, not domain)
- **Marketing claim**: "We test knowledge acquisition across perception, comprehension, and adaptation stages, inspired by VideoMMMU's cognitive framework."
- **Use case**: Demonstrate AIBaaS tests understanding (not just memorization) of codebase patterns
- **Credibility**: ICCV 2025 publication

**3. VendingBench** (methodology, not domain)
- **Marketing claim**: "Our multi-sprint scenarios test long-horizon coherence, inspired by VendingBench's approach to measuring consistency over extended time horizons."
- **Use case**: Show AIBaaS detects "doom loops" and inconsistent decision-making over multi-day tasks
- **Credibility**: Reveals high variance in top models (Grok 4, GPT-5)

### Tier 2: Avoid (Low Relevance)

**1. GeoBench**
- **Why skip**: Location identification has zero software development application
- **Exception**: Could mention as example of multimodal visual reasoning, but domain is too unrelated

**2. ForecastBench**
- **Why skip**: Prediction markets too specialized, though sprint planning AI could be a stretch connection
- **Exception**: Mention "long-term prediction accuracy" if AIBaaS includes timeline estimation

**3. BalrogAI**
- **Why skip**: Videogame playing is too indirect a parallel to software development
- **Exception**: "Long-horizon planning" is relevant, but VendingBench is a better reference

**4. VPCT**
- **Why skip**: Physics puzzles have no coding application, and models perform poorly (39-71% vs. human 100%)
- **Exception**: None - avoid entirely

---

## Conclusion

### Key Takeaways

1. **Cybench is the only domain-specific benchmark with direct software development relevance** (9/10 score)
   - Actionable: Integrate security scanning scenarios into AIBaaS code review
   - Marketing: Reference government adoption (US AISI, UK AISI) for credibility

2. **VideoMMMU and VendingBench offer methodological insights** (not domain insights)
   - VideoMMMU: Test perception → comprehension → adaptation (cognitive stages)
   - VendingBench: Test long-horizon coherence and detect "doom loops"

3. **Universal principles**:
   - **Multimodal testing**: Combine code + documentation + issues
   - **Simulation environments**: Let models interact with Docker containers
   - **Human baselines**: Calibrate against junior/mid/senior developers
   - **Subtask guidance**: Measure improvement with hints (like Cybench)

4. **Patterns to avoid**:
   - Over-specialization (GeoBench, ForecastBench)
   - Brittleness without mitigation (VPCT)
   - Unclear human baselines (BalrogAI)

### Next Steps for AIBaaS

1. **Design security scanning gate** inspired by Cybench
   - Tasks: Detect XSS, SQL injection, crypto flaws, privilege escalation
   - Scoring: Detection accuracy + classification accuracy + fix quality
   - Human baseline: Security auditor performance on same vulnerabilities

2. **Adopt VideoMMMU's cognitive framework**
   - Perception: Extract requirements from issue
   - Comprehension: Understand codebase architecture
   - Adaptation: Generate code integrating with existing patterns

3. **Test long-horizon coherence** inspired by VendingBench
   - Multi-sprint scenarios (e.g., 3-day feature development)
   - Measure consistency across multiple runs (variance analysis)
   - Detect "doom loops" (infinite refactoring, over-engineering)

4. **Reference in marketing**:
   - Primary: Cybench (security scanning)
   - Secondary: VideoMMMU (knowledge acquisition methodology), VendingBench (long-horizon coherence)
   - Avoid: GeoBench, ForecastBench, BalrogAI, VPCT

---

## Appendix: Detailed Performance Data

### Cybench Performance by Model (2024-2025)

| Model | Unguided % | Subtask-Guided % | Subtasks % | Release Date |
|-------|-----------|-----------------|------------|--------------|
| Claude Sonnet 4.5 | 71.8% | - | - | Oct 2025 |
| Claude Sonnet 3.7 | 35.9% | - | - | Feb 2025 |
| Claude 3.5 Sonnet | 17.5% | - | - | Mid-2024 |
| GPT-4o | - | 17.5% | - | Mid-2024 |
| OpenAI o1-preview | - | - | 46.8% | Mid-2024 |

### ForecastBench Performance (October 2025)

| Forecaster Type | Difficulty-Adjusted Brier Score | Rank |
|----------------|-------------------------------|------|
| Superforecasters | 0.081 | #1 |
| GPT-4.5 | 0.101 | #2 (best LLM) |
| Median Public Forecast | - | #22 (was #2 in Oct 2024) |

**Improvement rate**: ~0.016 Brier points per year
**Projected LLM-superforecaster parity**: November 2026 (95% CI: Dec 2025 - Jan 2028)

### VideoMMMU Performance

| Model | Overall Accuracy | Δknowledge |
|-------|-----------------|-----------|
| GPT-5-thinking | 84.6% | - |
| Gemini 2.5 Pro | 83.6% | - |
| Claude 3.5 Sonnet | 65.78% | +11.4% |
| GPT-4o | 61.22% | +15.6% |
| **Human Experts** | - | **+33.1%** |

### BalrogAI Performance (% Progress)

**LLM Leaderboard**:
1. Grok-4: 43.6%
2. Gemini 2.5 Pro Exp: 43.3%
3. DeepSeek-R1: 34.9%

**VLM Leaderboard**:
1. Gemini 2.5 Pro Exp: 35.7%
2. Claude 3.5 Sonnet: 35.5%
3. Gemini 1.5 Pro 002: 25.8%

### VPCT Performance (100 problems)

| Model | Accuracy | Notes |
|-------|---------|-------|
| Humans | 100% | "Trivial for most people" |
| GPT-5 Pro High | 71% | - |
| Claude Sonnet 4.5 | 39.8-63% | Conflicting reports |
| Random guessing | 33.3% | (3 buckets) |

### VendingBench Performance (~325 days simulation)

| Model | Mean Net Worth | Units Sold (mean) | Notes |
|-------|---------------|------------------|-------|
| Grok 4 | $4,694 | 4,569 | High variance across runs |
| GPT-5 | $3,578 | 2,471 | - |
| Claude Opus 4 | $2,077 | 1,412 | - |
| Human (1 participant) | $844 | 344 | 67 days (100% completion) |
| Starting balance | $500 | - | Daily costs: $2 |

**Key finding**: Top models outperform single human participant, but show "doom loops" (e.g., misunderstanding order schedules, contacting FBI over routine fees).

---

**End of Analysis**

**Files Referenced**:
- Cybench: https://cybench.github.io/
- GeoBench: https://geobench.org/ (limited data available)
- ForecastBench: https://www.forecastbench.org/
- VideoMMMU: https://videommmu.github.io/
- BalrogAI: https://balrogai.com/
- VPCT: https://cbrower.dev/vpct (limited data available)
- VendingBench: https://andonlabs.com/evals/vending-bench

**Research Date**: November 1, 2025
**Next Steps**: Integrate Cybench-inspired security scanning into AIBaaS code review scenario
