# LiveBench.ai & LiveCodeBench Pro: Contamination-Free Benchmark Research

**Research Date**: November 1, 2025
**Researcher**: AI Analysis (Claude)
**Purpose**: Document "live" benchmark methodologies to inform AIBaaS leaderboard design

---

## Executive Summary

Both LiveBench.ai and LiveCodeBench Pro solve the **benchmark contamination problem** through continuous updates with recently-released data. Their approaches differ in scope:

- **LiveBench.ai**: Multi-capability benchmark across 6 domains (math, coding, reasoning, language, instruction following, data analysis)
- **LiveCodeBench Pro**: Elite competitive programming benchmark using Codeforces/ICPC/IOI problems with Elo-based difficulty tiers

**Key Finding**: "Live" benchmarks require **monthly updates** with post-training data sources (recent contests, papers, datasets) and **strict contamination detection** via model release dates vs. problem release dates.

---

## 1. LiveBench.ai

### 1.1 "Live" Contamination Prevention Methodology

#### Data Sources (Post-Training)
LiveBench sources questions from information released **after April 1, 2024**, ensuring models trained before this date cannot have seen the data:

| Source Type | Examples | Update Frequency |
|-------------|----------|------------------|
| Math Competitions | AMC12, AIME, USAMO, IMO, SMC | Monthly (new contests) |
| Academic Papers | ArXiv abstracts (recent publications) | Monthly |
| News Articles | The Guardian articles | Monthly |
| Entertainment | IMDb movie synopses, Wikipedia pages | Monthly |
| Datasets | Kaggle datasets, Socrata data | Monthly |
| Coding Contests | LeetCode, AtCoder (via LiveCodeBench) | Monthly |

#### Update Process
1. **Monthly releases**: New questions added on a fixed monthly schedule
2. **Initial release**: June 2024 (problems from April 1 - June 12, 2024)
3. **Latest release**: April 25, 2025 (6 monthly updates completed)
4. **Stability**: Rank correlation between updates remains >0.997

#### Contamination Detection
- **Model release date tracking**: Models are flagged if released after problem publication dates
- **Time-window filtering**: Users can select problems within specific date ranges to exclude potentially contaminated data
- **Red highlighting**: Leaderboard marks likely contaminated models in red

#### Additional Anti-Contamination Measures
- **Harder versions**: Creates "contamination-limited versions" of existing benchmarks (Big-Bench Hard, AMPS, IFEval) with additional complexity
- **Objective scoring**: Uses verifiable ground-truth answers instead of LLM judges, eliminating judge contamination

### 1.2 Multi-Task Coverage

#### 6 Core Categories (18 Total Tasks)

**1. Math** (2-4 tasks)
- High school competition problems (AMC12, AIME, USAMO, IMO, SMC)
- Proof-based mathematical reasoning
- Harder synthetic AMPS questions
- Recent (past 12 months) problems only

**2. Coding** (2-4 tasks)
- Code generation from LeetCode (medium/hard only)
- Code generation from AtCoder contests
- Novel code completion task
- Evaluation: pass@1 (all test cases must pass)

**3. Reasoning** (3 tasks)
- Harder Big-Bench Hard questions with deductive components and red herrings
- Zebra Puzzles (logical deduction)
- Spatial reasoning questions
- Boolean function evaluation from natural language

**4. Language** (3 tasks)
- Connections word puzzles (NYT-style)
- Typo removal from recent text
- Movie synopsis unscrambling (recent IMDb movies)

**5. Instruction Following** (4 tasks)
- Paraphrase recent Guardian articles with constraints
- Simplify articles with word limits
- Summarize with specific requirements
- Generate stories incorporating specific elements

**6. Data Analysis** (3 tasks)
- Table reformatting from Kaggle/Socrata datasets
- Predict join columns for two tables
- Predict correct type annotations for data columns

### 1.3 Scoring Methodology

#### Hierarchical Equal-Weight Averaging

```
Question Score (0-1)
    ↓ Average
Task Score (0-1) ────────┐
    ↓ Average            │
Category Score (0-1) ────┤ 2-4 tasks per category
    ↓ Average            │
Overall Score (0-100) ───┘ 6 categories equally weighted
```

**Scoring Rules**:
1. Each question receives binary or fractional score (0.0 to 1.0)
2. Task score = Average of all question scores in that task
3. Category score = Average of all task scores in that category
4. Overall score = Average of 6 category scores × 100

**Key Properties**:
- **Equal weighting**: All categories contribute 16.67% to final score
- **No LLM judges**: All answers scored against objective ground truth
- **No human crowdsourcing**: Fully automated evaluation
- **Transparency**: All questions, code, and model answers publicly available

#### Performance Results
- **Top model accuracy**: <70% overall (typically <65%)
- **Difficulty by design**: Benchmark intentionally challenging to distinguish future model improvements
- **Evaluated models**: 93 unique models (0.5B to 405B parameters)

### 1.4 Leaderboard Features

#### Data Sources
- **Questions**: `livebench/question` on HuggingFace
- **Model Answers**: `livebench/model_answer` (93.7k rows, Parquet format)
- **Results**: CSV files (`all_groups.csv`, `all_tasks.csv`)

#### Filtering Capabilities

1. **Time-Window Filtering**
   - Dual-slider interface for date range selection
   - Shows "X problems selected in the current time window"
   - Prevents contamination by excluding pre-training problems

2. **Merged Models Filter**
   - Toggle to hide/show merged/fine-tuned models
   - Addresses concerns about model blending accuracy

3. **Contamination Highlighting**
   - Models released after problem dates marked in red
   - Visual warning system for potentially contaminated results

#### Sorting & Display

