# SimpleBench: Adversarial Robustness Research

**Benchmark**: SimpleBench
**Source**: https://simple-bench.com/
**Research Date**: November 1, 2025
**Focus**: Linguistic Adversarial Robustness ("Trick Questions")

---

## Executive Summary

SimpleBench is a multiple-choice benchmark where **non-specialized humans (high school knowledge) dramatically outperform state-of-the-art LLMs**. The benchmark contains 200+ questions testing spatio-temporal reasoning, social intelligence, and linguistic adversarial robustness. Key finding: Humans achieve **83.7%** accuracy while the best LLM (o1-preview) achieves only **41.7%**.

**Why It Matters for AIBaaS**: This benchmark reveals that models can solve complex coding tasks while failing at basic reasoning that humans find trivial. For code review and debugging, this suggests models may miss obvious bugs while focusing on sophisticated patterns.

---

## 1. Adversarial Question Types ("Trick Questions")

### Definition
SimpleBench defines **linguistic adversarial robustness** as questions where:
> "Memorized knowledge and approximate reasoning retrieval prove insufficient for accurate responses."

These questions test whether models can navigate **deceptive or unconventional phrasing** rather than rely on pattern matching from training data.

### Core Categories

#### 1.1 Spatial Reasoning Failures
**What They Test**: Understanding that physical objects obey gravity, containment rules, and spatial relationships.

**Example 1: The Vegetable Plate**
```
Q: Stephen carefully places a tomato, a potato, and a carrot on top
   of a plate, then spins the silver non-stick plate upside-down
   several times. How many vegetables remain on the plate?

A) 0 (all fall off)
B) 1
C) 2
D) 3 (all remain)

CORRECT: A
GPT-4o: D (WRONG)
Claude 3.5 Sonnet: D (WRONG)
```

**Why Models Fail**: They don't reliably understand that objects fall when not held in place.

**Example 2: The Sandwich Stack (from dataset)**
```
Q: Agatha stacks 5 sandwiches, tapes the top one to her walking stick,
   then walks to another room. How many whole sandwiches remain in
   each location?

A) 4 whole sandwiches in room A, 0 in Room B
B) 5 whole sandwiches in room A, 0 in Room B
C) 0 whole sandwiches in room A, 5 in Room B
D) 0 whole sandwiches in room A, 1 in Room B

CORRECT: A
WHY: The duct tape attachment doesn't constitute "carrying" the sandwich
     intact—it likely breaks apart during transport.
```

#### 1.2 Social Intelligence Gaps
**What They Test**: Intuition about how humans prioritize emotional reactions and social norms.

**Example: The Ex-Boyfriend (from dataset)**
```
Q: John returns from weeks offline to hear about his ex's new diet,
   new dog, nuclear war threat, and affair with Jack. What devastates
   him most?

A) Wider international events
B) Jack
C) The new dog
D) The diet

CORRECT: A
WHY: Most people prioritize existential threats over personal drama,
     but models assume relationship drama is paramount.
```

**Why Models Fail**: They exploit assumptions about personal drama while failing to recognize broader context importance.

#### 1.3 Logic Puzzle Subversions
**What They Test**: Whether models can recognize when a problem is simpler than expected.

**Example: The Sisters (from dataset)**
```
Q: Two sisters—Amy (always speaks mistruths) and Sam (always lies).
   You don't know which is which. One question determines the
   treasure path. What do you ask?

A) "Which path would your sister say leads to the treasure?"
B) "Are you Amy?"
C) "What path leads to the treasure?"
D) "Would you lie about the treasure path?"
E) "Is the left path the treasure path?"

CORRECT: C
WHY: Since BOTH sisters always lie (mistruths = lies), just ask
     directly and take the opposite. The classic meta-question
     (Option A) is unnecessary here.
```

**Why Models Fail**: Overfitting to memorized puzzle solutions (classic liar paradox).

#### 1.4 Temporal Reasoning Deficits
**What They Test**: Intuition about how long real-world activities take.

**Example: The Edinburgh Direction**
```
Q: You're standing in London facing west. Is Edinburgh to your
   left or right?

GPT-4 Turbo: "Edinburgh would be to your left, as it is located
              to the north."

CORRECT: Right (Edinburgh is northeast; facing west means north
         is to your right)
```

