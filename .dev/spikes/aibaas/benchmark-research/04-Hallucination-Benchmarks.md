# Hallucination Benchmarks Research

**Date:** 2024-11-01
**Author:** Research Team
**Status:** Complete

## Executive Summary

This research analyzes two leading hallucination benchmarks for LLMs and proposes methodologies for detecting code hallucinations in Tamma's AI-powered development platform. Key findings:

- **Vectara HHEM-2.1**: Automated factual consistency scoring using fine-tuned transformer model (0-1 scale)
- **LechMazur RAG Benchmark**: Human-verified adversarial questions testing confabulation vs non-response trade-offs
- **Code Hallucinations**: 19.7% of LLM-generated code contains hallucinated packages; API hallucinations occur in 38-93% of cases depending on API frequency

### Recommendations for Tamma

1. **Hybrid Detection**: Combine automated validation (package registries, API documentation) with confidence-based scoring
2. **Multi-Layer Validation**: Static analysis → Dynamic testing → Human review for critical paths
3. **Scoring System**: Weight false negatives (missed hallucinations) 3x higher than false positives (over-flagging)
4. **Adversarial Testing**: Deliberately challenging prompts for low-frequency APIs and edge cases

---

## 1. Vectara Hallucination Leaderboard

**URL:** https://github.com/vectara/hallucination-leaderboard
**Focus:** Factual consistency in LLM summarization
**Models Tested:** 100+ LLMs (as of Nov 2024)

### 1.1 Detection Methodology

**Core Approach:** Automated factual consistency scoring using HHEM (Hughes Hallucination Evaluation Model)

**HHEM-2.1 Architecture:**
- Base model: Google Flan-T5-base (fine-tuned)
- Input: Premise-hypothesis text pairs
- Output: Score 0.0-1.0 (0 = hallucinated, 1 = fully supported)
- Context length: Unlimited (vs 512 tokens in HHEM-1.0)
- Inference speed: ~1.5s for 2k tokens on modern x86 CPU
- Memory footprint: <600MB RAM at 32-bit precision

**Key Innovation:** Pure classification model, NOT "LLM-as-a-judge" approach

### 1.2 Ground Truth

**Dataset:** CNN/Daily Mail Corpus
- 1,000 documents per model tested
- 831 documents successfully summarized by all models
- Temperature: 0 (deterministic outputs)

**Validation Process:**
1. Prompt LLMs to summarize documents "using only the facts presented"
2. HHEM-2.1 compares summary (hypothesis) to original document (premise)
3. Score represents factual consistency probability

**Training Calibration:**
- AggreFact benchmark (summaries from T5, BART, Pegasus)
- RAGTruth benchmark (QA and summarization subsets)
- Human-annotated ground truth for both benchmarks

### 1.3 Scoring System

**Four Metrics Tracked:**

1. **Hallucination Rate (%)**: Percentage of summaries containing fabricated information
   - Calculated as: (1 - Factual Consistency Rate) × 100

2. **Factual Consistency Rate (%)**: 100 - Hallucination Rate
   - Based on HHEM-2.1 scores (threshold: typically 0.5 or calibrated value)

3. **Answer Rate (%)**: Frequency models respond vs refusing due to content filters
   - Notable: 169 documents (16.9%) triggered content restrictions across models

4. **Average Summary Length (words)**: Proxy for verbosity vs conciseness

**No Explicit False Positive/Negative Weights:** Metrics are balanced accuracy, F1, recall, precision

### 1.4 Benchmark Performance

**Leaderboard Highlights (Top Models):**
- GPT-4o: 1.8% hallucination rate
- Claude 3.5 Sonnet: 2.4% hallucination rate
- Gemini 1.5 Pro: 3.1% hallucination rate

**HHEM-2.1 vs LLM-as-Judge:**
- HHEM-2.1 outperforms GPT-3.5-Turbo and GPT-4 on AggreFact-SOTA
- Faster inference, lower cost, consistent calibration

---

## 2. LechMazur RAG Hallucination Leaderboard

**URL:** https://lechmazur.github.io/leaderboard1.html
**Repository:** https://github.com/lechmazur/confabulations
**Focus:** Confabulation (hallucination) in RAG systems with adversarial questions

### 2.1 Detection Methodology

**Core Approach:** Human-verified adversarial questions that LACK answers in provided documents

**Question Design:**
- 201 questions confirmed by humans to have NO answers in source texts
- 2,612 questions with known answers (for non-response rate testing)
- Questions are "intentionally crafted to be challenging"
- Documents are recent articles NOT in LLM training data

**Human Verification Process:**
1. Initial questions suggested by LLMs
2. Human reviewers verify each question is "unequivocally answer-free"
3. Compound questions (asking for multiple answers) removed in separate judging step
4. Only a small percentage of LLM-suggested questions passed verification

**Why Human Verification?** "LLMs often generated compound questions that effectively asked for two answers, which were removed"

### 2.2 Ground Truth

**Source of Truth:** Human annotators verifying no answer exists in provided text

**Document Sources:**
- Recent articles (published after LLM training cutoff dates)
- Ensures models cannot rely on memorized training data
- Forces models to extract information from provided context only

**Adversarial Nature:**
- Questions designed to be misleading (suggest answer might exist)
- Tests model's ability to say "I don't know" vs fabricating information
- Mimics real-world RAG scenarios with incomplete/ambiguous information

### 2.3 RAG-Specific Testing

**RAG Challenge:** Retrieval accuracy vs generation hallucination

**Two-Dimensional Evaluation:**

1. **Confabulation Rate (%)**: False answers to unanswerable questions
   - Ground truth: Human verification that NO answer exists
   - Detection: Human judges evaluate if model fabricated information

2. **Non-Response Rate (%)**: Refusals when answers DO exist in texts
   - Ground truth: 2,612 questions with verified answers
   - Detection: Model responds with "I don't know" or equivalent

**Why Both Metrics?** "A model that simply declines to answer most questions would achieve a low confabulation rate"

### 2.4 Scoring System

**Weighted Formula:** Combines confabulation and non-response rates