- **Primary sort**: Overall score (Pass@1) descending
- **Rank badges**: Gradient styling for top 3 (gold/silver/bronze)
- **Sticky header**: Category labels remain visible during scroll
- **Responsive design**: Adapts to mobile/tablet/desktop

#### Table Structure

| Column | Description | Format |
|--------|-------------|--------|
| Rank | Position (1, 2, 3, ...) | Gradient badge for top 3 |
| Model | Name with documentation link | Clickable hyperlink |
| Overall | Average score across 6 categories | 0-100 scale |
| Math | Category-specific score | 0-100 scale |
| Coding | Category-specific score | 0-100 scale |
| Reasoning | Category-specific score | 0-100 scale |
| Language | Category-specific score | 0-100 scale |
| Instruction | Category-specific score | 0-100 scale |
| Data Analysis | Category-specific score | 0-100 scale |

#### Visualization Elements

1. **Radar Chart**
   - 6-axis plot showing performance across categories
   - Comparative view of model strengths/weaknesses
   - Highlights uneven performance (e.g., strong coding, weak reasoning)

2. **Alternating Row Backgrounds**
   - Zebra striping for readability
   - Hover highlighting for row focus

3. **Animated Transitions**
   - Staggered entry effects for rows
   - Smooth slider rail updates

### 1.5 UI/UX Design Patterns

#### Effective Patterns

1. **Contamination Transparency**
   - Visual red highlighting of suspicious results
   - Time-window slider for user-controlled filtering
   - Model release date vs. problem date tracking

2. **Progressive Disclosure**
   - Overall score prominent
   - Category breakdown visible but secondary
   - Task-level details in CSV downloads

3. **Performance Context**
   - Radar charts show relative strengths
   - Category scores reveal specialization
   - Rank badges provide quick recognition

4. **Data Accessibility**
   - Public HuggingFace datasets
   - CSV exports for custom analysis
   - Full question/answer transparency

### 1.6 API & Programmatic Access

#### HuggingFace Datasets API

**Available Datasets**:
```python
from datasets import load_dataset

# Questions by category
coding = load_dataset("livebench/coding")
reasoning = load_dataset("livebench/reasoning")
language = load_dataset("livebench/language")
data_analysis = load_dataset("livebench/data_analysis")
math = load_dataset("livebench/math")
instruction = load_dataset("livebench/instruction_following")

# Model answers (leaderboard data)
answers = load_dataset("livebench/model_answer",
                       split="leaderboard")  # 93.7k rows
```

**Data Structure**:
```python
{
  "question_id": "q_abc123...",      # 64-char unique ID
  "answer_id": "a_xyz789...",        # 22-char unique ID
  "model_id": "gpt-4o",              # 93 unique models
  "choices": [                        # Nested structure
    {
      "index": 0,
      "turns": [...]
    }
  ],
  "tstamp": 1698483296.789,          # Unix timestamp
  "category": "coding",              # 6 categories
  "task": "leetcode_medium"          # 18 task types
}
```

**Format**: Parquet (compatible with pandas, Polars, DuckDB)

#### Python Evaluation API

**Run Benchmark**:
```bash
python run_livebench.py \
  --model gpt-4o \
  --bench-name live_bench/coding \
  --livebench-release-option 2024-11-25 \
  --parallel-requests 5
```

**Supported Providers**:
- OpenAI (native)
- Anthropic (native)
- Cohere, Mistral, Together, Google (via API)
- Local models (via vLLM, Ollama)

**Key Parameters**:
- `--bench-name`: Subset selection (`live_bench` = all, `live_bench/coding` = coding only)
- `--resume`, `--retry-failures`: Handle interruptions
- `--mode parallel`: Parallelizes across categories using tmux

### 1.7 Update Frequency Recommendation for AIBaaS

**Recommended**: **Monthly updates** with staggered releases per category

**Rationale**:
1. **LiveBench precedent**: Monthly updates maintain <0.997 rank correlation (stable rankings)
2. **Data availability**: New coding contests, papers, datasets release monthly
3. **Model training cycles**: Most models have 2-4 week training periods; monthly updates ensure post-training data
4. **Operational feasibility**: Monthly cadence balances freshness vs. curation effort

**Implementation Strategy**:
```
Week 1: Collect new problems (contests, papers released in previous month)
Week 2: Curate and validate problems (remove ambiguous, add test cases)
Week 3: Run evaluations on existing models
Week 4: Release updated leaderboard with new problems
```

**Staggered Category Updates** (reduce workload spikes):
- Week 1: Math + Coding
- Week 2: Reasoning + Language
- Week 3: Instruction Following + Data Analysis

---

## 2. LiveCodeBench Pro

### 2.1 "Live" Contamination Prevention Methodology

#### Problem Sources (Elite Competitive Programming)

| Platform | Description | Difficulty Range | Update Frequency |
|----------|-------------|------------------|------------------|
| **Codeforces** | Russian competitive programming platform | Elo 800-3500+ | ~2 contests/week |
| **ICPC** | International Collegiate Programming Contest | Regional to World Finals | Annual regionals, yearly finals |
| **IOI** | International Olympiad in Informatics | High school elite | Annual (July) |
| **Select University Contests** | Top-tier university competitions | Varies | Quarterly |

#### Data Releases

- **Release v1** (May 2023 - Mar 2024): 400 problems
- **Release v5** (May 2023 - Jan 2025): 880 problems
- **Release v6** (May 2023 - Apr 2025): 1,055 problems (current)

**Update Strategy**:
- **Continuous collection**: New contest problems added within days of publication
- **Contamination window**: Problems annotated with exact release dates
- **Time-window evaluation**: Models evaluated only on problems released after training cutoff

#### Olympiad Medalist Curation