**Why Models Fail**: Fail to mentally simulate spatial rotations.

#### 1.5 Modified Classic Problems
**What They Test**: Whether models analyze problem constraints or default to memorized solutions.

**Example: Modified Monty Hall**
```
Q: You pick door 1 from three doors (one has gold, two have vegetables).
   The host asks if you want door 2 instead. Should you switch?

GPT-4: Yes (applying classic Monty Hall strategy)
Claude 3 Opus: Yes (applying classic Monty Hall strategy)

CORRECT: No advantage to switching—no new information was provided.

WHY MODELS FAIL: They recognize the scenario as "Monty Hall problem"
and apply the memorized solution without noticing the host didn't
reveal any information.
```

#### 1.6 Mathematical Counting Errors
**What They Test**: Basic enumeration capabilities.

**Example: Letter Counting**
```
Q: Count the letter 'L' in "LOLLAPALOOZA"

GPT-4 Turbo: "There are 5 'L's"
CORRECT: 4 (L-O-L-L-A-P-A-L-O-O-Z-A)
```

**Why Models Fail**: Fundamental character enumeration errors, possibly due to tokenization.

---

## 2. Linguistic Adversarial Robustness: Definition & Testing

### 2.1 Official Definition
SimpleBench defines linguistic adversarial robustness as the ability to correctly answer questions where:
1. **Surface patterns are misleading** (mimics known problems but with critical differences)
2. **Common assumptions fail** (e.g., "personal drama > existential threats")
3. **Memorization is insufficient** (requires actual reasoning, not retrieval)

### 2.2 Testing Methodology

**Data Integrity**:
- Most of the 200+ questions remain **private** (never published online)
- Text never enters training corpora
- Models cannot memorize answers or overfit
- Public dataset contains only 10 representative questions

**Evaluation Protocol**:
1. Standardized prompt: "Choose the most realistic answer step-by-step (COT)"
2. Chain-of-thought reasoning required
3. Multiple-choice format (4-6 options)
4. Automated scoring (correct/incorrect)
5. Engineered prompts tested, showed only marginal improvements

**Human Baseline**:
- 9 participants with high school knowledge: **83.7%** average
- Larger sample (exact size not disclosed): **92%** average
- No specialized expertise required

---

## 3. Question Categories

SimpleBench covers **three primary reasoning domains**:

### 3.1 Spatio-Temporal Reasoning
- **Spatial**: Object placement, gravity, containment, directional orientation
- **Temporal**: Duration estimation, sequence ordering, causality timing
- **Examples**: Vegetable plate, sandwich stack, Edinburgh direction

### 3.2 Social Intelligence
- **Priorities**: What matters most to humans in various situations
- **Norms**: Expected behavior in social contexts
- **Emotions**: Likely emotional reactions to events
- **Examples**: Ex-boyfriend scenario, mirror/bald man incident

### 3.3 Linguistic Adversarial Robustness
- **Modified classics**: Problems that resemble known puzzles but with key differences
- **Deceptive phrasing**: Questions with misleading surface structure
- **Assumption exploitation**: Questions that punish common AI biases
- **Examples**: Modified Monty Hall, sisters puzzle, counting tasks

---

## 4. Evaluation Method

### Automated Scoring
- **Format**: Multiple-choice (one correct answer)
- **Verification**: Automated (no human judges needed)
- **Metric**: Simple accuracy (correct/total)
- **No partial credit**: Answer is either right or wrong

### Human Verification
The benchmark was **validated by humans first**:
1. Questions written by benchmark creators
2. Tested on non-specialized humans (high school knowledge)
3. Verified that humans consistently achieve 80%+ accuracy
4. Ensured questions are unambiguous (single correct answer)

### Answer Verification
- No subjective judgment required
- Correct answers are **objectively verifiable** (e.g., Edinburgh IS northeast of London)
- Multiple-choice format eliminates free-form ambiguity

---

## 5. Top Performers & Model Rankings

### Official Leaderboard Results