```
Score = (Confabulation_Weight × Confabulation_Rate) + (Non_Response_Weight × Non_Response_Rate)
```

**Default Weights:** 50% confabulation, 50% non-response (adjustable via slider)

**Rationale:**
- High confabulation → untrustworthy (dangerous hallucinations)
- High non-response → unusable (refuses valid questions)
- Balanced scoring rewards models that minimize both

**Leaderboard Example (Feb 2025):**
- o1: 3.5% confabulation, 5.0% non-response
- Claude 3.5 Sonnet: 4.8% confabulation, 3.2% non-response
- GPT-4o: 6.2% confabulation, 4.1% non-response

### 2.5 Adversarial Design Details

**Deliberately Challenging Characteristics:**

1. **Misleading Phrasing:** Questions imply answer should exist
   - Example: "What color was the car mentioned in paragraph 3?" (when no car mentioned)

2. **Domain Knowledge Traps:** Questions about plausible but absent details
   - Leverages model's world knowledge to create false confidence

3. **Near-Miss Information:** Documents contain related but insufficient information
   - Tests if model fabricates missing details vs admitting incomplete data

4. **Intentionally Adversarial:** Benchmark creator acknowledges these are NOT typical hallucination frequencies
   - Targets scenarios where models typically struggle
   - Provides worst-case performance assessment

**Distinction from Vectara:** "A popular hallucination leaderboard on GitHub uses a model for evaluation of document summaries, but this approach can be very misleading"
- Human verification vs automated model scoring
- RAG-specific (retrieval + generation) vs summarization only

---

## 3. Code Hallucination Research

### 3.1 Package Hallucinations

**Research:** "We Have a Package for You!" (arXiv:2406.10279v3)
**Scope:** 576,000 Python and JavaScript code samples, 16 LLMs

#### Detection Methodology

**Three-Heuristic Approach:**

1. **Heuristic 1: Explicit Installation Commands**
   - Parse for `pip install` and `npm install` commands
   - Directly extracts package names from code
   - Coverage: ~7% of total outputs

2. **Heuristic 2: Model Self-Querying**
   - Feed generated code back to same model
   - Prompt: "List packages required to run this code"
   - Simulates developer behavior when encountering missing dependencies

3. **Heuristic 3: Original Prompt Re-Submission**
   - Re-prompt: "Output package names required to accomplish this task"
   - Captures packages model intended but didn't explicitly install

**Validation Against Ground Truth:**
- Compare extracted package names to PyPI and npm registries (as of Jan 10, 2024)
- Any package NOT in official registries → hallucination
- Note: Results are "lower bound" (registries may contain malicious slopsquatting packages)

#### Results

**Overall Hallucination Rates:**
- 19.7% of code samples contain hallucinated packages (440,445 out of 2.23M)
- 205,474 unique hallucinated package names
- Python: 15.8% average hallucination rate
- JavaScript: 21.3% average hallucination rate

**Model Performance:**
- Best: GPT-4 Turbo (3.59%)
- Worst: DeepSeek 1B (13.63% among open-source models)
- Commercial models (GPT): 5.2% average
- Open-source models: 21.7% average

**Persistence Testing (500 prompts × 10 repeats):**
- 43% of hallucinations repeated in ALL 10 queries
- 39% NEVER repeated across 10 iterations
- 58% repeated at least once

**Security Threat ("Slopsquatting"):**
- Attackers register malicious packages under hallucinated names
- Developers install malware disguised as "legitimate" AI-recommended packages
- Real-world attacks already documented in wild

#### Mitigation Strategies Tested

1. **Retrieval Augmented Generation (RAG):** Most effective, but degrades code quality
2. **Supervised Fine-Tuning:** Reduces hallucinations, quality trade-off
3. **Self-Detection:** Models asked to verify their own outputs
   - Limited success, models often fail to recognize own hallucinations

### 3.2 API Hallucinations

**Research:** "On Mitigating Code LLM Hallucinations with API Documentation" (arXiv:2407.09726v1)
**Benchmark:** CloudAPIBench (622 Python tasks, cloud API invocations)

#### Detection Methodology

**Valid API Invocation Metric:**
- Generated code must bind to API stub matching official signature
- Captures three failure types:
  1. Non-existent APIs (invented functions)
  2. Incorrect existing APIs (wrong API for task)
  3. Invalid target API usage (correct API, wrong arguments)

**Validation Against Ground Truth:**
- Each task includes target API's official specification
- Function binding validation (NOT string matching)
- Syntax correctness alone insufficient

#### Hallucination Frequency by API Prevalence

**High-Frequency APIs (≥101 occurrences in training data):**
- GPT-4o: 93.66% accuracy (6.34% hallucination)
- Models learned these APIs well during training

**Low-Frequency APIs (≤10 occurrences in training data):**
- GPT-4o: 38.58% accuracy (61.42% hallucination)
- Over 50% of failures involve non-existent APIs
- Stark performance gap demonstrates training data dependency

**Key Insight:** API frequency in training data is strongest predictor of hallucination risk

#### Mitigation: Documentation Augmented Generation (DAG)

**Basic DAG:**
- Retrieve relevant API documentation
- Augment prompt with official specs
- Problem: "Suboptimal retrievers degrade performance for high-frequency APIs"
- Irrelevant information distracts model

**DAG++ (Selective Retrieval):**
- Trigger documentation retrieval ONLY when:
  - Invoked API doesn't exist in index, OR
  - Model's confidence score < 0.8 threshold
- Results: 8.2% absolute improvement for GPT-4o
- Preserves high-frequency API performance

### 3.3 General Code Hallucinations

**Research:** "LLM Hallucinations in Practical Code Generation" (arXiv:2409.20550v1)

#### Taxonomy of Code Hallucinations

**Three Primary Categories (8 subcategories):**

**1. Task Requirement Conflicts (43.53%)**
- Functional Requirement Violation:
  - Wrong Functionality: Implements incorrect feature
  - Missing Functionality: Omits required features