**Unique Differentiator**: Team of Olympiad medalists (IOI/ICPC medalists):
- **Annotate algorithmic categories**: Graph theory, DP, greedy, number theory, etc.
- **Line-by-line failure analysis**: Manual review of incorrect submissions
- **Difficulty calibration**: Ensure Elo ratings reflect true human performance

### 2.2 Difficulty Tiers (Elo-Based Classification)

#### Tier Definitions

| Tier | Elo Range | Human Percentile | Description |
|------|-----------|------------------|-------------|
| **Easy** | ≤2000 | ~95th percentile | Specialist/Expert level (Codeforces Div2 C/D) |
| **Medium** | 2000-3000 | 95th-99.9th percentile | Candidate Master to International Grandmaster |
| **Hard** | >3000 | >99.9th percentile | Legendary Grandmaster level (IOI gold medalists) |

#### Elo Rating System Methodology

**Bayesian Maximum A Posteriori (MAP) Estimator**:
1. Models solve problems with known Elo ratings
2. Pass/fail results update model's estimated Elo using Bayesian inference
3. Final Elo directly comparable to Codeforces human ratings

**Percentile Mapping**:
- **1400 Elo**: ~50th percentile (Pupil rank)
- **1900 Elo**: ~90th percentile (Expert rank)
- **2100 Elo**: ~98.5th percentile (Candidate Master rank) ← Best model without tools (o4-mini-high)
- **2400 Elo**: ~99.5th percentile (International Master rank)
- **2700 Elo**: ~99.9th percentile (Grandmaster rank) ← Models with tools (o3, o4-mini-high)
- **3000+ Elo**: >99.95th percentile (Legendary Grandmaster)

#### Hard Tier "99.9% Fail Rate" Explanation

**Not a literal 99.9% fail rate** - refers to **Elo >3000 requiring >99.9th percentile human skill**:
- Problems rated 3000+ Elo are solvable by <0.1% of Codeforces users
- **All current LLMs score 0% Pass@1** on Hard tier (even best models)
- Represents gap between top LLMs (2100-2700 Elo) and human grandmasters (3000+ Elo)

### 2.3 Evaluation Scenarios & Metrics

#### 4 Assessment Scenarios

**1. Code Generation** (Primary)
- **Task**: Generate code from problem statement
- **Metric**: Pass@1 (all test cases pass on first try)
- **Pass@5**: Best of 5 attempts
- **Inference**: vLLM for local models, API for commercial

**2. Self-Repair**
- **Task**: Fix failing code given test failure feedback
- **Metric**: Repair@k (success after k fix attempts)
- **Process**: Model receives error message → generates fix → re-test
- **Use case**: Evaluates debugging capability

**3. Test Output Prediction**
- **Task**: Predict program output without executing code
- **Metric**: Exact match accuracy
- **Evaluates**: Code comprehension and trace simulation

**4. Code Execution** (with Chain-of-Thought)
- **Task**: Execute code mentally with step-by-step reasoning
- **Metric**: Correct final output
- **Evaluates**: Algorithmic understanding vs. pattern matching

#### Performance Results (Without External Tools)

| Difficulty | Best Model | Pass@1 | Elo Rating |
|------------|-----------|--------|------------|
| Easy | o4-mini-high | ~75% | 2116 |
| Medium | o4-mini-high | 53% | 2116 |
| Hard | All models | 0% | N/A |

**With External Tools** (terminal, search, web browser):
- **o3**: 2700+ Elo (99.9th percentile)
- **o4-mini-high**: 2700+ Elo (99.9th percentile)
- **Tool impact**: +600 Elo (~10x percentile improvement)

#### Key Findings

**LLM Strengths**:
- **Implementation-heavy problems**: Strong at translating clear algorithms to code
- **Pattern matching**: Recognizes common competitive programming templates

**LLM Weaknesses**:
- **Nuanced algorithmic reasoning**: Struggles with novel algorithm design
- **Complex case analysis**: Misses edge cases requiring deep problem understanding
- **Confidently incorrect**: Generates plausible-sounding but wrong justifications
- **Grandmaster gap**: 600-900 Elo below human elite (without tools)

### 2.4 Algorithmic Categories (Olympiad Medalist Annotations)

**Problem Taxonomy** (based on competitive programming):

| Category | Examples | Frequency in Benchmark |
|----------|----------|------------------------|
| **Graph Theory** | Shortest paths, MST, max flow, bipartite matching | ~20% |
| **Dynamic Programming** | Knapsack, LIS, bitmask DP, tree DP | ~25% |
| **Greedy Algorithms** | Interval scheduling, Huffman coding | ~10% |
| **Number Theory** | Modular arithmetic, prime factorization, GCD/LCM | ~8% |
| **Combinatorics** | Permutations, combinations, inclusion-exclusion | ~8% |
| **Data Structures** | Segment trees, Fenwick trees, disjoint sets | ~12% |
| **String Algorithms** | KMP, suffix arrays, trie structures | ~7% |
| **Geometry** | Convex hull, line intersection, coordinate geometry | ~5% |
| **Ad-Hoc** | Problem-specific logic, simulation | ~5% |

**Annotation Process**:
1. Olympiad medalist solves problem manually
2. Identifies primary algorithmic technique(s)
3. Tags problem with categories (1-3 tags)
4. Reviews failed LLM submissions for error patterns

**Value for AIBaaS**:
- **Capability profiling**: Identify which algorithm types a model excels at
- **Training insights**: Reveal gaps in model's algorithmic knowledge
- **Benchmark evolution**: Add underrepresented categories

### 2.5 Leaderboard Features

#### Data Access
- **Website**: https://livecodebenchpro.com
- **Paper**: arXiv:2506.11928
- **GitHub**: https://github.com/GavinZhengOI/LiveCodeBench-Pro (not official repo)

**Note**: LiveCodeBench Pro leaderboard less publicly documented than LiveBench; details inferred from paper.