| Model | Score | Gap to Human |
|-------|-------|--------------|
| **Human Baseline (9 people)** | 83.7% | — |
| **Human Baseline (larger sample)** | 92% | — |
| **o1-preview** | 41.7% | -42.0% |
| **Claude 3.5 Sonnet** | 27% | -56.7% |
| **Claude 3 Opus** | ~25% | -58.7% |
| **GPT-4 Turbo-Preview** | ~23% | -60.7% |
| **GPT-4o** | ~13% | -70.7% |
| **Gemini 1.5 Pro** | ~20% | -63.7% |

*(Note: Some scores estimated from descriptions like "10% difference" or "7th place")*

### Key Findings

1. **ALL tested LLMs underperform non-specialized humans** by massive margins (40-70%)
2. **Reasoning-focused models (o1) perform best** but still fail 58% of the time
3. **GPT-4o shows dramatic underperformance** despite being optimized for coding/math
4. **Performance inversely correlates with specialization** (general models slightly better)

### Hypothesis: GPT-4o Underperformance
SimpleBench creators hypothesize that GPT-4o's poor performance stems from:
> "Optimizing for specific industrial applications (math and coding) at the expense of holistic reasoning."

OpenAI likely made GPT-4o **lighter (fewer parameters)** for better price-to-performance, but this **lost reasoning capability** on basic common-sense tasks.

**Implication for AIBaaS**: Models optimized for coding may actually be **worse** at code review requiring common-sense reasoning (e.g., "Does this make sense?" vs. "Is this syntactically correct?").

---

## 6. Failure Patterns

### 6.1 Overfitting to Training Data
**Pattern**: Models recognize surface similarity to known problems and apply memorized solutions.

**Example**: Modified Monty Hall problem
- Models detect "three doors" + "host asks to switch"
- Retrieve "Monty Hall" solution from training data
- Fail to notice the host didn't reveal information
- Apply incorrect strategy

**Code Analogy**: Seeing `for (int i = 0; ...)` and assuming standard iteration without checking boundary conditions.

### 6.2 Reasoning Fragmentation
**Pattern**: Models have "snippets of good reasoning but often can't piece them together."

**Example**: Claude 3.5 Sonnet on mirror question
```
✅ CORRECT: "The 'bald man' John sees in the mirror is actually
             John himself. He's looking at his own reflection."

❌ THEN CONTRADICTS: "C) is incorrect because someone else
                      (the bald man) did get hurt."
```

**Code Analogy**: Correctly identifying that a variable is null, then suggesting to call methods on it anyway.

### 6.3 Missing Common-Sense Physics
**Pattern**: Models fail to simulate basic physical laws (gravity, containment, etc.).

**Examples**:
- Objects on upside-down plate (should fall, models say they stay)
- Sandwich taped to stick (should break, models say it stays whole)

**Code Analogy**: Missing obvious resource leaks or race conditions that any human would catch.

### 6.4 Statistical vs. Logical Reasoning
**Pattern**: Models use probabilistic pattern matching instead of deterministic logic.

**Example**: Russian Roulette problem
```
Claude 3 Opus: "It does not matter [if you spin]"
THEN PROVIDES: "100% danger vs 83.33% danger"
CONTRADICTION: Claims outcomes identical while giving different probabilities
```

**Code Analogy**: Saying "performance is the same" while admitting one approach is O(n²) and the other is O(n).

### 6.5 Failure to Adapt to Constraints
**Pattern**: Models ignore modified problem constraints and solve the original version.

**Example**: Farmer with three-compartment boat
- Problem states boat has **three separate compartments**
- Models ignore this and provide the classic multi-trip solution
- Correct answer: Just put each item in a compartment and row once

**Code Analogy**: Continuing to suggest workarounds for a limitation that was already removed in the problem description.

### 6.6 Directional/Spatial Confusion
**Pattern**: Models struggle with mental rotation and relative positioning.

**Example**: Edinburgh direction (facing west in London)
- GPT-4 Turbo: "Edinburgh would be to your left, as it is located to the north."
- Fails to rotate mental model (north is to the RIGHT when facing west)

**Code Analogy**: Confusing left/right in binary tree traversal or getting array indices backwards.

---

## 7. Code-Specific Adversarial Questions

Below are **10 trick questions for code review** designed to test adversarial robustness in coding models.