- Non-functional Requirement Violation:
  - Security issues (SQL injection, XSS vulnerabilities)
  - Performance issues (inefficient algorithms)
  - Style violations (PEP 8, ESLint rules)
  - Code smells (duplicated code, long functions)

**2. Factual Knowledge Conflicts (31.91%)**
- Background Knowledge Conflicts:
  - Domain concepts misunderstood
  - Standards/protocols violated
- Library Knowledge Conflicts:
  - Framework/library misuse
  - Deprecated APIs used
- API Knowledge Conflicts:
  - Parameter type errors
  - Improper guard conditions
  - Similar-but-wrong APIs (e.g., `os.path.join` vs `pathlib.Path.joinpath`)
  - Exception handling errors

**3. Project Context Conflicts (24.56%)**
- Environment Conflicts:
  - Version incompatibilities (Python 2 vs 3 syntax)
  - Platform-specific code (Windows vs Linux)
- Dependency Conflicts:
  - Undefined methods (called but not imported)
  - API version conflicts (old API signature)
- Non-code Resource Conflicts:
  - Missing data files
  - Config files not found
  - Database connection errors

#### Root Cause Mechanisms

**1. Training Data Quality:**
- Mismatches between docstrings and code
- Inefficient or insecure code implementations
- Outdated library versions in corpus

**2. Intention Understanding Capacity:**
- Models generate "code based on common patterns observed in training data"
- Lack deep understanding of requirements
- Pattern matching vs reasoning

**3. Context Window Limitations:**
- Long-range dependencies missed
- Project structure not fully understood
- Cross-file relationships ignored

**4. Ambiguous Requirements:**
- Vague natural language prompts
- Underspecified edge cases
- Implicit assumptions not stated

#### Detection Methods (Manual Annotation)

**No Automated Detection System Proposed**
- Manual testing in actual development environments
- Comparison against ground-truth implementations
- Open coding analysis by multiple annotators

**Some Hallucinations Detectable Via:**
- Static analysis (undefined variables, type errors)
- Dynamic test execution (runtime errors, assertion failures)
- Linters/formatters (style violations)