#### Expected Features (Based on Paper)

**Table Structure**:

| Model | Overall Elo | Easy Pass@1 | Medium Pass@1 | Hard Pass@1 | Percentile | Tools Used |
|-------|-------------|-------------|---------------|-------------|------------|------------|
| o4-mini-high | 2116 | ~75% | 53% | 0% | 98.5% | None |
| o3 (w/ tools) | 2700+ | ~95% | ~80% | ~15% | 99.9% | Terminal, search |

**Filtering**:
- **Difficulty tier**: Show only Easy/Medium/Hard problems
- **Tool usage**: Separate leaderboards for "No Tools" vs. "With Tools"
- **Time window**: Filter problems by release date (contamination prevention)
- **Algorithmic category**: Filter by graph, DP, greedy, etc.

**Sorting**:
- **By Elo rating**: Default (descending)
- **By percentile**: Human-comparable ranking
- **By difficulty tier**: Best Easy, Medium, or Hard performance

**Comparison**:
- **Elo gap visualization**: Show Elo difference from human grandmaster (2700+)
- **Percentile mapping**: Convert Elo to competitive programming ranks

### 2.6 UI/UX Design Patterns

#### Effective Patterns (Inferred from Paper & General Competitive Programming Sites)

**1. Elo-Based Ranking** (vs. Percentage Accuracy)
- **Advantage**: Directly comparable to human Codeforces ratings
- **Context**: Users immediately understand 2100 Elo = 98.5th percentile
- **Implementation**: Display both Elo and percentile for clarity

**2. Difficulty Tier Breakdown**
- **Advantage**: Reveals model performance curve (strong on easy, weak on hard)
- **Context**: Exposes whether model truly understands algorithms or pattern-matches
- **Implementation**: 3-column Easy/Medium/Hard Pass@1 display

**3. Tool Usage Separation**
- **Advantage**: Fair comparison (tool-augmented models have 600+ Elo boost)
- **Context**: Isolates model reasoning from tool orchestration capability
- **Implementation**: Toggle between "Pure LLM" and "LLM + Tools" leaderboards

**4. Algorithmic Category Filters**
- **Advantage**: Identify model specialization (e.g., strong at DP, weak at geometry)
- **Context**: Helps developers choose models for specific coding tasks
- **Implementation**: Multi-select checkbox filters

**5. Human Percentile Context**
- **Advantage**: Grounds model performance in competitive programming community
- **Context**: "98.5th percentile" more meaningful than "2100 Elo" for non-competitors
- **Implementation**: Show rank equivalents (Expert, Candidate Master, etc.)

### 2.7 API & Programmatic Access

#### HuggingFace Datasets

**Available Datasets**:
```python
from datasets import load_dataset

# Problem sets (v1-v6 releases)
code_gen_lite = load_dataset("livecodebench/code_generation_lite")  # 400 problems (v1)
code_gen_full = load_dataset("livecodebench/code_generation")       # 1055 problems (v6)

# Evaluation scenarios
execution = load_dataset("livecodebench/execution")
test_prediction = load_dataset("livecodebench/test_generation")
```

**Data Structure**:
```python
{
  "problem_id": "codeforces_1234_E",
  "source": "codeforces",
  "contest_id": "1234",
  "problem_letter": "E",
  "difficulty_elo": 2800,         # Actual Codeforces rating
  "difficulty_tier": "hard",      # Easy/Medium/Hard
  "release_date": "2024-03-15",
  "algorithmic_tags": ["dp", "graph", "greedy"],
  "olympiad_medalist_notes": "Requires segment tree + DP optimization...",
  "statement": "...",
  "test_cases": [...],
  "time_limit_ms": 2000,
  "memory_limit_mb": 256
}
```

#### Python Evaluation API

**Run Code Generation Benchmark**:
```bash
python -m lcb_runner.runner.main \
  --model deepseek-coder-33b \
  --scenario codegeneration \
  --release_version release_v6 \
  --evaluate \
  --use_cache
```

**Run Self-Repair Evaluation**:
```bash
python -m lcb_runner.runner.main \
  --model gpt-4o \
  --scenario codegeneration \
  --codegen_n 5 \              # 5 repair attempts
  --evaluate
```

**Key Parameters**:
- `--release_version`: `release_v1` to `release_v6`
- `--scenario`: `codegeneration`, `execution`, `test_prediction`
- `--tensor_parallel_size`: GPU distribution for local models
- `--continue_existing`: Resume interrupted evaluations

#### Leaderboard Submission

**Process** (from HuggingFace blog):
1. Run local evaluation using LiveCodeBench repo
2. Generate results JSON with pass@1, pass@5 metrics
3. Submit via Google Form (manual review by Olympiad medalists)
4. Results appear on leaderboard after validation (~1 week)

**No Public API**: Unlike LiveBench, LiveCodeBench Pro doesn't expose leaderboard data via HuggingFace datasets (as of Nov 2025).

---

## 3. Comparative Analysis: LiveBench vs. LiveCodeBench Pro

| Aspect | LiveBench.ai | LiveCodeBench Pro |
|--------|--------------|-------------------|
| **Scope** | Multi-capability (6 categories) | Elite competitive programming only |
| **Update Frequency** | Monthly (new problems) | Continuous (2-3 contests/week from Codeforces) |
| **Contamination Prevention** | Post-April 2024 data sources | Post-May 2023 contest problems with release dates |
| **Difficulty Calibration** | Top models <70% accuracy | Elo-based human-comparable ratings |
| **Scoring** | 0-100 scale (equal-weighted categories) | Elo rating (Bayesian MAP estimator) |
| **Evaluation** | Objective ground truth (automated) | Competitive programming judge + medalist review |
| **Public API** | Yes (HuggingFace datasets, Python) | Partial (HuggingFace datasets, no leaderboard API) |
| **Leaderboard Filtering** | Time window, merged models | Difficulty tier, tool usage, algorithmic category |
| **Visualization** | Radar chart (6 categories) | Elo curve, percentile mapping |
| **Human Benchmark** | No direct human comparison | Direct Elo comparison to Codeforces users |
| **Best Model Performance** | <70% overall | 2116 Elo (98.5th percentile) without tools |
| **Contamination Detection** | Red highlighting (model vs. problem dates) | Time-window filtering (release_version parameter) |
| **Curation** | Automated (APIs) + manual validation | Olympiad medalists (line-by-line review) |