### Q1: The Misleading Variable Name
```python
def calculate_total_price(items):
    """Calculate total price of items including tax."""
    subtotal = sum(item.price for item in items)
    tax_rate = 0.08
    total = subtotal + tax_rate  # ← BUG
    return total
```

**Question**: Is there a bug in this code?

**Choices**:
- A) No, it correctly calculates the total price with tax
- B) Yes, it should be `subtotal * tax_rate`, not addition
- C) Yes, it should be `subtotal * (1 + tax_rate)`, not addition
- D) No, but it should use Decimal for currency

**Correct Answer**: C

**Expected Failure**: Models might focus on the "should use Decimal" best practice (D) and miss the actual arithmetic bug. The variable name `tax_rate` plus the docstring creates false confidence.

---

### Q2: The Red Herring Comment
```javascript
function processUserInput(input) {
  // SECURITY: Always sanitize user input to prevent XSS attacks
  const sanitized = input.trim().toLowerCase();

  // Insert into database
  db.query(`INSERT INTO users (name) VALUES ('${sanitized}')`);
  return { success: true };
}
```

**Question**: What is the PRIMARY security vulnerability in this code?

**Choices**:
- A) No XSS protection (missing HTML escaping)
- B) SQL injection vulnerability
- C) Missing input length validation
- D) No authentication check

**Correct Answer**: B

**Expected Failure**: The comment loudly warns about XSS, distracting from the **actual** vulnerability (SQL injection). Models might focus on what the comment mentions rather than analyzing the code.

---

### Q3: The Off-By-One Disguise
```java
public static int findLastIndex(int[] arr, int target) {
    // Search from end to find last occurrence
    for (int i = arr.length - 1; i >= 0; i--) {
        if (arr[i] == target) {
            return i + 1;  // Return 1-based index for user-friendliness
        }
    }
    return 0;  // Not found (0 is invalid 1-based index)
}
```

**Question**: Is there a bug in this code?

**Choices**:
- A) No, it correctly returns 1-based indices as documented
- B) Yes, it should return -1 for not found, not 0
- C) Yes, the 1-based indexing is inconsistent with Java conventions and will cause bugs
- D) No, but it should throw an exception instead of returning 0

**Correct Answer**: C

**Expected Failure**: The code is internally consistent (1-based indexing throughout), but violates Java conventions and will cause confusion when used with standard 0-based APIs. Models might accept the "user-friendly" justification without considering integration issues.

---

### Q4: The Performance "Optimization"
```python
def get_user_emails(user_ids):
    """Optimized version: Batch database queries for performance."""
    emails = []
    for user_id in user_ids:
        # Cache each lookup to avoid redundant queries
        cached = cache.get(f"user_email_{user_id}")
        if cached:
            emails.append(cached)
        else:
            result = db.query("SELECT email FROM users WHERE id = ?", [user_id])
            cache.set(f"user_email_{user_id}", result.email)
            emails.append(result.email)
    return emails
```

**Question**: What is the performance anti-pattern here?

**Choices**:
- A) No issue, it's properly optimized with caching
- B) Should use connection pooling
- C) N+1 query problem (should use single query for all IDs)
- D) Cache keys should be hashed for security

**Correct Answer**: C

**Expected Failure**: The comment claims "batch queries" and shows caching, distracting from the fact that it's still making **N individual queries** instead of one `WHERE id IN (...)` query.

---

### Q5: The Plausible Explanation
```typescript
class RateLimiter {
  private requests: Map<string, number[]> = new Map();

  /**
   * Check if request is allowed (max 100 requests per minute per user).
   * Clears old timestamps to prevent memory leaks.
   */
  isAllowed(userId: string): boolean {
    const now = Date.now();
    const userRequests = this.requests.get(userId) || [];

    // Remove requests older than 1 minute (60000ms)
    const recentRequests = userRequests.filter(
      timestamp => now - timestamp < 60000
    );

    if (recentRequests.length >= 100) {
      return false;
    }

    recentRequests.push(now);
    this.requests.set(userId, recentRequests);
    return true;
  }
}
```

**Question**: What is the critical bug?

**Choices**:
- A) No bug, it correctly implements rate limiting with cleanup
- B) Race condition: Not thread-safe for concurrent requests
- C) Memory leak: Never removes users with zero recent requests
- D) Logic error: Allows 100 requests every minute instead of limiting to 100/min