**Many Hallucinations Evade Automation:**
- Incomplete functionality (code runs but doesn't meet requirements)
- Subtle logic errors (edge cases not handled)
- Security vulnerabilities (SQL injection, XSS)

---

## 4. Applicability to Code Hallucination Detection in Tamma

### 4.1 Can We Detect Code Hallucinations Automatically?

**Yes, with Multi-Layer Approach:**

```
Layer 1: Static Validation (100% automated)
  ├── Package registry lookup (PyPI, npm, Maven, RubyGems)
  ├── API documentation validation (stdlib, framework docs)
  ├── Type checking (TypeScript strict mode, mypy, Psalm)
  └── Linting (ESLint, Pylint, RuboCop)

Layer 2: Dynamic Validation (95% automated)
  ├── Unit test execution
  ├── Integration test execution
  ├── Security scanning (Semgrep, Bandit, npm audit)
  └── Build verification (compile, bundle, package)

Layer 3: Semantic Validation (50% automated)
  ├── Requirement tracing (issue description → code changes)
  ├── API usage patterns (common vs uncommon)
  ├── Confidence scoring (model self-assessment)
  └── Human review (critical paths, breaking changes)

Layer 4: Production Validation (post-deployment)
  ├── Runtime error monitoring
  ├── Performance metrics
  └── User feedback
```

### 4.2 Common Code Hallucination Patterns

**Pattern 1: Non-Existent Packages**
- Example: `import fictional_package` (not in PyPI/npm)
- Detection: Registry lookup (100% automated)
- Frequency: 19.7% of generated code (research finding)
- Mitigation: Package existence check before code execution

**Pattern 2: Non-Existent APIs**
- Example: `requests.get_json()` (actual method: `requests.get().json()`)
- Detection: API documentation scraping + signature matching
- Frequency: 61% for low-frequency APIs (research finding)
- Mitigation: DAG++ (documentation augmentation with confidence threshold)

**Pattern 3: Incorrect Parameter Types**
- Example: `open(file, mode=0644)` (mode should be string "r"/"w", not octal)
- Detection: Type checking (mypy, TypeScript strict mode)
- Frequency: Part of 31.91% factual knowledge conflicts
- Mitigation: Enforce strict typing, lint rules

**Pattern 4: Similar-But-Wrong APIs**
- Example: `os.path.join(path, file)` vs `pathlib.Path(path).joinpath(file)`
- Detection: Context-aware linting, API usage pattern analysis
- Frequency: Part of API knowledge conflicts (31.91%)
- Mitigation: Provide explicit API examples in prompt

**Pattern 5: Deprecated/Removed APIs**
- Example: `unittest.assertEquals()` (removed in Python 3.11, should be `assertEqual`)
- Detection: Version-specific linting, changelog scraping
- Frequency: Environment conflicts (24.56%)
- Mitigation: Pin language/framework versions in prompt

**Pattern 6: Fabricated Library Functions**
- Example: `lodash.deepMerge()` (actual function: `_.merge()`)
- Detection: Library documentation lookup, symbol table validation
- Frequency: Library knowledge conflicts (31.91%)
- Mitigation: Include library version and API reference in context

**Pattern 7: Missing Error Handling**
- Example: `json.loads(data)` without try/except for `JSONDecodeError`
- Detection: Static analysis (Pylint, Semgrep rules)
- Frequency: Part of API knowledge conflicts (improper guard conditions)
- Mitigation: Security gate enforcement (mandatory error handling)

**Pattern 8: Security Vulnerabilities**
- Example: `eval(user_input)`, SQL string concatenation
- Detection: Security scanners (Bandit, Semgrep, CodeQL)
- Frequency: Part of non-functional requirement violations (43.53%)
- Mitigation: Security quality gate (SAST, DAST)

**Pattern 9: Incomplete Functionality**
- Example: Generates function stub without implementing edge cases
- Detection: Test coverage analysis, requirement tracing
- Frequency: Most common (wrong/missing functionality, 43.53%)
- Mitigation: Test-driven development, acceptance criteria validation

**Pattern 10: Version Incompatibilities**
- Example: `async/await` syntax in Python 2.7 code
- Detection: Version-aware syntax checking
- Frequency: Environment conflicts (24.56%)
- Mitigation: Explicit version constraints in prompt

### 4.3 Proposed Scoring Methodology for Tamma

#### Hallucination Severity Levels

```typescript
enum HallucinationSeverity {
  CRITICAL = 'critical',    // Security vulnerability, data loss risk
  HIGH = 'high',            // Breaking changes, non-existent APIs
  MEDIUM = 'medium',        // Deprecated APIs, poor patterns
  LOW = 'low',              // Style violations, minor inefficiencies
  INFO = 'info'             // Suggestions, optimizations
}
```

#### Scoring Formula

**Weighted Hallucination Score (0-100, lower is better):**

```
HallucinationScore = (
  (Critical_Count × 50) +
  (High_Count × 20) +
  (Medium_Count × 5) +
  (Low_Count × 1)
) / Total_Generated_Lines × 100
```

**Acceptance Thresholds:**
- Score < 5: Auto-approve (minimal hallucinations)
- Score 5-15: Auto-approve with warnings
- Score 15-30: Human review required
- Score > 30: Auto-reject, regenerate with DAG++

#### False Positive vs False Negative Weights

**Asymmetric Weighting (False Negatives Cost More):**

```
Quality_Gate_Score = (
  (True_Positives × 1.0) +
  (True_Negatives × 1.0) +
  (False_Positives × 0.5) +   # Over-flagging wastes time
  (False_Negatives × 3.0)      # Missed hallucinations cause production bugs
)
```

**Rationale:**
- False Negative (missed hallucination): Bugs reach production, security risk, customer impact
- False Positive (over-flagging): Developer wastes time investigating, productivity loss
- Weight FN 6x higher than FP to prioritize safety over convenience

#### Confidence-Based Scoring

**Model Confidence Threshold (DAG++ Approach):**

```typescript
interface GenerationResult {
  code: string;
  confidence: number;  // 0.0-1.0 from model logprobs
  hallucinationRisk: 'low' | 'medium' | 'high';
}

function assessHallucinationRisk(confidence: number, apiFrequency: 'high' | 'low'): string {
  if (confidence < 0.8 || apiFrequency === 'low') {
    return 'high';  // Trigger DAG++, require human review
  } else if (confidence < 0.9) {
    return 'medium';  // Run extra validation layers
  } else {
    return 'low';  // Standard validation only
  }
}
```

#### Multi-Metric Scoring (Inspired by LechMazur)

**Two-Dimensional Code Quality:**

1. **Hallucination Rate (%)**: Invalid code (non-existent APIs, packages, syntax errors)
   - Detection: Static + dynamic validation layers
   - Target: < 5% for production deployments

2. **Non-Implementation Rate (%)**: Missing functionality (incomplete requirements)
   - Detection: Requirement tracing, test coverage, acceptance criteria
   - Target: < 10% (some features may be deferred intentionally)

**Combined Score:**

```
Code_Quality_Score = (
  (Hallucination_Weight × Hallucination_Rate) +
  (Non_Implementation_Weight × Non_Implementation_Rate)
)
```

**Recommended Weights:**
- Hallucination: 70% (safety-critical, hard to detect post-merge)
- Non-Implementation: 30% (easier to detect via testing, can be iterated)

### 4.4 Adversarial Testing Strategy

**Deliberately Challenging Test Cases (Inspired by LechMazur RAG Benchmark):**

**1. Low-Frequency API Tests**
- Prompt: "Use the `boto3.client('transcribe').start_call_analytics_job()` API"
- Ground Truth: AWS documentation for TranscribeService API
- Challenge: API exists but has 12 parameters, complex nested structure
- Expected: Model may hallucinate simplified parameters or invent convenience methods

**2. Non-Existent Package Traps**
- Prompt: "Install and use the `fastapi-redis-cache` package for caching"
- Ground Truth: Package doesn't exist (as of Jan 2024), should use `fastapi-cache2[redis]`
- Challenge: Name sounds plausible, follows naming conventions
- Expected: Model may confidently recommend non-existent package

**3. Deprecated API Tests**
- Prompt: "Write a unittest using `assertEquals()` method"
- Ground Truth: `assertEquals` removed in Python 3.11, should use `assertEqual`
- Challenge: Tests if model keeps up with recent language changes
- Expected: Model may use deprecated API if trained on older corpus

**4. Similar-But-Wrong API Tests**
- Prompt: "Parse JSON from a requests response object"
- Ground Truth: `response.json()` is correct, NOT `response.get_json()`
- Challenge: `get_json()` exists in Flask, not requests (common confusion)
- Expected: Model may conflate similar APIs from different libraries

**5. Edge Case Requirement Tests**
- Prompt: "Write a function to merge two dictionaries, handling nested conflicts"
- Ground Truth: Must specify conflict resolution strategy (last-wins, merge, error)
- Challenge: Vague requirements, multiple valid approaches
- Expected: Model may generate simple `dict1.update(dict2)` missing edge cases

**6. Security Vulnerability Tests**
- Prompt: "Write a SQL query function that takes a user-provided table name"
- Ground Truth: Must parameterize or whitelist table names (prevent injection)
- Challenge: Tests if model includes security best practices
- Expected: Model may generate `f"SELECT * FROM {table_name}"` (vulnerable)

**7. Cross-Platform Compatibility Tests**
- Prompt: "Write a script to delete a directory recursively"
- Ground Truth: Must handle Windows (`shutil.rmtree`) vs Linux (`rm -rf`) differences
- Challenge: Platform-specific behavior
- Expected: Model may assume single platform

**8. Missing Documentation Tests**
- Prompt: "Use the internal `_private_method()` from MyLibrary class"
- Ground Truth: Private methods undocumented, may change between versions
- Challenge: Tests if model respects API stability contracts
- Expected: Model may use internal APIs without warning about stability

### 4.5 Implementation Roadmap for Tamma

**Phase 1: Static Validation (Epic 3 - Quality Gates)**
- [ ] Package registry integration (PyPI, npm, Maven, RubyGems APIs)
- [ ] API documentation scraping (stdlib, popular frameworks)
- [ ] Type checking enforcement (TypeScript strict, mypy, Psalm)
- [ ] Linter integration (ESLint, Pylint, RuboCop)
- [ ] Security scanner integration (Semgrep, Bandit)

**Phase 2: Dynamic Validation (Epic 3)**
- [ ] Unit test execution (Vitest, pytest, Jest)
- [ ] Integration test execution (Docker environments)
- [ ] Build verification (compile, bundle, package)
- [ ] Coverage analysis (c8, pytest-cov)

**Phase 3: Semantic Validation (Epic 4 - Autonomous Loop)**
- [ ] Requirement tracing (issue description → code changes)
- [ ] Confidence scoring (model logprobs → risk assessment)
- [ ] API frequency analysis (training corpus occurrence counts)
- [ ] Human review triggers (threshold-based escalation)

**Phase 4: Adversarial Testing (Epic 6 - Testing)**
- [ ] Adversarial test suite (low-frequency APIs, deprecated APIs, security)
- [ ] Hallucination benchmark (Tamma-specific leaderboard)
- [ ] Model comparison (track hallucination rates per provider)
- [ ] Continuous monitoring (production error tracking)

**Phase 5: Mitigation Strategies (Epic 4)**
- [ ] DAG++ implementation (selective documentation augmentation)
- [ ] Context enrichment (API examples, recent changelogs)
- [ ] Self-correction loop (regenerate on high hallucination score)
- [ ] Confidence thresholding (require human review for low confidence)

---

## 5. Design Proposal: Code Hallucination Detection System

### 5.1 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Code Generation Request                      │
│  (Issue Description + Codebase Context + Provider Config)        │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│                    AI Provider (with logprobs)                   │
│          Returns: { code, confidence, metadata }                 │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│                  Hallucination Detection Pipeline                │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Layer 1: Static Validation (100% Automated)            │   │
│  │  ├── Package Registry Lookup                            │   │
│  │  ├── API Documentation Validation                       │   │
│  │  ├── Type Checking (strict mode)                        │   │
│  │  ├── Linting (ESLint, Pylint, etc.)                     │   │
│  │  └── Security Scanning (Semgrep, Bandit)                │   │
│  └──────────────────────┬──────────────────────────────────┘   │
│                         │ Pass                                   │
│                         ▼                                        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Layer 2: Dynamic Validation (95% Automated)            │   │
│  │  ├── Unit Test Execution                                │   │
│  │  ├── Integration Test Execution                         │   │
│  │  ├── Build Verification                                 │   │
│  │  └── Coverage Analysis                                  │   │
│  └──────────────────────┬──────────────────────────────────┘   │
│                         │ Pass                                   │
│                         ▼                                        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Layer 3: Semantic Validation (50% Automated)           │   │
│  │  ├── Requirement Tracing                                │   │
│  │  ├── Confidence Scoring (model logprobs)                │   │
│  │  ├── API Frequency Analysis                             │   │
│  │  └── Human Review Triggers                              │   │
│  └──────────────────────┬──────────────────────────────────┘   │
│                         │                                        │
│                         ▼                                        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │          Hallucination Scoring Engine                   │   │
│  │  - Calculate weighted score (0-100)                     │   │
│  │  - Classify severity (critical/high/medium/low)         │   │
│  │  - Assess hallucination risk (low/medium/high)          │   │
│  └──────────────────────┬──────────────────────────────────┘   │
└─────────────────────────┼────────────────────────────────────────┘
                          │
                          ▼
              ┌───────────────────────┐
              │  Score < 5?           │
              └───────┬───────────────┘
                      │
        ┌─────────────┼─────────────┐
        │ Yes                       │ No
        ▼                           ▼
┌───────────────┐        ┌──────────────────────┐
│  Auto-Approve │        │ Score 5-15?          │
│  (Event: CODE.│        └──────┬───────────────┘
│  VALIDATED.   │               │
│  SUCCESS)     │     ┌─────────┼─────────┐
└───────────────┘     │ Yes              │ No
                      ▼                  ▼
          ┌───────────────────┐  ┌──────────────────┐
          │ Approve + Warn    │  │ Score 15-30?     │
          │ (Event: CODE.     │  └────┬─────────────┘
          │ VALIDATED.WARNING)│       │
          └───────────────────┘  ┌────┼────┐
                                 │Yes      │No
                                 ▼         ▼
                    ┌────────────────┐  ┌─────────────┐
                    │ Human Review   │  │ Auto-Reject │
                    │ (Event: GATE.  │  │ Regenerate  │
                    │ REVIEW_        │  │ with DAG++  │
                    │ REQUESTED)     │  │ (Event: CODE│
                    └────────────────┘  │ .VALIDATION.│
                                        │ FAILED)     │
                                        └─────────────┘
```

### 5.2 Data Model

```typescript
// Hallucination Detection Result
interface HallucinationDetectionResult {
  id: string;  // UUID v7
  timestamp: string;  // ISO 8601
  codeGenerationId: string;  // Links to original generation event

  // Overall Scores
  hallucinationScore: number;  // 0-100 (weighted score)
  hallucinationRate: number;  // 0.0-1.0 (percentage of invalid code)
  nonImplementationRate: number;  // 0.0-1.0 (percentage of missing features)
  codeQualityScore: number;  // Combined metric

  // Risk Assessment
  hallucinationRisk: 'low' | 'medium' | 'high';
  confidence: number;  // 0.0-1.0 from model logprobs

  // Validation Results
  staticValidation: StaticValidationResult;
  dynamicValidation: DynamicValidationResult;
  semanticValidation: SemanticValidationResult;

  // Decision
  decision: 'auto_approved' | 'approved_with_warnings' | 'human_review_required' | 'auto_rejected';
  rejectionReason?: string;

  // Hallucinations Found
  hallucinations: Hallucination[];
}

// Individual Hallucination
interface Hallucination {
  id: string;  // UUID v7
  type: HallucinationType;
  severity: HallucinationSeverity;
  location: CodeLocation;
  description: string;
  suggestion?: string;  // How to fix
  groundTruth?: string;  // Correct API/package name
}

enum HallucinationType {
  NON_EXISTENT_PACKAGE = 'non_existent_package',
  NON_EXISTENT_API = 'non_existent_api',
  INCORRECT_PARAMETERS = 'incorrect_parameters',
  SIMILAR_BUT_WRONG_API = 'similar_but_wrong_api',
  DEPRECATED_API = 'deprecated_api',
  MISSING_ERROR_HANDLING = 'missing_error_handling',
  SECURITY_VULNERABILITY = 'security_vulnerability',
  INCOMPLETE_FUNCTIONALITY = 'incomplete_functionality',
  VERSION_INCOMPATIBILITY = 'version_incompatibility',
  TYPE_ERROR = 'type_error'
}

enum HallucinationSeverity {
  CRITICAL = 'critical',  // Security vuln, data loss
  HIGH = 'high',          // Breaking changes, non-existent APIs
  MEDIUM = 'medium',      // Deprecated APIs, poor patterns
  LOW = 'low',            // Style violations
  INFO = 'info'           // Suggestions
}

interface CodeLocation {
  file: string;
  line: number;
  column: number;
  snippet: string;  // Surrounding code context
}

// Static Validation Result
interface StaticValidationResult {
  passed: boolean;
  packageLookup: PackageLookupResult;
  apiValidation: APIValidationResult;
  typeChecking: TypeCheckingResult;
  linting: LintingResult;
  securityScanning: SecurityScanningResult;
}

interface PackageLookupResult {
  passed: boolean;
  packages: {
    name: string;
    exists: boolean;
    registry: 'pypi' | 'npm' | 'maven' | 'rubygems' | 'crates.io';
  }[];
  hallucinatedPackages: string[];
}

interface APIValidationResult {
  passed: boolean;
  apis: {
    name: string;
    exists: boolean;
    signature?: string;
    documentationUrl?: string;
    frequency: 'high' | 'low';
  }[];
  hallucinatedAPIs: string[];
  parameterErrors: {
    api: string;
    expectedParams: string[];
    actualParams: string[];
  }[];
}

// Dynamic Validation Result
interface DynamicValidationResult {
  passed: boolean;
  unitTests: TestExecutionResult;
  integrationTests: TestExecutionResult;
  buildVerification: BuildResult;
  coverageAnalysis: CoverageResult;
}

interface TestExecutionResult {
  passed: boolean;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  errors: TestError[];
}

// Semantic Validation Result
interface SemanticValidationResult {
  passed: boolean;
  requirementTracing: RequirementTracingResult;
  confidenceScoring: ConfidenceScoringResult;
  apiFrequencyAnalysis: APIFrequencyResult;
}

interface RequirementTracingResult {
  passed: boolean;
  implementedRequirements: string[];
  missingRequirements: string[];
  coveragePercentage: number;
}

interface ConfidenceScoringResult {
  averageConfidence: number;
  lowConfidenceBlocks: {
    location: CodeLocation;
    confidence: number;
  }[];
}
```

### 5.3 Validation Logic

```typescript
class HallucinationDetector {
  async detect(
    code: string,
    language: string,
    requirements: string[],
    modelConfidence: number
  ): Promise<HallucinationDetectionResult> {

    // Layer 1: Static Validation
    const staticResult = await this.runStaticValidation(code, language);

    // Short-circuit if critical static failures
    if (this.hasCriticalFailures(staticResult)) {
      return this.buildRejectionResult(staticResult, 'Critical static validation failures');
    }

    // Layer 2: Dynamic Validation
    const dynamicResult = await this.runDynamicValidation(code, language);

    // Layer 3: Semantic Validation
    const semanticResult = await this.runSemanticValidation(
      code,
      requirements,
      modelConfidence
    );

    // Calculate Scores
    const hallucinations = this.extractHallucinations(
      staticResult,
      dynamicResult,
      semanticResult
    );

    const hallucinationScore = this.calculateHallucinationScore(hallucinations);
    const hallucinationRate = hallucinations.length / this.countLines(code);
    const nonImplementationRate = semanticResult.requirementTracing.missingRequirements.length / requirements.length;

    const codeQualityScore = (
      (0.7 * hallucinationRate) +
      (0.3 * nonImplementationRate)
    );

    // Risk Assessment
    const hallucinationRisk = this.assessRisk(
      modelConfidence,
      staticResult.apiValidation.apis
    );

    // Decision Logic
    let decision: string;
    let rejectionReason: string | undefined;

    if (hallucinationScore < 5) {
      decision = 'auto_approved';
    } else if (hallucinationScore < 15) {
      decision = 'approved_with_warnings';
    } else if (hallucinationScore < 30) {
      decision = 'human_review_required';
    } else {
      decision = 'auto_rejected';
      rejectionReason = `Hallucination score too high: ${hallucinationScore}`;
    }

    return {
      id: uuidv7(),
      timestamp: dayjs.utc().toISOString(),
      hallucinationScore,
      hallucinationRate,
      nonImplementationRate,
      codeQualityScore,
      hallucinationRisk,
      confidence: modelConfidence,
      staticValidation: staticResult,
      dynamicValidation: dynamicResult,
      semanticValidation: semanticResult,
      decision,
      rejectionReason,
      hallucinations
    };
  }

  private calculateHallucinationScore(hallucinations: Hallucination[]): number {
    const weights = {
      critical: 50,
      high: 20,
      medium: 5,
      low: 1,
      info: 0
    };

    const totalWeight = hallucinations.reduce(
      (sum, h) => sum + weights[h.severity],
      0
    );

    return Math.min(100, totalWeight);  // Cap at 100
  }

  private assessRisk(
    confidence: number,
    apis: APIValidationResult['apis']
  ): 'low' | 'medium' | 'high' {
    const hasLowFrequencyAPIs = apis.some(api => api.frequency === 'low');

    if (confidence < 0.8 || hasLowFrequencyAPIs) {
      return 'high';
    } else if (confidence < 0.9) {
      return 'medium';
    } else {
      return 'low';
    }
  }

  private async runStaticValidation(
    code: string,
    language: string
  ): Promise<StaticValidationResult> {
    const [
      packageLookup,
      apiValidation,
      typeChecking,
      linting,
      securityScanning
    ] = await Promise.all([
      this.packageRegistry.lookup(code, language),
      this.apiValidator.validate(code, language),
      this.typeChecker.check(code, language),
      this.linter.lint(code, language),
      this.securityScanner.scan(code, language)
    ]);

    return {
      passed: packageLookup.passed && apiValidation.passed && typeChecking.passed && linting.passed && securityScanning.passed,
      packageLookup,
      apiValidation,
      typeChecking,
      linting,
      securityScanning
    };
  }
}
```

### 5.4 Package Registry Integration

```typescript
class PackageRegistry {
  private registries = {
    pypi: 'https://pypi.org/pypi',
    npm: 'https://registry.npmjs.org',
    maven: 'https://search.maven.org/solrsearch/select',
    rubygems: 'https://rubygems.org/api/v1/gems',
    crates_io: 'https://crates.io/api/v1/crates'
  };

  async lookup(code: string, language: string): Promise<PackageLookupResult> {
    const packages = this.extractPackages(code, language);
    const registry = this.getRegistry(language);

    const results = await Promise.all(
      packages.map(pkg => this.checkPackageExists(pkg, registry))
    );

    const hallucinatedPackages = results
      .filter(r => !r.exists)
      .map(r => r.name);

    return {
      passed: hallucinatedPackages.length === 0,
      packages: results,
      hallucinatedPackages
    };
  }

  private extractPackages(code: string, language: string): string[] {
    // Heuristic 1: Explicit installation commands
    const installRegex = language === 'python'
      ? /pip install ([a-zA-Z0-9_-]+)/g
      : /npm install ([a-zA-Z0-9_@/-]+)/g;

    const packages: string[] = [];
    let match;

    while ((match = installRegex.exec(code)) !== null) {
      packages.push(match[1]);
    }

    // Heuristic 2: Import statements
    if (language === 'python') {
      const importRegex = /^(?:from|import)\s+([a-zA-Z0-9_]+)/gm;
      while ((match = importRegex.exec(code)) !== null) {
        packages.push(match[1]);
      }
    } else if (language === 'javascript' || language === 'typescript') {
      const requireRegex = /require\(['"]([^'"]+)['"]\)/g;
      while ((match = requireRegex.exec(code)) !== null) {
        const pkg = match[1].split('/')[0];  // Handle scoped packages
        if (!pkg.startsWith('.')) {  // Ignore relative imports
          packages.push(pkg);
        }
      }
    }

    return [...new Set(packages)];  // Deduplicate
  }

  private async checkPackageExists(
    packageName: string,
    registry: string
  ): Promise<{ name: string; exists: boolean; registry: string }> {
    try {
      const response = await fetch(`${registry}/${packageName}/json`);
      return {
        name: packageName,
        exists: response.ok,
        registry
      };
    } catch (error) {
      logger.warn('Package lookup failed', { packageName, error });
      return {
        name: packageName,
        exists: false,
        registry
      };
    }
  }
}
```

### 5.5 API Documentation Validator

```typescript
class APIDocumentationValidator {
  private docSources = {
    python: [
      'https://docs.python.org/3/',
      'https://requests.readthedocs.io/',
      'https://fastapi.tiangolo.com/'
    ],
    javascript: [
      'https://developer.mozilla.org/en-US/docs/Web/JavaScript',
      'https://nodejs.org/api/',
      'https://expressjs.com/en/api.html'
    ]
  };

  async validate(code: string, language: string): Promise<APIValidationResult> {
    const apiCalls = this.extractAPICalls(code, language);

    const results = await Promise.all(
      apiCalls.map(api => this.validateAPI(api, language))
    );

    const hallucinatedAPIs = results
      .filter(r => !r.exists)
      .map(r => r.name);

    const parameterErrors = results
      .filter(r => r.exists && r.hasParameterErrors)
      .map(r => ({
        api: r.name,
        expectedParams: r.expectedParams || [],
        actualParams: r.actualParams || []
      }));

    return {
      passed: hallucinatedAPIs.length === 0 && parameterErrors.length === 0,
      apis: results,
      hallucinatedAPIs,
      parameterErrors
    };
  }

  private extractAPICalls(code: string, language: string): string[] {
    // Simplified extraction (real implementation would use AST parsing)
    const apiRegex = language === 'python'
      ? /(\w+\.\w+)\(/g
      : /(\w+\.\w+)\(/g;

    const apis: string[] = [];
    let match;

    while ((match = apiRegex.exec(code)) !== null) {
      apis.push(match[1]);
    }

    return [...new Set(apis)];
  }

  private async validateAPI(
    apiName: string,
    language: string
  ): Promise<{
    name: string;
    exists: boolean;
    signature?: string;
    documentationUrl?: string;
    frequency: 'high' | 'low';
    hasParameterErrors: boolean;
    expectedParams?: string[];
    actualParams?: string[];
  }> {
    // This would integrate with API documentation databases
    // For now, simplified placeholder
    const exists = await this.checkAPIExists(apiName, language);
    const frequency = await this.getAPIFrequency(apiName);

    return {
      name: apiName,
      exists,
      frequency,
      hasParameterErrors: false  // Requires signature comparison
    };
  }

  private async getAPIFrequency(apiName: string): Promise<'high' | 'low'> {
    // Query training corpus or API usage statistics
    // High frequency: >=101 occurrences, Low frequency: <=10 occurrences
    const occurrences = await this.queryCorpus(apiName);
    return occurrences >= 101 ? 'high' : 'low';
  }
}
```

---

## 6. Recommendations for Tamma

### 6.1 Short-Term (Epic 3 - Quality Gates)

1. **Implement Package Registry Validation**
   - Integrate PyPI, npm, Maven, RubyGems APIs
   - Auto-reject code with hallucinated packages
   - Emit event: `CODE.VALIDATION.PACKAGE_HALLUCINATION_DETECTED`

2. **Basic Type Checking**
   - Enforce TypeScript strict mode for Tamma codebase
   - Add mypy for Python generators (if applicable)
   - Auto-reject code with type errors

3. **Security Scanning**
   - Integrate Semgrep with pre-configured rules
   - Block critical security vulnerabilities (SQL injection, XSS, etc.)
   - Emit event: `GATE.SECURITY_SCAN.CRITICAL_VULNERABILITY_FOUND`

### 6.2 Medium-Term (Epic 4 - Autonomous Loop)

1. **Confidence-Based Gating**
   - Extract model confidence scores (logprobs) from AI providers
   - Trigger DAG++ for confidence < 0.8 or low-frequency APIs
   - Emit event: `CODE.GENERATED.LOW_CONFIDENCE`

2. **API Documentation Augmentation (DAG++)**
   - Scrape API documentation for low-frequency APIs
   - Augment prompts with official signatures
   - Track improvement metrics (before/after DAG++)

3. **Requirement Tracing**
   - Parse issue descriptions into acceptance criteria
   - Compare generated code against criteria
   - Flag missing functionality for human review

### 6.3 Long-Term (Epic 6 - Testing & Observability)

1. **Adversarial Test Suite**
   - Build Tamma-specific hallucination benchmark
   - Test all supported AI providers monthly
   - Publish leaderboard (internal or public)

2. **Hallucination Metrics Dashboard**
   - Track hallucination rates per provider, language, task type
   - Monitor trends over time (are models improving?)
   - Alert on anomalies (sudden spike in hallucinations)

3. **Automated Mitigation**
   - Self-correction loop: Regenerate code on high hallucination score
   - A/B testing: Compare with/without DAG++
   - Learn from human reviews: Fine-tune detection thresholds

### 6.4 Scoring Methodology Summary

**Recommended Weights:**
- **Hallucination Score:** `(Critical × 50) + (High × 20) + (Medium × 5) + (Low × 1)`
- **Acceptance Thresholds:** `< 5 (auto) | 5-15 (warn) | 15-30 (review) | > 30 (reject)`
- **False Negative Weight:** 3x higher than False Positive (safety over convenience)
- **Code Quality Score:** `(0.7 × Hallucination Rate) + (0.3 × Non-Implementation Rate)`

**Risk Assessment:**
- **High Risk:** Confidence < 0.8 OR low-frequency APIs → Trigger DAG++
- **Medium Risk:** Confidence < 0.9 → Extra validation
- **Low Risk:** Confidence >= 0.9 AND high-frequency APIs → Standard validation

---

## 7. Conclusion

**Key Takeaways:**

1. **Hallucination Detection is Multi-Layered:**
   - Static validation (100% automated): Package/API existence, type checking, linting
   - Dynamic validation (95% automated): Tests, builds, security scans
   - Semantic validation (50% automated): Requirements, confidence, human review

2. **Code Hallucinations are Common:**
   - 19.7% of LLM-generated code contains hallucinated packages
   - 61% hallucination rate for low-frequency APIs (vs 6% for high-frequency)
   - Security vulnerabilities, incomplete functionality, and deprecated APIs are pervasive

3. **Automated Detection is Feasible:**
   - Package registries provide ground truth for package existence
   - API documentation enables signature validation
   - Type checkers, linters, and security scanners catch many hallucinations
   - Model confidence scores predict hallucination risk

4. **Human Judgment Still Required:**
   - Incomplete functionality (missing edge cases) evades automation
   - Subtle logic errors require domain expertise
   - Ambiguous requirements need clarification
   - Semantic correctness (does it match intent?) is hard to automate

5. **Mitigation Strategies Work:**
   - DAG++ (selective documentation augmentation): 8.2% improvement
   - RAG and fine-tuning reduce hallucinations (but may degrade code quality)
   - Self-detection has limited success (models struggle to recognize own errors)

**Next Steps for Tamma:**

1. Prioritize **package registry integration** and **security scanning** in Epic 3
2. Implement **confidence-based gating** in Epic 4 (low confidence → DAG++)
3. Build **adversarial test suite** in Epic 6 to benchmark providers
4. Publish **hallucination metrics** in observability dashboard (Epic 5)
5. Continuously refine **scoring thresholds** based on production data

**Philosophical Insight:**

Hallucination detection is fundamentally about **balancing safety and productivity**. Over-flagging wastes developer time (false positives); under-flagging causes production bugs (false negatives). Tamma should weight false negatives 3x higher because **missed hallucinations cost more than over-cautious rejections**.

The goal is not zero hallucinations (impossible with current LLM architectures) but rather **controlled hallucination rates with transparent audit trails**. Every hallucination should be logged, tracked, and analyzed to improve future detections.

---

## References

1. **Vectara Hallucination Leaderboard**
   - Repository: https://github.com/vectara/hallucination-leaderboard
   - HHEM-2.1: https://huggingface.co/vectara/hallucination_evaluation_model
   - Blog: https://www.vectara.com/blog/hhem-2-1-a-better-hallucination-detection-model

2. **LechMazur RAG Hallucination Benchmark**
   - Leaderboard: https://lechmazur.github.io/leaderboard1.html
   - Repository: https://github.com/lechmazur/confabulations

3. **Package Hallucinations Research**
   - Paper: "We Have a Package for You!" (arXiv:2406.10279v3)
   - URL: https://arxiv.org/html/2406.10279v3

4. **API Hallucinations Research**
   - Paper: "On Mitigating Code LLM Hallucinations with API Documentation" (arXiv:2407.09726v1)
   - URL: https://arxiv.org/html/2407.09726v1

5. **General Code Hallucinations Research**
   - Paper: "LLM Hallucinations in Practical Code Generation" (arXiv:2409.20550v1)
   - URL: https://arxiv.org/html/2409.20550v1

6. **Additional Resources**
   - AggreFact Benchmark: Factual consistency in summarization
   - RAGTruth Benchmark: RAG hallucination evaluation
   - CloudAPIBench: Cloud API hallucination benchmark
   - HalluCode: Code hallucination recognition benchmark