---

## 4. Recommendations for AIBaaS Leaderboard Design

### 4.1 Contamination Prevention Strategy

**Adopt LiveBench's Monthly Update Cadence**:
- **Why**: Balances data freshness with operational feasibility
- **Implementation**: Release new problems on 1st of each month from previous month's sources
- **Sources**: Recent coding contests (LeetCode, Codeforces), arXiv papers (AI/ML), GitHub issues (open-source projects)

**Implement Time-Window Filtering**:
- **Why**: Allows users to exclude problems released before model training
- **Implementation**: Date-range slider (like LiveBench) with problem count display
- **Default**: Show all problems; highlight contamination risk in red

**Track Model Release Dates**:
- **Why**: Automatic contamination flagging
- **Implementation**: Database field `model_release_date`; compare against `problem_release_date`
- **Display**: Red row highlighting + warning icon for potentially contaminated results

**Problem Versioning** (LiveCodeBench approach):
- **Why**: Users can reproduce historical benchmarks
- **Implementation**: Snapshot problems monthly → `aibaas_v2025_01`, `aibaas_v2025_02`, etc.
- **API**: `--release_version` parameter to select snapshot

### 4.2 Difficulty Tier Classification

**Use Hybrid Approach** (LiveBench categories + LiveCodeBench Pro Elo tiers):

**Category-Level Tiers** (like LiveBench):
- **Basic** (0-33rd percentile): Simple API calls, basic CRUD
- **Intermediate** (33-66th percentile): Multi-step workflows, error handling
- **Advanced** (66-100th percentile): Complex orchestrations, ambiguity resolution

**Elo-Based Granularity** (like LiveCodeBench Pro):
- **Easy**: ≤1500 Elo (tasks 50% of humans solve)
- **Medium**: 1500-2000 Elo (tasks 10-50% of humans solve)
- **Hard**: 2000-2500 Elo (tasks 1-10% of humans solve)
- **Expert**: >2500 Elo (tasks <1% of humans solve)

**Calculation**:
1. Start with uniform Elo (1500) for all problems
2. Models attempt problems → pass/fail updates problem Elo
3. After 100+ attempts, Elo stabilizes to true difficulty
4. Re-categorize problems monthly as Elo refines

### 4.3 Leaderboard Filtering & Sorting UI

#### Filtering Options (Priority Order)

**1. Time-Window Filter** (LiveBench pattern)
```
[====|====================|====]  ← Dual-slider
 Jan 2025              Dec 2025

 "245 tasks selected in current time window"
```

**2. Difficulty Tier Filter** (LiveCodeBench Pro pattern)
```
☑ Easy (≤1500 Elo)     125 tasks
☑ Medium (1500-2000)   90 tasks
☑ Hard (2000-2500)     25 tasks
☐ Expert (>2500)       5 tasks
```

**3. Category Filter** (LiveBench pattern)
```
☑ Code Generation       80 tasks
☑ Debugging             45 tasks
☑ Refactoring          30 tasks
☐ Security Analysis     15 tasks
☑ Documentation        20 tasks
```

**4. Tool Usage Filter** (LiveCodeBench Pro pattern)
```
( ) No Tools Only
( ) Tools Allowed
(•) Both (Separate Columns)
```

**5. Provider Filter**
```
☑ Anthropic Claude
☑ OpenAI GPT
☐ Google Gemini
☑ Local LLMs (Ollama, vLLM)
```

#### Sorting Options

**Default**: Overall Score (descending)

**Dropdown Menu**:
- Overall Score ↓
- Overall Score ↑
- Elo Rating ↓
- Elo Rating ↑
- Model Size ↓ (params)
- Model Size ↑ (params)
- Release Date ↓ (newest first)
- Release Date ↑ (oldest first)

#### Table Structure

| Rank | Model | Overall | Easy | Medium | Hard | Expert | Elo | Percentile | Tools | Provider |
|------|-------|---------|------|--------|------|--------|-----|------------|-------|----------|
| 1 🥇 | Claude Opus 4 | 78.5 | 95% | 82% | 65% | 12% | 2250 | 99.2% | No | Anthropic |
| 2 🥈 | GPT-5 | 76.2 | 93% | 80% | 60% | 10% | 2180 | 98.8% | No | OpenAI |
| ... | ... | ... | ... | ... | ... | ... | ... | ... | ... | ... |

**Responsive Columns**:
- **Mobile**: Rank, Model, Overall (collapse difficulty breakdown)
- **Tablet**: Add Easy/Medium/Hard
- **Desktop**: Full table with Elo, Percentile, Tools, Provider

#### Comparison Features

**1. Radar Chart** (LiveBench pattern)
```
       Code Gen
           /\
          /  \
         /    \
   Debug ------  Refactor
         \    /
          \  /
           \/
       Security
```

**2. Difficulty Curve Chart** (LiveCodeBench Pro-inspired)
```
Pass@1 %
  100 |     ●●●●●●●
      |           ●●●●
   50 |               ●●●
      |                  ●
    0 |____________________●●
       Easy  Med  Hard  Expert
```