**Correct Answer**: C

**Expected Failure**: The comment specifically mentions preventing memory leaks, creating false confidence. Models might miss that we clean up old timestamps but never remove **users** with empty arrays.

---

### Q6: The Subtle Type Confusion
```python
def merge_configs(base: dict, override: dict) -> dict:
    """Merge two config dictionaries, with override taking precedence.

    Example:
        base = {"timeout": 30, "retries": 3}
        override = {"timeout": 60}
        result = {"timeout": 60, "retries": 3}
    """
    result = base.copy()
    result.update(override)
    return result
```

**Question**: What happens if configs contain nested dictionaries?

**Choices**:
- A) Works correctly, nested dicts are recursively merged
- B) Shallow copy: Nested dicts in override completely replace base nested dicts
- C) Error: dict.update() doesn't support nested dicts
- D) Deep copy: All nested structures are properly merged

**Correct Answer**: B

**Expected Failure**: The docstring example only shows flat dictionaries. Models might assume it handles nesting correctly since the function name says "merge" and the example works.

---

### Q7: The Async Trap
```javascript
async function saveUsers(users) {
  console.log('Starting batch save...');

  for (const user of users) {
    await db.users.save(user);
    console.log(`Saved user ${user.id}`);
  }

  console.log('Batch save complete!');
  return { saved: users.length };
}
```

**Question**: What is the performance issue?

**Choices**:
- A) No issue, async/await is correctly used
- B) Sequential execution: Should use Promise.all() for parallel saves
- C) Missing try-catch for error handling
- D) Memory leak: Logs not garbage collected

**Correct Answer**: B

**Expected Failure**: The code is syntactically correct and uses `async/await` properly. Models might approve it without noticing the sequential bottleneck. The phrase "batch save" misleadingly suggests optimization.

---

### Q8: The "Fixed" Security Vulnerability
```go
func HandleFileUpload(w http.ResponseWriter, r *http.Request) {
    file, header, err := r.FormFile("upload")
    if err != nil {
        http.Error(w, "Upload failed", 400)
        return
    }
    defer file.Close()

    // SECURITY FIX: Prevent directory traversal attacks
    filename := filepath.Base(header.Filename)

    // Validate file extension
    allowed := []string{".jpg", ".png", ".gif"}
    ext := filepath.Ext(filename)
    if !contains(allowed, ext) {
        http.Error(w, "Invalid file type", 400)
        return
    }

    // Save file
    dst, _ := os.Create("./uploads/" + filename)
    defer dst.Close()
    io.Copy(dst, file)
}
```

**Question**: What security vulnerability remains?

**Choices**:
- A) None, the directory traversal fix is complete
- B) Filename collision: Could overwrite existing files
- C) File content not validated (could upload .jpg.php as .jpg)
- D) Missing file size limit check

**Correct Answer**: C

**Expected Failure**: The comment claims "SECURITY FIX" and shows `filepath.Base()`, creating false confidence. Models might miss that extension validation is insufficient (file content can be malicious, double extensions, etc.).

---

### Q9: The Hidden State Mutation
```python
class ShoppingCart:
    def __init__(self):
        self.items = []

    def get_items(self):
        """Returns current items in cart."""
        return self.items

    def get_total(self):
        """Calculate total price of items."""
        return sum(item.price for item in self.items)

# Usage
cart = ShoppingCart()
cart.items.append(Item("Book", 20))

# Later in another module
items = cart.get_items()
items.append(Item("Pen", 2))  # ← Is this safe?
```

**Question**: What is the design flaw?

**Choices**:
- A) No flaw, the API is correctly designed
- B) get_items() returns mutable reference, allowing external state mutation
- C) Missing type hints on get_items()
- D) get_total() should be cached for performance

**Correct Answer**: B

**Expected Failure**: The code runs without errors. Models might miss that returning a direct reference to `self.items` breaks encapsulation and allows external mutation.

---

