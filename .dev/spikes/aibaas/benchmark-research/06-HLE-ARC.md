# Advanced Reasoning Benchmarks: HLE & ARC-AGI

**Research Date**: 2024-11-01
**Author**: AI Research Team
**Status**: Complete
**Purpose**: Analyze two cutting-edge reasoning benchmarks (Humanity's Last Exam and ARC Prize) to identify transferable insights for developer reasoning benchmarks

---

## Executive Summary

Both **Humanity's Last Exam (HLE)** and **ARC-AGI** represent the frontier of AI reasoning evaluation, but they approach the problem from fundamentally different angles:

- **HLE**: Tests **breadth of knowledge** + **depth of reasoning** across academic domains (PhD-level difficulty)
- **ARC-AGI**: Tests **pure abstract reasoning** and **generalization** with minimal prior knowledge requirements

**Key Finding**: Current frontier models excel at knowledge retrieval but struggle with novel reasoning. HLE scores: 10-25%, ARC-AGI-1 scores: 55% (best competition entry), ARC-AGI-2 scores: 1-4% (even for advanced reasoning models).

**Code Reasoning Implication**: Developer benchmarks should test **architectural abstraction** and **pattern composition** (ARC-style), not just **API knowledge recall** (HLE-style).

---

## Part 1: Humanity's Last Exam (HLE)

### 1.1 What Makes Questions "PhD-Level"?

**Definition**: Questions designed to be the "last academic exam of its kind for AI" - challenging enough that:
- A typical undergrad **couldn't answer** the question
- A typical undergrad **couldn't even understand what was being asked**

**Difficulty Validation Process**:
1. Over 70,000 submissions received from subject experts
2. 13,000 passed initial difficulty screening (**must stump frontier LLMs**)
3. Human expert reviewers with graduate degrees scored against standardized rubrics
4. Community bug bounty program removed flagged errors
5. Searchable questions manually audited and removed (prevent memorization)

**Examples**:

#### Example 1: Ecology (Biology/Medicine)
```
Question: "Hummingbirds within Apodiformes uniquely have a bilaterally paired
oval bone, a sesamoid embedded in the caudolateral portion of the expanded,
cruciate aponeurosis of insertion of m. depressor caudae. How many paired
tendons are supported by this sesamoid bone? Answer with a number."

Characteristics:
- Requires deep anatomical knowledge (muscle insertion anatomy)
- Technical terminology that undergrads wouldn't recognize
- Numerical precision required (no partial credit)
```

#### Example 2: Chemistry (Organic Mechanisms)
```
Question: "The reaction shown is a thermal pericyclic cascade that converts
the starting heptaene into endiandric acid B methyl ester. The cascade involves
three steps: two electrocyclizations followed by a cycloaddition. What types of
electrocyclizations are involved in step 1 and step 2, and what type of
cycloaddition is involved in step 3?"

Characteristics:
- Requires advanced organic chemistry (pericyclic reactions)
- Multi-step reasoning (analyze cascade mechanism)
- Domain-specific vocabulary ("heptaene", "electrocyclization")
```

#### Example 3: Ancient Languages (Humanities)
```
Question: [Image of Roman inscription with Palmyrene script]
"Translate the following inscription..."

Characteristics:
- Multimodal input (14% of HLE is multimodal)
- Requires specialized linguistic knowledge
- Cultural/historical context required
```

**What Makes It "PhD-Level"**:
- **Technical Precision**: Questions use domain-specific jargon that requires years of study
- **Multi-Step Reasoning**: Not just recall - requires chaining concepts together
- **Novel Problem-Solving**: Cannot be solved via pattern matching against training data
- **Cross-Domain Knowledge**: Sometimes requires bridging multiple fields

---

### 1.2 Scoring Methodology

**Evaluation Process**:
1. Models provide final answers + **confidence estimates** (0-100% certainty)
2. **Automatic judging**: `o3-mini-2025-01-31` compares responses to ground truth
3. **Statistical significance ranking**: Model's rank = 1 + count of models whose lower CI bound exceeds its upper CI bound
4. Temperature set to **0.0** (deterministic, no randomness)

**Metrics Reported**:
- **Accuracy**: Percentage of correct answers
- **RMS Calibration Error**: How well confidence estimates match actual correctness
  - All measured models show **80%+ calibration error** with **<10% accuracy**
  - This means models are overconfident in wrong answers

**Question Format Distribution**:
- **24% Multiple Choice**: Limited to 4-5 options
- **76% Open-Ended**: Free-form numerical/text answers
- **14% Multimodal**: Requires diagram comprehension

**Anti-Overfitting Measures**:
- Private held-out question set exists to detect overfitting to public questions
- Questions searchable via Google/arXiv were manually removed

---

### 1.3 Question Sources & Validation

**Contributor Network**:
- **Nearly 1,000 subject experts** from:
  - **500+ academic institutions** (universities, research labs)
  - **50+ countries** (global diversity)
- Contributors must have **graduate degrees** (MA/MS/PhD) in their domain

**Quality Assurance Pipeline**:
```
70,000 submissions
    ↓ (Difficulty filter: must stump frontier LLMs)
13,000 passed screening
    ↓ (Expert review: standardized rubrics)
2,500 final questions
    ↓ (Bug bounty + searchability audit)
Final benchmark release
```

**Domain Coverage** (2,500 questions):
- **Mathematics**: 41% (largest category - proofs, theorems, advanced calculus)
- **Computer Science/AI**: 10%
- **Physics**: 9%
- **Biology/Medicine**: 11%
- **Humanities/Social Science**: 9%
- **Chemistry**: 7%
- **Engineering**: 4%
- **Other**: 9%

**Validation Criteria**:
1. **Difficulty**: Must stump GPT-4, Claude 3.5, Gemini Pro
2. **Unambiguity**: Single correct answer with clear grading rubric
3. **Non-Searchable**: Cannot be found via Google/academic search
4. **Expertise Required**: Requires domain knowledge beyond Wikipedia/textbooks

---

### 1.4 Difficulty Distribution

**Key Finding**: HLE is **uniformly challenging**, not tiered.

**Evidence**:
- All models perform poorly: **<10% accuracy** across the board
- No "easy" vs "hard" subsets - entire benchmark is frontier-level
- Calibration errors exceed **80%** for all models (severe overconfidence)

**Rationale**:
- Goal is to measure **ceiling of current AI capabilities**, not create a skill ladder
- Questions that become "easy" over time are candidates for removal (benchmark staleness)

**Comparison to Other Benchmarks**:
- **MMLU** (Massive Multitask Language Understanding): 57 domains, high school to college level → **saturated** (frontier models >85%)
- **GPQA** (Graduate-Level Google-Proof Q&A): PhD-level, Google-proof → **saturated** (frontier models >60%)
- **HLE**: PhD+ level, expert-validated → **unsaturated** (frontier models <25%)

---

### 1.5 Top Performers (Current Leaderboard)

**As of 2024-11-01**:

| Rank | Model | Accuracy | RMS Calibration Error | Notes |
|------|-------|----------|----------------------|-------|
| 1 | **GPT-5** (2025-08-07) | **25.32%** | 82.4% | Latest OpenAI reasoning model |
| 2 | **Gemini 2.5 Pro Preview** | **21.64%** | 83.1% | Google's advanced reasoning |
| 3 | **O3 (high)** | **20.32%** | 84.7% | OpenAI's extended reasoning (high compute) |
| 4 | **GPT-5 Mini** | **19.44%** | 81.9% | Smaller version of GPT-5 |
| - | *Human Baseline* | **~60-80%** | N/A | Estimated based on expert performance |

**Key Observations**:
- **No model exceeds 30%** - massive gap to human expert performance
- **Calibration is broken**: Models are 80%+ confident in wrong answers
- **Reasoning models struggle**: Even O3 (high compute) only reaches 20%
- **Multimodal advantage unclear**: No published breakdown showing if multimodal questions hurt scores more

**Performance Trends**:
- Models with **extended reasoning** (O3, GPT-5) do NOT significantly outperform standard models
- **Scaling compute** (O3 low → O3 high) yields marginal gains (~5%)
- **Knowledge retrieval** != **reasoning ability** (models memorize patterns but can't reason through novel problems)

---

### 1.6 HLE Summary

**What HLE Tests**:
- **Depth of knowledge**: PhD-level domain expertise across 9+ fields
- **Multi-step reasoning**: Chaining concepts from first principles
- **Multimodal understanding**: Interpreting diagrams, equations, visual data
- **Precision**: Exact answers required (no partial credit)

**Why LLMs Struggle**:
- **Knowledge gaps**: Training data lacks esoteric academic content
- **Reasoning limitations**: Can retrieve patterns but can't construct novel proofs/derivations
- **Overconfidence**: Models don't know what they don't know (poor calibration)
- **Multimodal bottleneck**: Visual reasoning still weak (14% multimodal questions)

**Benchmark Design Strengths**:
- **Resistant to memorization**: Google-proof, manually audited for searchability
- **Expert-validated**: 1,000 PhDs contributing ensures quality
- **Broad coverage**: 9+ domains prevents overfitting to specific fields
- **Calibration tracking**: Exposes overconfidence issue

**Limitations**:
- **Domain bias**: 41% math-heavy (may favor models trained on arXiv papers)
- **Static benchmark**: No ongoing question refresh mechanism mentioned
- **Access restrictions**: Private held-out set exists but not described
- **Human baseline unclear**: No systematic human testing reported

---

---

## Part 2: ARC Prize (ARC-AGI Benchmark)

### 2.1 Puzzle Structure: What is ARC-AGI?

**Full Name**: **Abstraction and Reasoning Corpus for Artificial General Intelligence**

**Created By**: François Chollet (Google, creator of Keras)

**Core Concept**: A benchmark that tests **pure reasoning** and **generalization** - the ability to learn and apply new concepts from minimal examples, similar to human "fluid intelligence" (Raven's Progressive Matrices).

**Format**: Grid-based visual puzzles stored as JSON

```json
{
  "train": [
    {
      "input": [[0, 0, 1], [1, 0, 0], [0, 1, 0]],
      "output": [[1, 1, 0], [0, 1, 1], [1, 0, 1]]
    },
    // 2-4 more training examples
  ],
  "test": [
    {
      "input": [[0, 1, 0], [0, 0, 1], [1, 0, 0]],
      "output": [[?, ?, ?], [?, ?, ?], [?, ?, ?]]  // Solver must predict
    }
  ]
}
```

**Grid Characteristics**:
- **Dimensions**: 1x1 to 30x30 cells (variable size)
- **Symbols**: Integers 0-9 (visualized as colors: black, blue, red, green, yellow, etc.)
- **Training examples**: Typically 3 input/output pairs demonstrating the transformation rule
- **Test examples**: 1-3 puzzles where solver must predict output
- **Attempts allowed**: 3 trials per test input

**Example Puzzle Type** (conceptual):
```
Training Example 1:
Input:  [0, 1, 0]    Output: [0, 0, 0]
        [1, 1, 1]            [1, 1, 1]
        [0, 1, 0]            [0, 0, 0]

Training Example 2:
Input:  [1, 0, 1]    Output: [1, 1, 1]
        [0, 0, 0]            [0, 0, 0]
        [1, 0, 1]            [1, 1, 1]

Rule to infer: "Keep horizontal lines, turn vertical lines into blanks"

Test Input:
        [1, 1, 0]
        [1, 0, 1]    → Solver must apply discovered rule
        [0, 1, 1]
```

**Puzzle Manipulation Tools** (for human solvers):
- Grid resizing and copying
- Cell-by-cell editing with color selection (0-9 palette)
- Rectangular selection and batch coloring
- Copy/paste operations between grids
- Floodfill operations (paint bucket tool)

These tools suggest tasks involve:
- **Spatial transformations**: Rotation, reflection, translation
- **Pattern completion**: Filling missing parts based on symmetry/rules
- **Object manipulation**: Moving, copying, or transforming colored regions
- **Rule composition**: Applying multiple transformations in sequence

---

### 2.2 Evaluation Method

**Success Criteria**:
> "A test-taker is said to solve a task when they are able to produce the **correct output grid for ALL test inputs**"

**Scoring**:
- **Binary**: Task is either solved (100%) or failed (0%) - **no partial credit**
- **Exact match required**: Every cell must match the ground truth output
- **Attempts allowed**: 3 trials per test input (can refine answer if first attempt wrong)

**Performance Calculation**:
```
Accuracy = (Tasks solved correctly) / (Total tasks attempted)
```

**Why No Partial Credit?**:
- Tests **understanding of the rule**, not guessing
- If solver truly grasped the transformation, they should get ALL test cases right
- Prevents "lucky guesses" from inflating scores
- Mirrors real-world problem-solving: partial understanding often leads to complete failure

**Evaluation Process**:
1. Solver receives **training pairs** (input + output examples)
2. Solver analyzes patterns to infer the **transformation rule**
3. Solver receives **test inputs** (output hidden)
4. Solver produces **predicted outputs** (up to 3 attempts per input)
5. System compares predictions to ground truth (**exact match** required)
6. Task marked as solved only if **all test inputs** are correct

**Grid Dimension Challenge**:
- Solver must **determine correct output dimensions** (not always same as input)
- Many tasks require resizing grid (e.g., 3x3 input → 5x5 output)
- Adds extra difficulty: wrong dimensions = automatic failure

---

### 2.3 Prize Competition

**Total Prize Pool**: **$1M+** (distributed across multiple categories)

**Prize Breakdown**:
- **Grand Prize**: $700,000 (top-performing publicly released solution)
- **Paper Awards**: $75,000 (best research papers advancing ARC-AGI)
- **Top Scores**: $50,000 (leaderboard bonuses for high scorers)
- **To Be Announced**: $175,000 (future competitions/categories)

**Competition Structure**:

#### ARC Prize 2024 (Completed)
- **Platform**: Kaggle competition
- **Duration**: Multiple months (2024)
- **Winner**: "the ARChitects" team - **53.5%** accuracy
- **Highest Score**: MindsAI - **55.5%** (ineligible - didn't open-source solution)
- **Progress**: Improved SOTA from **33% → 55.5%**
- **Compute Budget**: Not restricted (teams used various approaches)

#### ARC Prize 2025 (Ongoing)
- **Platform**: Kaggle
- **Duration**: March 26 - November 3, 2025
- **New Benchmark**: ARC-AGI-2 (much harder than ARC-AGI-1)
- **Compute Constraint**: $50 compute budget for 120 evaluation tasks (~$0.42/task)
- **Efficiency Focus**: "True intelligence isn't just about solving problems, but solving them efficiently with minimal resources"
- **Leaderboard Rule**: Only systems requiring <$10,000 total compute are displayed
  - **Notable exclusion**: O3 (high compute) used $20,000/task → not on leaderboard

**Prize Philosophy**:
- Incentivize **open-source solutions** (MindsAI disqualified for not sharing code)
- Reward **efficiency** (low compute budgets force algorithmic innovation)
- Encourage **research publication** (paper awards for novel approaches)
- Align with **AGI mission**: "Guide researchers, industry, and regulators towards artificial general intelligence"

**Why $1M Prize?**:
- Solving ARC-AGI is considered a **proxy for AGI progress**
- Current AI systems excel at **memorization** but fail at **generalization**
- Prize attracts top researchers to work on fundamental reasoning
- François Chollet (creator): "AGI remains unsolved. New ideas still needed."

**Competition Impact**:
- **2024**: 20+ point improvement in SOTA (33% → 55.5%)
- **2025**: Focus shifted to ARC-AGI-2 (much harder - even O3 scores only 4-15%)
- **Research output**: Multiple papers published on novel reasoning approaches

---

### 2.4 Human Performance

**Key Finding**: Human performance varies widely depending on:
- Which dataset (training vs evaluation)
- Number of attempts allowed
- Selection of tasks (random vs cherry-picked)

**ARC-AGI-1 Human Baselines**:

| Study | Dataset | Accuracy | Notes |
|-------|---------|----------|-------|
| **Original ARC Paper** | Training set (40 tasks, semi-random) | **83.8%** | Small sample, possible selection bias |
| **H-ARC Study (2024)** | All 400 training tasks (1st attempt) | **59.9%** | Most comprehensive human test |
| **H-ARC Study (2024)** | All 400 training tasks (2nd attempt) | **72.6%** | Humans improve significantly with retry |
| **Original ARC Paper** | Public training set average | **76.2%** | Reported baseline |
| **Original ARC Paper** | Public evaluation set average | **64.2%** | Harder than training set |

**ARC-AGI-2 Human Baselines** (2025):

| Metric | Performance | Notes |
|--------|-------------|-------|
| **Average test-taker score** | **60%** | Significantly harder than ARC-AGI-1 |
| **Task solving rate (attempted)** | **66%** | Humans solve 66% of tasks they try |
| **Tasks solvable by humans** | **100%** | All tasks solved by ≥2 people in ≤2 attempts |

**Key Insights**:

1. **Humans are NOT perfect**: Even on designed-for-humans puzzles, experts average 60-85%
2. **Retry improves performance**: 59.9% (1st attempt) → 72.6% (2nd attempt) = +21% gain
3. **Evaluation set harder**: 76.2% (training) vs 64.2% (evaluation) = puzzles get harder
4. **ARC-AGI-2 is brutal**: 60% human average (vs 72-84% on ARC-AGI-1) = major difficulty jump

**Expert vs General Population**:
- **PhD graduates**: Higher accuracy on complex tasks (no exact % reported)
- **PhD students**: Moderate performance
- **General public**: Lower accuracy, more time required
- **Spatial reasoning matters**: People with strong visual-spatial skills perform better

**Human Solving Strategies** (observed):
- **Pattern matching**: Look for symmetries, repetitions, transformations
- **Hypothesis testing**: Try a rule on training examples, refine if wrong
- **Decomposition**: Break complex transformations into simpler sub-rules
- **Visual intuition**: "See" the pattern without explicit verbalization
- **Trial and error**: 3 attempts allow refinement of understanding

**Human vs AI Gap**:
- **ARC-AGI-1**: Humans ~75%, Best AI ~55% = 20-point gap (closing)
- **ARC-AGI-2**: Humans ~60%, Best AI ~4% = 56-point gap (widening!)
- **Implication**: ARC-AGI-2 successfully "reset" the benchmark to prevent saturation

---

### 2.5 AI Challenges: Why LLMs Struggle

**Core Problem**: LLMs excel at **pattern matching** (retrieving learned patterns from training data) but fail at **pattern discovery** (inferring new rules from minimal examples).

**Primary Failure Modes**:

#### 1. **Training on Text vs. Visual Reasoning**
- **LLM Strength**: 1D sequence processing (tokens, text, code)
- **ARC Requirement**: 2D spatial reasoning (grids, transformations, geometric patterns)
- **Gap**: Models trained on text lack innate visual-spatial representations
- **Evidence**: LLMs struggle with tasks requiring 2D manipulation (rotation, reflection, object movement)

#### 2. **Pattern Matching vs. True Abstraction**
- **LLM Behavior**: "Stochastic parrots" - repeat patterns from massive training data
- **ARC Requirement**: Infer **novel rules** from 3 examples (not in training data)
- **Gap**: No amount of scaling fixes this - GPT-4, Claude 3.5 still score near 0% on ARC-AGI-2
- **François Chollet**: "LLMs became good at repeating patterns...but failed when faced with genuinely new problems"

#### 3. **Scaling Complexity Failure**
- **Small puzzles**: LLMs achieve **>50% accuracy** on simple 3x3 grids
- **Large puzzles**: Accuracy drops by **42.7% on average**, up to **84% loss** on complex tasks
- **Reason**: Larger grids = 100x longer token sequences → reasoning breaks down
- **Implication**: Models lack robust spatial representations (rely on token proximity, not true geometric understanding)

#### 4. **2D Manipulation Bottleneck**
- **LLMs excel at**: Inpainting (fill missing cells), denoising (fix errors), small grids
- **LLMs fail at**: Rotation, reflection, translation, object grouping, spatial relationships
- **Reason**: 1D autoregressive processing (predict next token) doesn't naturally encode 2D geometry
- **Workaround attempts**: Encode grids as text (row-by-row) → loses spatial structure

#### 5. **Lack of Compositional Reasoning**
- **ARC tasks require**: Applying **multiple rules simultaneously** that interact
  - Example: "Rotate clockwise AND fill symmetrically AND copy to corners"
- **LLMs struggle**: Can't chain transformations or handle rule interactions
- **Human advantage**: Can decompose complex transformations into sub-steps mentally

#### 6. **Context-Sensitive Rule Application**
- **ARC tasks require**: Rules that change based on **context** (grid state, object properties)
  - Example: "If object is blue, mirror it; if red, rotate it"
- **LLMs struggle**: Rigid pattern application, poor conditional logic in visual domain
- **Human advantage**: Flexible mental simulation ("what if I try this rule?")

**Performance Gap Summary**:

| Model | ARC-AGI-1 | ARC-AGI-2 | Notes |
|-------|-----------|-----------|-------|
| **GPT-4.5 / Claude 3.7** | ~5-10% | **0%** | Standard LLMs completely fail |
| **O1-Pro (OpenAI Reasoning)** | ~15-20% | **1%** | Extended reasoning barely helps |
| **DeepSeek R1 (Reasoning)** | ~10-15% | **1.3%** | Reasoning models still fail |
| **O3-Low (OpenAI, $200/task)** | **75.7%** | **4%** | Massive compute, marginal ARC-AGI-2 gain |
| **O3-High (OpenAI, $20k/task)** | **87.5%** | **15-20%** (est) | First to beat human baseline on v1, but still fails v2 |
| **MindsAI (2024 winner)** | **55.5%** | N/A | Non-reasoning approach (likely hybrid symbolic) |
| **Humans** | **~75%** | **~60%** | Still far ahead on ARC-AGI-2 |

**Why O3 Breaks ARC-AGI-1 (But Not ARC-AGI-2)**:
- **Extended reasoning**: Generates thousands of candidate solutions, tests against training examples
- **Test-time compute**: Spends $20,000/task on brute-force search
- **Limitation**: ARC-AGI-2 designed to resist brute force → O3 collapses to 4-15%

**Key Insight**:
> "Reasoning AI models like O1-Pro and DeepSeek R1 score between 1% and 1.3% on ARC-AGI-2, while human panels got 60% of the test's questions right."

This is a **~60-point gap** - the largest human-AI reasoning gap in any modern benchmark.

**What Capabilities ARC Tests (That LLMs Lack)**:

1. **Symbolic interpretation**: Understanding that grid symbols represent abstract concepts
2. **Compositional reasoning**: Applying multiple interacting rules
3. **Context-sensitive rules**: Adapting behavior based on grid state
4. **Spatial reasoning**: True 2D geometric understanding (not text encoding of grids)
5. **Few-shot generalization**: Learning from 3 examples (not billions of training samples)
6. **Novelty handling**: Solving problems never seen before (not pattern retrieval)

**Bottom Line**: ARC-AGI exposes that current AI reasoning is **shallow** - models memorize vast amounts of data but cannot **think abstractly** or **generalize** like humans.

---

### 2.6 ARC-AGI Summary

**What ARC Tests**:
- **Pure abstract reasoning**: No domain knowledge required (unlike HLE)
- **Few-shot generalization**: Learn from 3 examples, apply to novel cases
- **Spatial reasoning**: 2D geometric transformations and pattern discovery
- **Compositional thinking**: Combining multiple rules that interact
- **Fluid intelligence**: Cognitive flexibility to handle novel problems

**Why LLMs Fail**:
- **1D processing**: Text-trained models lack true 2D spatial understanding
- **Pattern matching**: Retrieve learned patterns, can't discover new rules
- **Scaling limitations**: Large grids (100x longer sequences) break reasoning
- **No compositional reasoning**: Can't chain or interact multiple transformations
- **Brute force resistance** (ARC-AGI-2): Even $20k/task compute fails

**Benchmark Design Strengths**:
- **Resistant to memorization**: 800 unique tasks, procedurally generated rules
- **Pure reasoning test**: No domain knowledge gives LLMs unfair advantage
- **Human baseline**: All tasks solvable by humans → measures AI-human gap
- **Evolving difficulty**: ARC-AGI-2 "resets" benchmark when v1 saturates
- **Efficiency focus**: $10k compute limit forces algorithmic innovation

**Limitations**:
- **Visual bias**: May favor humans with strong spatial reasoning skills
- **Small dataset**: 800 tasks (vs 2,500 for HLE) → easier to overfit
- **Binary scoring**: No credit for "partial understanding" of rules
- **Tool dependency**: Human interface (grid editor) may advantage certain strategies

**ARC Prize Impact**:
- **Research catalyst**: $1M prize drives novel reasoning approaches
- **AGI proxy**: Solving ARC = major step toward general intelligence
- **Benchmark evolution**: ARC-AGI-2 shows commitment to staying ahead of AI progress

---

---

## Part 3: Comparative Analysis

### 3.1 Both Test Abstract Reasoning - What's Different?

| Dimension | Humanity's Last Exam (HLE) | ARC-AGI |
|-----------|----------------------------|---------|
| **Reasoning Type** | **Knowledge-grounded reasoning** (apply domain expertise to novel problems) | **Pure abstraction** (discover patterns from scratch, no prior knowledge) |
| **Difficulty Source** | **Depth of knowledge** (PhD-level domain expertise) + multi-step reasoning | **Generalization gap** (infer rules from 3 examples, apply to unseen cases) |
| **Prior Knowledge** | **Essential** (organic chemistry, advanced math, ancient languages) | **Forbidden** (domain knowledge gives no advantage) |
| **Evaluation** | **Mixed** (76% open-ended, 24% multiple choice, partial credit via judging) | **Binary** (exact match required, no partial credit) |
| **Question Count** | **2,500 questions** across 9+ domains | **800 tasks** (400 training, 400 evaluation) |
| **Modality** | **86% text, 14% multimodal** (diagrams, equations) | **100% visual** (grid-based puzzles) |
| **Human Baseline** | **~60-80%** (estimated, varies by domain) | **~60-75%** (measured across studies) |
| **Best AI Performance** | **25.3%** (GPT-5, 2025) | **55.5%** (MindsAI, ARC-AGI-1) / **4%** (O3-Low, ARC-AGI-2) |
| **AI-Human Gap** | **35-55 points** (large but closing) | **20 points** (ARC-AGI-1) / **56 points** (ARC-AGI-2) |
| **What LLMs Lack** | **Domain expertise** + **multi-step reasoning** + **calibration** | **True abstraction** + **2D spatial reasoning** + **few-shot generalization** |
| **Saturation Risk** | **Low** (frontier models <30%) | **ARC-AGI-1**: Moderate (O3 at 87.5%) / **ARC-AGI-2**: Very low (O3 at 4%) |

**Key Differences**:

#### 1. **Knowledge vs. Abstraction**
- **HLE**: Tests if AI can **reason within a domain** (given PhD-level knowledge)
  - Example: "What type of electrocyclization occurs in step 2?" → requires knowing organic mechanisms
- **ARC**: Tests if AI can **learn a domain from scratch** (3 examples, no prior knowledge)
  - Example: Given 3 grid transformations, infer the rule → pure pattern discovery

#### 2. **Breadth vs. Depth**
- **HLE**: **Breadth across domains** (9 fields) + **depth in each** (PhD-level)
- **ARC**: **Depth in one skill** (abstract reasoning) with **no domain switching**

#### 3. **Scaling Behavior**
- **HLE**: Models improve with **knowledge scaling** (larger training data, more arXiv papers)
  - GPT-3 → GPT-4 → GPT-5 shows steady progress (5-10% per generation)
- **ARC**: Models **do NOT improve** with scale (GPT-4.5 scores ~0% on ARC-AGI-2)
  - Requires **algorithmic breakthroughs**, not just more data/compute

#### 4. **Failure Modes**
- **HLE Failures**:
  - **Knowledge gaps** (model hasn't memorized esoteric concepts)
  - **Multi-step reasoning** (can't chain complex derivations)
  - **Overconfidence** (80%+ calibration error)
- **ARC Failures**:
  - **No true abstraction** (pattern matching, not rule discovery)
  - **2D reasoning deficit** (1D text processing doesn't transfer)
  - **Scaling collapse** (large grids break reasoning)

#### 5. **Benchmark Philosophy**
- **HLE**: "The last academic exam AI will take" → test limits of current paradigms
- **ARC**: "A benchmark for AGI" → test fundamental reasoning, not knowledge

**Complementarity**:
- **HLE** measures: "How much does AI know?" + "Can it reason with that knowledge?"
- **ARC** measures: "Can AI think abstractly?" + "Can it learn new concepts?"

Both are needed to evaluate AI progress toward human-level intelligence:
- HLE → Expert-level performance in specialized domains
- ARC → General-purpose reasoning applicable to any domain

---

### 3.2 Applicability to Code Reasoning

**Core Question**: How do HLE and ARC insights transfer to evaluating **developer reasoning** (design patterns, architecture decisions, refactoring)?

**Code Reasoning = HLE-Style or ARC-Style?**

#### **Code Reasoning is BOTH**:

1. **HLE-Style (Knowledge-Grounded)**:
   - **Design Patterns**: Requires knowing 23 GoF patterns, SOLID principles, DDD concepts
   - **Framework APIs**: "How do you implement authentication in FastAPI?" → domain expertise
   - **Performance Optimization**: "Why is this React component re-rendering?" → React knowledge
   - **Analogy**: Like chemistry PhD knowing "what type of electrocyclization occurs in step 2?"

2. **ARC-Style (Abstract Pattern Discovery)**:
   - **Architecture Decisions**: Given codebase structure, infer the architectural pattern (layered? microservices?)
   - **Refactoring Opportunities**: Spot code smells from a few examples, generalize to entire codebase
   - **Debugging**: Given 3 failing tests, infer the underlying bug (pattern discovery from minimal examples)
   - **Analogy**: Like ARC puzzles - "given 3 examples, discover the transformation rule"

**Key Insight**: **Code reasoning benchmarks should test BOTH**:
- **Knowledge recall** (HLE-style): "What design pattern is this?" (multiple choice)
- **Pattern abstraction** (ARC-style): "Given these 3 code snippets, what's the refactoring rule?" (novel generalization)

**Current Code Benchmarks (Gaps)**:

| Benchmark | What It Tests | HLE/ARC Style | Gap |
|-----------|---------------|---------------|-----|
| **HumanEval** | Code generation from docstrings | HLE (API knowledge) | No reasoning, just generation |
| **MBPP** | Python programming problems | HLE (algorithm knowledge) | No architecture/design patterns |
| **CodeContests** | Competitive programming | HLE (CS theory) | No real-world code reasoning |
| **Do Code LLMs Understand Design Patterns?** | Pattern recognition in code | HLE (pattern recall) | No abstraction/composition |

**Missing**: **Code Architecture Reasoning Corpus (CARC)** - an ARC-style benchmark for code

---

### 3.3 Could We Create "Code Architecture Puzzles" Inspired by ARC?

**Proposal**: **CARC (Code Architecture Reasoning Corpus)**

**Concept**: Visual/structural puzzles for code, inspired by ARC's few-shot abstraction tasks.

**Example Puzzle Types**:

#### **Puzzle Type 1: Refactoring Pattern Discovery**

**Format** (inspired by ARC's grid transformations):
```
Training Example 1:
Before:                          After:
┌─────────────────┐             ┌─────────────────┐
│ if (x > 0) {    │             │ return x > 0    │
│   return true;  │    ───→     │   ? calculate() │
│ } else {        │             │   : 0;          │
│   return false; │             │                 │
│ }               │             │                 │
└─────────────────┘             └─────────────────┘

Training Example 2:
Before:                          After:
┌─────────────────┐             ┌─────────────────┐
│ if (status == X)│             │ return status==X│
│   doA();        │    ───→     │   ? doA()       │
│ else            │             │   : doB();      │
│   doB();        │             │                 │
└─────────────────┘             └─────────────────┘

Test Case (solver must apply rule):
Before:
┌─────────────────┐
│ if (user) {     │
│   login();      │    ───→     ???
│ } else {        │
│   logout();     │
│ }               │
└─────────────────┘

Answer: return user ? login() : logout();
```

**What This Tests**:
- **Pattern abstraction**: Infer "if-else → ternary" refactoring rule from 2 examples
- **Compositional reasoning**: Apply rule to novel code structure
- **Few-shot generalization**: Learn from minimal examples (like ARC's 3 examples)

---

#### **Puzzle Type 2: Architecture Pattern Recognition**

**Format** (visual dependency graphs):
```
Training Example 1:
Component Graph:              Pattern:
┌──────┐
│ View │──→┌──────────┐       "MVC"
└──────┘   │Controller│
      ↑    └──────────┘
      │         │
      │         ↓
┌─────┴───┐  ┌─────┐
│  Model  │←─┤     │
└─────────┘  └─────┘

Training Example 2:
Component Graph:              Pattern:
┌──────┐
│Client│──→┌────────┐         "Client-Server"
└──────┘   │ Server │
      ↑    └────────┘
      │         │
      └─────────┘

Test Case:
┌──────────┐
│Presenter │──→┌──────┐
└──────────┘   │ View │       ???
      ↑        └──────┘
      │
┌─────┴───┐
│  Model  │
└─────────┘

Answer: "MVP (Model-View-Presenter)"
```

**What This Tests**:
- **Structural abstraction**: Recognize architecture from component relationships
- **Visual reasoning**: Understand dependency graphs (like ARC's 2D grids)
- **Transfer learning**: Apply pattern knowledge to novel structures

---

#### **Puzzle Type 3: Code Smell Detection & Fixing**

**Format** (similar to ARC's "spot the pattern, fix the grid"):
```
Training Example 1:
Bad Code:                       Good Code:
┌──────────────────┐           ┌──────────────────┐
│ class God {      │           │ class User {     │
│   login() {...}  │   ───→    │   login() {...}  │
│   logout() {...} │           │ }                │
│   sendEmail()    │           │ class Email {    │
│   calcTax()      │           │   send() {...}   │
│   renderUI()     │           │ }                │
│ }                │           │ class Tax {      │
└──────────────────┘           │   calc() {...}   │
                               └──────────────────┘

Training Example 2:
Bad Code:                       Good Code:
┌──────────────────┐           ┌──────────────────┐
│ x = data[0]      │           │ [user, id, name] │
│ y = data[1]      │   ───→    │   = data         │
│ z = data[2]      │           │                  │
└──────────────────┘           └──────────────────┘

Test Case:
Bad Code:
┌──────────────────┐
│ class Manager {  │
│   hire()         │   ───→    ???
│   fire()         │
│   payroll()      │
│   schedule()     │
│   review()       │
└──────────────────┘

Answer: Split into HRManager, PayrollManager, ScheduleManager (SRP violation fix)
```

**What This Tests**:
- **Smell detection**: Recognize "God Object" anti-pattern from examples
- **Refactoring reasoning**: Infer the fix (split into cohesive classes)
- **Generalization**: Apply SOLID principles to novel code

---

#### **Puzzle Type 4: Dependency Injection Pattern**

**Format** (transformation puzzles):
```
Training Example 1:
Before:                         After:
┌──────────────────┐           ┌──────────────────┐
│ class Service {  │           │ class Service {  │
│   db = new DB()  │   ───→    │   constructor(   │
│   run() {        │           │     db: IDB      │
│     db.query()   │           │   ) {...}        │
│   }              │           │   run() {        │
│ }                │           │     db.query()   │
└──────────────────┘           │   }              │
                               └──────────────────┘

Test Case:
Before:
┌──────────────────┐
│ class Logger {   │
│   file = new File│   ───→    ???
│   log(msg) {     │
│     file.write() │
│   }              │
└──────────────────┘

Answer: Inject IFile dependency via constructor
```

**What This Tests**:
- **Design pattern abstraction**: Learn DI pattern from examples
- **Decoupling reasoning**: Understand why hard dependencies are bad
- **Applied knowledge**: Transfer DI to novel scenarios

---

**CARC Benchmark Properties** (Inspired by ARC):

1. **Few-Shot Learning**: 3 training examples per task (like ARC)
2. **Novel Test Cases**: Test cases never seen during training
3. **Binary Scoring**: Exact match required (no partial credit)
4. **Visual Representation**: Dependency graphs, code structure diagrams (like ARC grids)
5. **Compositional Reasoning**: Tasks require combining multiple refactoring rules
6. **Resistant to Memorization**: Procedurally generated code snippets (can't memorize solutions)

**Dataset Composition**:
- **400 Training Tasks**: For algorithm development
- **400 Evaluation Tasks**: For final assessment
- **Categories**:
  - Refactoring patterns (100 tasks)
  - Architecture recognition (100 tasks)
  - Code smell detection (100 tasks)
  - Design pattern application (100 tasks)

**Human Baseline**:
- Test with **professional developers** (5+ years experience)
- Expected baseline: **70-85%** (similar to ARC human performance)

**Why This Would Be Valuable**:

1. **Tests Real Developer Skills**:
   - Pattern recognition (not just API recall)
   - Abstraction ability (not just code generation)
   - Few-shot learning (not memorization)

2. **Exposes LLM Weaknesses**:
   - Current code LLMs often **fail to understand existing design patterns**
   - Generated code **doesn't match project style** (lack of abstraction)
   - **Can't refactor** - only generate from scratch

3. **Complements Existing Benchmarks**:
   - HumanEval/MBPP: Generation tasks (write code from scratch)
   - CARC: Reasoning tasks (understand, refactor, improve existing code)

4. **Practical Impact**:
   - Measures skills needed for **real-world development** (reading/refactoring > writing from scratch)
   - Aligns with **AI pair programming** use case (help improve code, not just generate)

---

### 3.4 Transferable Insights for Developer Reasoning Benchmarks

**From HLE**:

1. **Expert Validation is Critical**:
   - HLE used **1,000 PhDs** to validate questions
   - **CARC equivalent**: Use **senior engineers (Staff+/Principal)** to validate refactoring tasks
   - Ensures tasks represent **real expertise**, not textbook knowledge

2. **Multi-Domain Coverage**:
   - HLE spans 9+ fields (math, physics, chemistry, etc.)
   - **CARC equivalent**: Span 5+ architecture styles (MVC, microservices, event-driven, layered, hexagonal)
   - Prevents overfitting to single domain

3. **Difficulty Calibration**:
   - HLE filtered 70k submissions → 2.5k that **stump frontier LLMs**
   - **CARC equivalent**: Test tasks against GPT-4, Claude 3.5 - only keep tasks where AI scores <30%
   - Ensures benchmark stays challenging

4. **Calibration Tracking**:
   - HLE tracks **confidence estimates** (exposes overconfidence)
   - **CARC equivalent**: Ask models "How confident are you this is the right pattern?" (0-100%)
   - Reveals when models **don't know what they don't know**

5. **Google-Proof Design**:
   - HLE manually removed searchable questions
   - **CARC equivalent**: Generate code snippets procedurally (can't search for exact solutions)
   - Prevents memorization from GitHub/StackOverflow

**From ARC-AGI**:

1. **Few-Shot Generalization**:
   - ARC gives 3 examples, tests on unseen cases
   - **CARC equivalent**: Show 3 refactoring examples, test on 4th novel case
   - Measures **true understanding**, not pattern matching

2. **Binary Scoring (No Partial Credit)**:
   - ARC requires exact match on ALL test cases
   - **CARC equivalent**: Refactored code must pass all tests + match expected pattern
   - Eliminates "lucky guesses"

3. **Resistance to Brute Force**:
   - ARC-AGI-2 designed so $20k/task compute still fails
   - **CARC equivalent**: Design tasks where exhaustive search is intractable (10^6+ possible refactorings)
   - Forces **algorithmic reasoning**, not search

4. **Visual Representation**:
   - ARC uses grids (spatial reasoning, not text)
   - **CARC equivalent**: Use **dependency graphs, UML diagrams, call trees**
   - Tests **structural understanding**, not just code reading

5. **Efficiency Constraints**:
   - ARC Prize limits compute to $10k (encourages algorithmic innovation)
   - **CARC equivalent**: Limit inference time to 10 seconds/task (realistic IDE constraints)
   - Prevents "just throw more compute at it" solutions

6. **Evolving Difficulty**:
   - ARC-AGI-1 → ARC-AGI-2 when v1 saturated
   - **CARC equivalent**: Release CARC-2 when models exceed 80% on CARC-1
   - Keeps benchmark ahead of AI progress

---

**Combined HLE + ARC Insights for CARC**:

| Principle | HLE Contribution | ARC Contribution | CARC Application |
|-----------|------------------|------------------|------------------|
| **Validation** | 1,000 PhD experts | Human solvability (100% tasks solved) | 100+ senior engineers validate tasks |
| **Difficulty** | Stump frontier LLMs (<10% accuracy) | Resist brute force (ARC-AGI-2) | Tasks where GPT-4 scores <30% |
| **Generalization** | Cross-domain (9 fields) | Few-shot (3 examples) | Cross-architecture + few-shot refactoring |
| **Scoring** | Mixed (MC + open-ended) | Binary (exact match) | Binary (code + pattern match) |
| **Anti-Memorization** | Google-proof questions | Procedural generation | Procedurally generated code snippets |
| **Calibration** | Confidence estimates tracked | N/A (binary scoring) | Track model confidence on pattern choices |
| **Efficiency** | N/A | $10k compute limit | 10-second inference limit |
| **Evolution** | Private held-out set | ARC-AGI-2 when v1 saturates | CARC-2 when models exceed 80% |

---

**Example CARC Task** (Combining HLE + ARC Principles):

```
Task: "Dependency Injection Refactoring"

Training Examples (3 total):

Example 1:
Before:
class UserService {
  private db = new MySQLDatabase();
  getUser(id: string) { return this.db.query(`SELECT * FROM users WHERE id=${id}`); }
}

After:
class UserService {
  constructor(private db: IDatabase) {}
  getUser(id: string) { return this.db.query(`SELECT * FROM users WHERE id=${id}`); }
}

Example 2:
Before:
class EmailSender {
  private smtp = new SMTPClient("smtp.gmail.com");
  send(to: string, body: string) { this.smtp.sendMail(to, body); }
}

After:
class EmailSender {
  constructor(private smtp: IMailClient) {}
  send(to: string, body: string) { this.smtp.sendMail(to, body); }
}

Example 3:
Before:
class Logger {
  private fs = new FileSystem("/var/log");
  log(msg: string) { this.fs.write("app.log", msg); }
}

After:
class Logger {
  constructor(private fs: IFileSystem) {}
  log(msg: string) { this.fs.write("app.log", msg); }
}

Test Case (novel code, must apply learned pattern):
Before:
class PaymentProcessor {
  private stripe = new StripeAPI("sk_live_...");
  charge(amount: number, card: string) {
    return this.stripe.createCharge(amount, card);
  }
}

Expected Answer:
class PaymentProcessor {
  constructor(private stripe: IPaymentGateway) {}
  charge(amount: number, card: string) {
    return this.stripe.createCharge(amount, card);
  }
}

Scoring:
✅ Constructor injection: +33%
✅ Interface abstraction (IPaymentGateway): +33%
✅ Removed hardcoded dependency: +34%
Total: 100% (all 3 required for full credit - like ARC's "all test cases correct")

Model must also provide confidence: "I am 85% confident this is the correct refactoring"
→ If wrong, calibration error is logged (like HLE)
```

**Why This Works**:
- **Few-shot** (ARC): Learn DI pattern from 3 examples
- **Generalization** (ARC): Apply to novel payment processing code
- **Expert-validated** (HLE): Senior engineers confirm this is correct DI refactoring
- **Binary scoring** (ARC): All 3 criteria must match (no partial credit)
- **Calibration** (HLE): Track overconfidence on wrong answers
- **Difficult** (HLE): Current code LLMs fail to recognize DI opportunities consistently

---

---

## Part 4: Final Recommendations

### 4.1 For Tamma's AI Provider Evaluation

Based on HLE + ARC insights, **how should we evaluate AI providers for code reasoning tasks**?

**Proposed Evaluation Criteria** (Inspired by HLE + ARC):

1. **Knowledge Depth** (HLE-Style):
   - Test on **domain-specific code** (e.g., "Implement OAuth2 PKCE flow in FastAPI")
   - Measure **accuracy** (does generated code work?)
   - Measure **calibration** (does model know when it's guessing?)
   - **Target**: Providers scoring >70% on HumanEval-style tasks

2. **Abstract Reasoning** (ARC-Style):
   - Test on **refactoring tasks** (e.g., "Given these 3 code smells, refactor the 4th")
   - Measure **pattern abstraction** (does model learn refactoring rules?)
   - Measure **few-shot generalization** (apply learned pattern to novel code)
   - **Target**: Providers scoring >50% on CARC-style tasks (when we build it)

3. **Architectural Understanding** (Hybrid HLE + ARC):
   - Test on **design pattern recognition** (e.g., "Identify the pattern in this codebase")
   - Test on **architecture decisions** (e.g., "Should this be microservices or monolith? Why?")
   - Measure **compositional reasoning** (does model combine multiple patterns?)
   - **Target**: Providers that can explain tradeoffs (not just generate code)

**Provider Selection Strategy**:

| Provider | Expected Strength | HLE Performance | ARC Performance | Best Use Case |
|----------|-------------------|-----------------|-----------------|---------------|
| **Anthropic Claude** | Reasoning + safety | High (strong on math/code) | Medium (limited spatial) | Architecture decisions, code review |
| **OpenAI O3** | Extended reasoning | Very High (25% HLE) | High (87% ARC-1, 15% ARC-2) | Complex refactoring, debugging |
| **GPT-5** | Knowledge breadth | Very High (25% HLE) | Low (0% ARC-2) | API generation, documentation |
| **GitHub Copilot** | Code completion | Medium (narrow domain) | Low | Boilerplate generation |
| **DeepSeek R1** | Cost-effective reasoning | Medium | Low (1.3% ARC-2) | Budget-constrained tasks |
| **Local LLMs** | Privacy, cost | Low | Low | Offline workflows, sensitive code |

**Tamma's Multi-Provider Strategy** (Based on Benchmark Insights):

1. **Use O3/Claude for architecture tasks** (high ARC + HLE performance)
2. **Use GPT-5/Copilot for generation tasks** (high knowledge, fast)
3. **Use DeepSeek for cost-sensitive tasks** (cheaper reasoning model)
4. **Measure calibration** (avoid overconfident providers for critical code)

---

### 4.2 Building a Code Reasoning Benchmark (CARC)

**Phase 1: Prototype (Month 1-2)**:
1. Create **50 tasks** spanning:
   - Refactoring patterns (15 tasks)
   - Architecture recognition (15 tasks)
   - Code smell detection (10 tasks)
   - Design pattern application (10 tasks)
2. Test on **GPT-4, Claude 3.5, O3** to validate difficulty
3. Test on **10 senior engineers** to validate solvability

**Phase 2: Full Benchmark (Month 3-6)**:
1. Expand to **400 tasks** (200 training, 200 evaluation)
2. Add **visual representations** (dependency graphs, UML diagrams)
3. Implement **procedural generation** (prevent memorization)
4. Add **calibration tracking** (confidence estimates)

**Phase 3: Public Release (Month 7+)**:
1. Open-source benchmark on GitHub
2. Launch **leaderboard** (like HLE/ARC)
3. Host **competition** with prizes for novel solutions
4. Partner with **code LLM providers** (OpenAI, Anthropic, GitHub) for evaluation

---

### 4.3 Research Gaps & Future Work

**Unanswered Questions from HLE**:
1. **Multimodal impact**: Do diagram-based questions hurt LLM scores more? (14% multimodal, no breakdown provided)
2. **Human baseline**: No systematic human testing reported (only estimated 60-80%)
3. **Domain bias**: Is 41% math too high? Does this favor arXiv-trained models?
4. **Calibration improvement**: Can models be trained to reduce 80%+ calibration error?

**Unanswered Questions from ARC**:
1. **Why does O3 work on ARC-AGI-1?**: What algorithmic approach enables 87.5%? (OpenAI hasn't published details)
2. **ARC-AGI-2 ceiling**: Is 60% human performance the limit, or can experts reach 80%+?
3. **Hybrid approaches**: Can symbolic reasoning + LLMs beat pure LLMs? (MindsAI used non-LLM approach)
4. **Transfer to code**: Do ARC-AGI skills transfer to code refactoring? (unexplored)

**Future Research for Code Reasoning**:
1. **Do code LLMs understand architecture?**: Systematic study of design pattern recognition
2. **Few-shot refactoring**: Can models learn refactoring rules from 3 examples?
3. **Visual code reasoning**: Do dependency graphs help LLMs reason about code structure?
4. **Calibration in code**: Are code LLMs overconfident when generating incorrect code?

---

---

## Conclusion

**Key Takeaways**:

1. **HLE and ARC are complementary**:
   - HLE tests **knowledge-grounded reasoning** (PhD-level expertise + multi-step thinking)
   - ARC tests **pure abstraction** (few-shot generalization, no prior knowledge)
   - **Both** expose severe limitations in current AI reasoning

2. **LLMs struggle with novel reasoning**:
   - HLE: Frontier models score **<25%** (vs 60-80% human baseline)
   - ARC-AGI-2: Even O3 (high compute) scores **4-15%** (vs 60% human baseline)
   - **Root cause**: Models excel at **pattern matching** (retrieving learned patterns) but fail at **pattern discovery** (inferring new rules)

3. **Code reasoning needs both HLE + ARC testing**:
   - **HLE-style**: Test knowledge of design patterns, frameworks, best practices
   - **ARC-style**: Test abstraction ability (refactoring, architecture recognition, few-shot learning)
   - **Current benchmarks** (HumanEval, MBPP) only test knowledge, not reasoning

4. **CARC (Code Architecture Reasoning Corpus) is feasible**:
   - Inspired by ARC's **few-shot puzzles** + HLE's **expert validation**
   - Tests **real developer skills**: Pattern recognition, refactoring, abstraction
   - Exposes LLM weaknesses: **Poor at understanding existing code**, overconfident, can't generalize

5. **Implications for Tamma**:
   - **Multi-provider strategy**: Use O3/Claude for reasoning tasks, GPT-5/Copilot for generation
   - **Calibration matters**: Avoid overconfident models for critical architecture decisions
   - **Benchmark our own providers**: Measure both knowledge (HLE-style) and reasoning (ARC-style)

**Final Insight**:
> "HLE shows AI lacks deep knowledge. ARC shows AI lacks true reasoning. Code reasoning requires BOTH. Current benchmarks test neither."

Building **CARC** would fill a critical gap in evaluating AI for real-world software development.

---

**References**:
- Humanity's Last Exam: https://scale.com/leaderboard/humanitys_last_exam
- ARC Prize: https://arcprize.org/
- ARC-AGI GitHub: https://github.com/fchollet/ARC-AGI
- H-ARC Human Performance Study: https://arxiv.org/html/2409.01374v1
- Do Code LLMs Understand Design Patterns?: https://arxiv.org/html/2501.04835v1