**3. Head-to-Head Comparison**
```
Select Models:  [Claude Opus 4 ▼]  vs  [GPT-5 ▼]

Category        Claude   GPT-5   Winner
─────────────────────────────────────
Code Generation   95%     93%     Claude
Debugging         82%     88%     GPT-5
Refactoring       78%     75%     Claude
Overall          78.5%   76.2%    Claude
```

### 4.4 Visualization & UX Patterns

#### From LiveBench

**✅ Adopt**:
- **Radar charts**: Show category strengths/weaknesses
- **Contamination highlighting**: Red rows for suspicious results
- **Sticky headers**: Category labels remain visible during scroll
- **Gradient rank badges**: Visual distinction for top 3

**✅ Improve**:
- **Drill-down**: Click category score → see task-level breakdown
- **Tooltips**: Hover over score → show task distribution (e.g., "Code Gen: 50/50 tasks passed")

#### From LiveCodeBench Pro

**✅ Adopt**:
- **Elo ratings**: More meaningful than percentage for task difficulty
- **Percentile mapping**: "99.2nd percentile" context for non-experts
- **Tool usage separation**: Fair comparison between pure LLM vs. tool-augmented
- **Algorithmic categories**: Tag tasks with required capabilities (e.g., "multi-step reasoning", "context management")

**✅ Improve**:
- **Elo curve visualization**: Show model performance vs. difficulty (LiveCodeBench Pro lacks this)
- **Failure analysis**: Show common error patterns (e.g., "hallucinated API", "incorrect context")

#### New Patterns for AIBaaS

**1. Cost-Performance Plot**
```
Performance (Elo)
    2500 |              ● Claude Opus 4 ($15/1M tokens)
         |          ●   GPT-5 ($8/1M tokens)
    2000 |      ●       Gemini Pro 2 ($2/1M tokens)
         |  ●           Local Llama 4 ($0.00)
    1500 |_________________________________
            $0        $5         $10        $15
                  Cost per 1M Tokens
```

**2. Latency-Performance Plot**
```
Performance (Elo)
    2500 |      ● Claude Opus 4 (3.2s)
         |  ●       GPT-5 (2.8s)
    2000 |              ● Gemini Pro 2 (5.1s)
         |                      ● Local Llama 4 (8.5s)
    1500 |_________________________________
           0s        5s         10s
              Average Response Time
```

**3. Capability Heatmap**
```
Model          │ Code Gen │ Debug │ Refactor │ Security │ Docs │
───────────────┼──────────┼───────┼──────────┼──────────┼──────┤
Claude Opus 4  │   🟩     │  🟩   │   🟩     │   🟨     │  🟩  │
GPT-5          │   🟩     │  🟩   │   🟨     │   🟩     │  🟨  │
Gemini Pro 2   │   🟨     │  🟩   │   🟩     │   🟨     │  🟩  │

🟩 >80%   🟨 60-80%   🟥 <60%
```

### 4.5 Update Frequency Recommendation

**Monthly Updates** (1st of each month)

**Rationale**:
- **LiveBench precedent**: Monthly updates with >0.997 rank correlation (stable)
- **Data availability**: New coding contests, GitHub issues, ArXiv papers monthly
- **Model training cycles**: 2-4 weeks typical; monthly ensures post-training data
- **Operational feasibility**: Weekly too frequent (curation burden), quarterly too slow (contamination risk)

**Staggered Category Releases** (reduce workload spikes):

| Week | Categories | Task Count Target |
|------|-----------|-------------------|
| Week 1 | Code Generation, Debugging | 30-40 tasks |
| Week 2 | Refactoring, Security | 20-30 tasks |
| Week 3 | Documentation, Testing | 20-30 tasks |
| Week 4 | Evaluation, Leaderboard Update | N/A |

**Emergency Updates**:
- **Trigger**: Major model release (e.g., GPT-6, Claude Opus 5)
- **Action**: Add 10-15 "recent" tasks from last 7 days
- **Frequency**: As needed (max 1/week)

### 4.6 API Access Design

**HuggingFace Datasets** (like LiveBench + LiveCodeBench):

```python
from datasets import load_dataset

# Problem sets by category
code_gen = load_dataset("aibaas/code_generation", split="v2025_11")
debugging = load_dataset("aibaas/debugging", split="v2025_11")

# Leaderboard data
leaderboard = load_dataset("aibaas/leaderboard", split="latest")

# Filtered by difficulty
hard_tasks = load_dataset("aibaas/all_tasks",
                          split="v2025_11",
                          filter=lambda x: x["elo"] > 2000)
```

**REST API** (programmatic leaderboard access):

```bash
# Get leaderboard (with filters)
GET /api/v1/leaderboard?
    category=code_generation,debugging
    &difficulty=medium,hard
    &time_window=2025-01-01,2025-12-31
    &tools=no_tools

# Response
{
  "version": "v2025_11",
  "filters": {
    "category": ["code_generation", "debugging"],
    "difficulty": ["medium", "hard"],
    "time_window": ["2025-01-01", "2025-12-31"],
    "tools": "no_tools"
  },
  "task_count": 125,
  "models": [
    {
      "rank": 1,
      "name": "Claude Opus 4",
      "provider": "Anthropic",
      "overall_score": 78.5,
      "elo": 2250,
      "percentile": 99.2,
      "difficulty_breakdown": {
        "easy": 95.0,
        "medium": 82.0,
        "hard": 65.0,
        "expert": 12.0
      },
      "category_breakdown": {
        "code_generation": 92.0,
        "debugging": 85.0
      }
    }
  ]
}
```

**WebSocket Subscriptions** (real-time updates):

```javascript
// Subscribe to leaderboard changes
ws://aibaas.com/api/v1/leaderboard/subscribe

// Message format
{
  "event": "model_evaluated",
  "model": "Claude Opus 4",
  "tasks_completed": 245,
  "tasks_total": 245,
  "provisional_rank": 1,
  "provisional_elo": 2250
}
```