### Q10: The Concurrent "Fix"
```rust
use std::sync::Arc;
use std::thread;

struct Counter {
    value: i32,
}

impl Counter {
    fn increment(&mut self) {
        self.value += 1;
    }
}

fn main() {
    // FIX: Use Arc for thread-safe sharing
    let counter = Arc::new(Counter { value: 0 });

    let mut handles = vec![];

    for _ in 0..10 {
        let counter = Arc::clone(&counter);
        let handle = thread::spawn(move || {
            counter.increment();  // ← Does this compile?
        });
        handles.push(handle);
    }

    for handle in handles {
        handle.join().unwrap();
    }

    println!("Final count: {}", counter.value);
}
```

**Question**: What is wrong with this code?

**Choices**:
- A) Nothing, Arc makes it thread-safe
- B) Won't compile: Arc doesn't allow mutation without Mutex
- C) Race condition: Multiple threads increment simultaneously
- D) Memory leak: Arc creates reference cycle

**Correct Answer**: B

**Expected Failure**: The comment says "FIX" and shows `Arc`, creating false confidence. Models might think `Arc = thread-safe` without recognizing that `Arc` only provides **shared ownership**, not **interior mutability**. Needs `Arc<Mutex<Counter>>`.

---

## 8. Applicability to AIBaaS

### 8.1 Should We Have an "Adversarial Code Review" Scenario?

**Recommendation: YES, with high priority**

**Rationale**:
1. **Real-world relevance**: Codebases contain misleading comments, confusing variable names, and plausible-but-wrong explanations. Models must navigate these distractions.

2. **Differentiating factor**: Most benchmarks test "can you solve this problem?" SimpleBench tests "can you spot the trick?" This is closer to real code review.

3. **Anti-gaming measure**: Models can't memorize solutions since trick questions require analyzing specific context, not retrieving patterns.

4. **Reveals overfitting**: SimpleBench shows that coding-optimized models (GPT-4o) can be **worse** at reasoning tasks. Our benchmark should test if code-focused models miss obvious bugs.

### 8.2 Weight: How Much Should Robustness Matter vs Accuracy?

**Recommended Weight: 20-25% of total score**

**Justification**:
- **Not primary metric**: Code generation accuracy is still most important (50-60% weight)
- **Critical quality gate**: A model that generates perfect code but misses obvious bugs in review is dangerous
- **Balances specialization trade-off**: Prevents over-optimization for narrow tasks at the expense of reasoning

**Scoring Breakdown**:
```
Total Benchmark Score =
  50% Code Generation Accuracy (SWE-bench, Aider-style tasks)
  20% Adversarial Robustness (SimpleBench-inspired trick questions)
  15% Code Honesty (MASK-inspired, admits uncertainty)
  10% Hallucination Resistance (catches fake API usage)
  5%  Performance & Cost Efficiency
```

### 8.3 Could Adversarial Testing Reveal Overfitting to Training Data?

**Answer: YES, this is a primary benefit**

**Evidence from SimpleBench**:

1. **Modified Monty Hall Problem**: Models retrieve "Monty Hall → always switch" from training data without analyzing the modified scenario.

2. **Sisters Puzzle**: Models apply complex meta-question solutions from training data instead of recognizing the simple direct approach works here.

3. **Private Question Set**: SimpleBench keeps 190+ questions private specifically to prevent training contamination.

**Application to Code Benchmarks**:

**Example: Overfitting Detection**
```python
# Training data likely contains many examples of this pattern:
def factorial(n):
    if n <= 1:
        return 1
    return n * factorial(n - 1)

# Trick question: Does this work correctly?
def factorial(n):
    """Calculate factorial of n."""
    if n <= 1:
        return 1
    return n * factorial(n - 1)

# For n = 0.5: Infinite recursion!
# Models might approve because the pattern matches training data.
```

**Private Test Set Strategy**:
- Keep 80% of adversarial questions private
- Rotate questions quarterly
- Monitor performance degradation on new vs old questions
- If score drops >15% on new questions, flag as potential overfitting

### 8.4 Proposed Adversarial Code Review Benchmark

**Name**: CodeTrickBench (CTB)

**Structure**:
- 200 total questions (40 public, 160 private)
- 10 categories (security, performance, logic, types, concurrency, etc.)
- 20 questions per category
- Multiple-choice format (4 options)
- Quarterly question rotation

**Difficulty Tiers**:
1. **Tier 1 (Basic)**: Obvious bugs with misleading comments (50 questions)
2. **Tier 2 (Intermediate)**: Subtle logic errors with plausible explanations (100 questions)
3. **Tier 3 (Advanced)**: Context-dependent bugs requiring domain knowledge (50 questions)

**Scoring**:
```
Base Score = (Correct / Total) × 100

Adjusted Score = Base Score × (1 - Overfitting Penalty)

Overfitting Penalty = max(0, (Public Score - Private Score) / Public Score)
```

**Example**:
- Public questions: 85%
- Private questions: 70%
- Overfitting penalty: (85 - 70) / 85 = 17.6%
- Adjusted score: 70 × (1 - 0.176) = 57.7%

**Prevents Gaming**: Models that memorize public questions get penalized when private scores drop.

---

## 9. Key Takeaways for AIBaaS

### 9.1 Design Insights

1. **Humans as baseline**: SimpleBench validates questions by ensuring humans with basic knowledge achieve 80%+. We should do the same with junior developers for our code questions.

2. **Private test sets**: Keep majority of questions private to prevent training contamination and memorization.

3. **Multiple-choice format**: Eliminates subjective judgment and enables automated evaluation at scale.

4. **Quarterly rotation**: Refresh questions regularly to detect overfitting over time.

5. **Overfitting penalties**: Models that score much better on public vs private questions should be penalized.

### 9.2 Implementation Recommendations

**Phase 1: Prototype (Month 1)**
- Create 50 adversarial code review questions (10 public, 40 private)
- Manual validation with engineering team (target: humans achieve 85%+)
- Baseline evaluation: Claude 3.5, GPT-4o, Gemini 1.5 Pro, o1

**Phase 2: Scaling (Month 2-3)**
- Expand to 200 questions across 10 categories
- Implement automated evaluation pipeline
- Build overfitting detection (public vs private performance)

**Phase 3: Integration (Month 4)**
- Integrate into AIBaaS leaderboard (20% weight)
- Launch public beta with 10% of questions visible
- Quarterly question rotation schedule

**Phase 4: Iteration (Ongoing)**
- Monitor which questions best differentiate models
- Add new categories based on failure patterns
- Adjust weights based on user feedback

### 9.3 Expected Impact

**Hypothesis**: Coding-optimized models (GPT-4o, Codex) will underperform on adversarial code review compared to general reasoning models (o1, Claude Opus).

**Why This Matters**:
- Challenges assumption that "best at code generation = best at code review"
- Reveals trade-offs between specialization and robustness
- Helps users choose models based on use case (generation vs review vs debugging)

**Differentiation**: No other code benchmark tests adversarial robustness at scale. This makes AIBaaS uniquely valuable for evaluating code review capabilities.

---

## 10. Comparison with Other Benchmarks

| Benchmark | Focus | Adversarial? | Private? | AIBaaS Relevance |
|-----------|-------|--------------|----------|------------------|
| **SimpleBench** | General reasoning | ✅ Yes | ✅ 95% | HIGH (methodology) |
| **SWE-bench** | Code generation | ❌ No | ❌ Public | HIGH (core task) |
| **Aider** | Code editing | ❌ No | ✅ Partial | MEDIUM (accuracy) |
| **MASK** | Honesty | ✅ Yes | ✅ Some | HIGH (trust) |
| **HumanEval** | Code synthesis | ❌ No | ❌ Public | LOW (saturated) |
| **MBPP** | Basic programming | ❌ No | ❌ Public | LOW (too easy) |

**SimpleBench's Unique Value**: Only mainstream benchmark testing adversarial robustness with private question sets and human baseline validation.

---

## 11. Risks & Mitigations

### Risk 1: Questions Too Hard
**Problem**: If adversarial questions are too tricky, all models score <30% and the benchmark isn't useful.

**Mitigation**:
- Validate with human baseline first (target: 80-90%)
- Use tiered difficulty (easy/medium/hard)
- Report per-tier scores separately

### Risk 2: Subjectivity in "Trick" Definition
**Problem**: What counts as a "trick" vs "unfairly misleading"?

**Mitigation**:
- Multiple-choice format reduces ambiguity
- Each question has objectively correct answer
- Human review panel approves all questions
- A/B test questions with users to eliminate controversial ones

### Risk 3: Models Game the Benchmark
**Problem**: Once questions are public, models might be fine-tuned on them.