---

## 5. Key Takeaways for AIBaaS

### 5.1 Contamination Prevention

**Critical Success Factors**:
1. **Post-training data sources**: Use problems released after model training cutoff
2. **Monthly updates**: Balance freshness with rank stability
3. **Release date tracking**: Flag contaminated results automatically
4. **Time-window filtering**: Let users exclude suspicious data
5. **Problem versioning**: Enable reproducible benchmarks

**Implementation Priority**: **HIGH** (core credibility requirement)

### 5.2 Difficulty Calibration

**Use Elo Ratings** (LiveCodeBench Pro approach):
- **Why**: Human-comparable, self-calibrating, robust to model diversity
- **How**: Bayesian MAP estimator updates problem Elo based on pass/fail
- **Display**: Show Elo + percentile + difficulty tier (Easy/Medium/Hard/Expert)

**Avoid Fixed Percentages** (LiveBench limitation):
- **Problem**: "Hard" tasks showing 80% pass rate → not actually hard
- **Solution**: Elo dynamically adjusts → 80% pass rate → Elo decreases → reclassified as "Medium"

**Implementation Priority**: **MEDIUM** (improves UX, not blocking for v1)

### 5.3 UI/UX Design

**Must-Have Features**:
- ✅ Time-window slider (contamination filtering)
- ✅ Difficulty tier filter (Easy/Medium/Hard/Expert)
- ✅ Category breakdown (radar chart + table columns)
- ✅ Contamination highlighting (red rows)
- ✅ Responsive design (mobile-first)

**Nice-to-Have Features**:
- 🔶 Elo curve visualization (performance vs. difficulty)
- 🔶 Cost-performance plot (Elo vs. $/1M tokens)
- 🔶 Latency-performance plot (Elo vs. avg response time)
- 🔶 Capability heatmap (category × model)
- 🔶 Head-to-head comparison (select 2 models)

**Implementation Priority**: **HIGH** (must-have), **LOW** (nice-to-have)

### 5.4 API Access

**Required**:
- ✅ HuggingFace datasets (public problems + leaderboard)
- ✅ REST API (filtered leaderboard queries)
- ✅ CSV exports (all_groups.csv, all_tasks.csv)

**Optional**:
- 🔶 WebSocket subscriptions (real-time leaderboard updates)
- 🔶 GraphQL API (flexible queries)
- 🔶 Batch evaluation API (submit model → run on all tasks → get results)

**Implementation Priority**: **MEDIUM** (HuggingFace datasets), **LOW** (WebSocket/GraphQL)

### 5.5 Update Frequency

**Recommendation**: **Monthly updates** (1st of each month)

**Justification**:
- ✅ LiveBench precedent (>0.997 rank correlation)
- ✅ Data availability (monthly contests, papers, issues)
- ✅ Model training cycles (2-4 weeks)
- ✅ Operational feasibility (weekly too frequent, quarterly too slow)

**Staggered Releases**:
- Week 1: Code Gen + Debugging
- Week 2: Refactoring + Security
- Week 3: Documentation + Testing
- Week 4: Evaluation + Leaderboard Update

**Implementation Priority**: **HIGH** (core to contamination prevention)

---

## 6. References

### 6.1 LiveBench.ai

**Paper**:
- White, C., et al. (2024). "LiveBench: A Challenging, Contamination-Limited LLM Benchmark." *ICLR 2025 Spotlight Paper*. arXiv:2406.19314. https://arxiv.org/abs/2406.19314

**Resources**:
- **Website**: https://livebench.ai
- **GitHub**: https://github.com/LiveBench/LiveBench
- **Datasheet**: https://github.com/LiveBench/LiveBench/blob/main/docs/DATASHEET.md
- **HuggingFace Datasets**:
  - Questions: `livebench/coding`, `livebench/reasoning`, `livebench/language`, `livebench/data_analysis`, `livebench/math`, `livebench/instruction_following`
  - Answers: `livebench/model_answer` (93.7k rows)

### 6.2 LiveCodeBench Pro

**Paper**:
- Zhou, Z., et al. (2025). "LiveCodeBench Pro: How Do Olympiad Medalists Judge LLMs in Competitive Programming?" arXiv:2506.11928. https://arxiv.org/abs/2506.11928

**Resources**:
- **Website**: https://livecodebenchpro.com
- **Original LiveCodeBench**: https://livecodebench.github.io
- **GitHub**: https://github.com/LiveCodeBench/LiveCodeBench
- **HuggingFace Datasets**:
  - Problems: `livecodebench/code_generation_lite` (400), `livecodebench/code_generation` (1055)
  - Evaluation: `livecodebench/execution`, `livecodebench/test_generation`
- **Leaderboard**: https://livecodebench.github.io/leaderboard.html

### 6.3 Related Resources

**Benchmark Leaderboards**:
- **Artificial Analysis**: https://artificialanalysis.ai/evaluations/livecodebench
- **GTLLMZoo** (Gradio multi-benchmark aggregator): https://github.com/git-disl/GTLLMZoo