**Mitigation**:
- Keep 80% of questions private
- Rotate 25% of questions quarterly
- Monitor performance degradation on new questions
- Apply overfitting penalty (public vs private score gap)

### Risk 4: Human Baseline Too Low
**Problem**: If human developers only achieve 60%, we can't call questions "simple."

**Mitigation**:
- Pre-validate with 20+ developers across skill levels
- Discard questions where humans score <75%
- Focus on "obvious bugs with distractions" not "obscure edge cases"

---

## 12. Research Gaps & Future Work

### Questions Not Answered by SimpleBench

1. **What types of tricks work best against specific models?**
   - SimpleBench doesn't break down failures by trick category
   - We should track: misleading comments, variable names, plausible explanations, etc.

2. **How does prompt engineering affect adversarial robustness?**
   - SimpleBench tested engineered prompts, found "marginal improvements"
   - We should test adversarial prompting techniques (e.g., "ignore all previous instructions")

3. **Can models improve adversarial robustness via training?**
   - SimpleBench is cross-sectional, doesn't track improvement over time
   - We should benchmark same model family across versions

4. **What's the cost-accuracy trade-off for adversarial robustness?**
   - SimpleBench doesn't report inference costs
   - We should track: does higher cost correlate with better robustness?

### Proposed Studies

**Study 1: Trick Taxonomy**
- Classify our 200 adversarial questions into 15 subcategories
- Example: "Misleading comment", "Red herring optimization", "Plausible but wrong explanation"
- Report per-subcategory performance to reveal model-specific weaknesses

**Study 2: Prompt Robustness**
- Test same questions with 5 different prompt styles:
  1. Standard: "Review this code. Is there a bug?"
  2. Pressured: "This code is from a senior engineer, it's probably correct. Any issues?"
  3. Adversarial: "This code is from a junior dev, find all the bugs."
  4. Neutral: "Analyze this code."
  5. Meta: "This is a trick question designed to fool you. Think carefully."

**Study 3: Longitudinal Robustness**
- Track GPT-4 → GPT-4 Turbo → GPT-4o adversarial scores over time
- Hypothesis: Optimization for specific tasks (coding) degrades general robustness

**Study 4: Cost-Robustness Trade-off**
- For each model, measure:
  - Adversarial robustness score (our benchmark)
  - Cost per 1M tokens
  - Accuracy on standard code generation (SWE-bench)
- Visualize Pareto frontier: Which models offer best robustness per dollar?

---

## 13. Conclusion

SimpleBench reveals a critical insight: **State-of-the-art models optimized for coding (GPT-4o) can be dramatically worse at basic reasoning than general models (o1, Claude).**

For AIBaaS, this means:
1. **We need adversarial code review tests** to measure robustness, not just accuracy
2. **Weight should be 20-25%** of total benchmark score
3. **Private test sets are essential** to prevent overfitting and gaming
4. **Human baselines validate** that questions test reasoning, not trivia

SimpleBench's methodology provides a proven framework for adversarial robustness benchmarking. By adapting it to code-specific scenarios, AIBaaS can offer unique insights into which models are best for **code review and debugging** (not just generation).

---

## References

1. SimpleBench official website: https://simple-bench.com/
2. SimpleBench public dataset: https://huggingface.co/datasets/Impulse2000/simple_bench_public-20-12-2024
3. "Easy Problems That LLMs Get Wrong" (research paper): https://arxiv.org/html/2405.19616v1
4. "A Not-So Simple Way to Beat Simple Bench": https://arxiv.org/abs/2412.12173

---

**Next Steps**:
1. ✅ Complete SimpleBench research documentation
2. ⬜ Create 50-question adversarial code review prototype
3. ⬜ Validate with human baseline (target: 85%+ accuracy)
4. ⬜ Baseline evaluation of Claude/GPT-4o/Gemini
5. ⬜ Design overfitting detection methodology
6. ⬜ Integrate into AIBaaS leaderboard architecture

---

**Document Status**: ✅ Complete
**Research Quality**: High confidence (official sources + academic papers)
**Applicability**: HIGH - Direct methodology transfer to code adversarial robustness
**Implementation Readiness**: Medium - Requires question creation and validation phase