**Technical Documentation**:
- **Gradio Leaderboard Component**: https://pypi.org/project/gradio-leaderboard/
- **HuggingFace Blog**: "Introducing the LiveCodeBench Leaderboard" (https://huggingface.co/blog/leaderboard-livecodebench)

---

## 7. Appendix: Implementation Checklist for AIBaaS

### Phase 1: Contamination Prevention (Sprint 1-2)

- [ ] **Problem Collection Pipeline**
  - [ ] Scrape LeetCode weekly contests (code generation)
  - [ ] Scrape Codeforces recent contests (competitive programming)
  - [ ] Scrape GitHub trending issues (bug fixing, feature requests)
  - [ ] Scrape ArXiv recent papers (documentation, explanation)

- [ ] **Problem Metadata Schema**
  ```typescript
  interface Problem {
    id: string;                    // UUID
    category: 'code_generation' | 'debugging' | 'refactoring' | 'security' | 'documentation';
    release_date: string;          // ISO 8601 (YYYY-MM-DD)
    source: string;                // 'leetcode', 'codeforces', 'github', 'arxiv'
    source_url: string;            // Original problem URL
    elo: number;                   // 1500 initial, updated dynamically
    difficulty_tier: 'easy' | 'medium' | 'hard' | 'expert';
    test_cases: TestCase[];
    tags: string[];                // ['multi-step', 'api-calls', 'error-handling']
  }
  ```

- [ ] **Model Release Date Tracking**
  ```typescript
  interface Model {
    id: string;
    name: string;
    provider: string;
    release_date: string;          // ISO 8601 (YYYY-MM-DD)
    training_cutoff_date: string;  // Last date of training data (if known)
  }
  ```

- [ ] **Contamination Detection Logic**
  ```typescript
  function isContaminated(model: Model, problem: Problem): boolean {
    return problem.release_date < model.training_cutoff_date;
  }
  ```

- [ ] **Time-Window Filter UI**
  - [ ] Dual-slider component (React: `react-slider`, `rc-slider`)
  - [ ] Display "X problems selected in current time window"
  - [ ] Persist filter state in URL query params (`?start=2025-01-01&end=2025-12-31`)

### Phase 2: Difficulty Tiers (Sprint 3-4)

- [ ] **Elo Rating System**
  - [ ] Implement Bayesian MAP estimator (Python: `trueskill`, `glicko2`)
  - [ ] Update problem Elo after each evaluation (win/loss)
  - [ ] Recalculate difficulty tiers monthly (Easy ≤1500, Medium 1500-2000, Hard 2000-2500, Expert >2500)

- [ ] **Percentile Mapping**
  - [ ] Calculate percentile from Elo distribution (z-score)
  - [ ] Map to human-readable ranks ("Expert", "Grandmaster", etc.)
  - [ ] Display percentile in leaderboard table

- [ ] **Difficulty Tier Filter**
  - [ ] Checkbox UI (Easy/Medium/Hard/Expert)
  - [ ] Show problem count per tier
  - [ ] Update leaderboard dynamically

### Phase 3: Leaderboard UI/UX (Sprint 5-6)

- [ ] **Table Component**
  - [ ] Responsive table (React: `react-table`, `tanstack-table`)
  - [ ] Sticky header with category labels
  - [ ] Alternating row backgrounds (zebra striping)
  - [ ] Gradient rank badges (🥇🥈🥉 for top 3)
  - [ ] Red highlighting for contaminated models

- [ ] **Filtering**
  - [ ] Time-window slider (Phase 1)
  - [ ] Difficulty tier checkboxes (Phase 2)
  - [ ] Category multi-select (Code Gen, Debugging, etc.)
  - [ ] Tool usage toggle (No Tools / Tools Allowed / Both)
  - [ ] Provider multi-select (Anthropic, OpenAI, etc.)

- [ ] **Sorting**
  - [ ] Overall Score ↓/↑
  - [ ] Elo Rating ↓/↑
  - [ ] Model Size ↓/↑
  - [ ] Release Date ↓/↑

- [ ] **Visualization**
  - [ ] Radar chart (React: `recharts`, `nivo`)
  - [ ] Difficulty curve chart (Pass@1 vs. Elo)
  - [ ] Cost-performance scatter plot (Elo vs. $/1M tokens)
  - [ ] Latency-performance scatter plot (Elo vs. avg response time)

- [ ] **Comparison**
  - [ ] Head-to-head comparison (select 2 models)
  - [ ] Category breakdown table
  - [ ] Elo gap visualization

### Phase 4: API Access (Sprint 7-8)

- [ ] **HuggingFace Datasets**
  - [ ] Upload problem sets (`aibaas/code_generation`, etc.)
  - [ ] Upload leaderboard data (`aibaas/leaderboard`)
  - [ ] Add Croissant metadata (schema, fields)
  - [ ] Document Python usage (`load_dataset("aibaas/...")`)

- [ ] **REST API**
  - [ ] `GET /api/v1/leaderboard` (filtered queries)
  - [ ] `GET /api/v1/problems` (filtered problem sets)
  - [ ] `GET /api/v1/models` (model metadata)
  - [ ] `POST /api/v1/evaluate` (submit model run)
  - [ ] OpenAPI spec (Swagger docs)

- [ ] **CSV Exports**
  - [ ] `all_groups.csv` (category breakdown)
  - [ ] `all_tasks.csv` (task-level results)
  - [ ] Download buttons on leaderboard

### Phase 5: Monthly Updates (Sprint 9+)

- [ ] **Automated Collection**
  - [ ] GitHub Actions workflow (runs 1st of each month)
  - [ ] Scrape new problems from sources (LeetCode, Codeforces, GitHub, ArXiv)
  - [ ] Validate problem quality (remove ambiguous, add test cases)
  - [ ] Assign initial Elo (1500) and tags

- [ ] **Evaluation Pipeline**
  - [ ] Run existing models on new problems
  - [ ] Update Elo ratings (Bayesian MAP)
  - [ ] Recalculate difficulty tiers
  - [ ] Update leaderboard rankings

- [ ] **Release Process**
  - [ ] Version snapshot (`aibaas_v2025_12`)
  - [ ] Upload to HuggingFace datasets
  - [ ] Update REST API (`/api/v1/leaderboard?version=v2025_12`)
  - [ ] Announce on blog, Twitter, Discord

---

**End of Document**
