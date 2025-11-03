# Vellum AI, Evidently AI, and LiveBench Website Analysis

**Date**: 2024-11-02
**Research Focus**: Three additional benchmark/evaluation resources
**Relevance to AIBaaS**: Vellum AI is a moderate competitive overlap, Evidently AI is adjacent (different category), LiveBench validates existing research

---

## Executive Summary

Analyzed three resources to assess competitive landscape for AIBaaS:

1. **Vellum AI LLM Leaderboard** - Development platform with leaderboard feature (moderate overlap)
2. **Evidently AI** - LLM observability/monitoring tool (different category, not competitive)
3. **LiveBench.ai** - Validates existing research (JavaScript-required site, couldn't extract new data)

**Key Finding**: Vellum AI has **some** overlap with AIBaaS (cost + latency tracking, developer focus) BUT they're a **full development platform** (like LangChain), not a dedicated benchmarking service. Their leaderboard is a **feature**, not their core product.

**Competitive Threat Level**:
- Vellum AI: **MODERATE** (overlapping features but different positioning)
- Evidently AI: **LOW** (different product category)
- LiveBench: **LOW** (already analyzed, academic focus)

---

## Part 1: Vellum AI LLM Leaderboard

### Platform Overview

**What is Vellum AI?**
- **Core Product**: LLM development platform (like LangChain, LlamaIndex, or LangSmith)
- **Key Capabilities**: Prompt engineering, workflow building, agent creation, deployment, monitoring
- **Leaderboard**: One feature among many (not their primary product)

**Business Model**:
- Freemium platform with paid tiers
- Specific pricing not disclosed publicly
- Leaderboard appears free to access

### Leaderboard Features

#### 1. **Benchmarks Covered** (40+ models)

| Category | Benchmarks | AIBaaS Equivalent |
|----------|-----------|-------------------|
| **Reasoning** | GPQA Diamond (complex science) | ❌ Not applicable (dev-focused) |
| **Math** | AIME 2025, MATH 500 | ❌ Not applicable |
| **Coding** | SWE Bench, HumanEval | ✅ Similar (code generation, GitHub issues) |
| **Tool Use** | BFCL benchmark | ⚠️ Related (API usage) |
| **Adaptive Reasoning** | GRIND (Vellum proprietary) | ❌ Not applicable |
| **Overall** | Humanity's Last Exam | ❌ Academic benchmark |

**Key Observation**: Mix of academic (reasoning, math) and practical (coding) benchmarks. Similar to Hugging Face Leaderboard's hybrid approach.

#### 2. **Cost Tracking** ✅

Vellum tracks:
- Input pricing per 1M tokens
- Output pricing per 1M tokens
- Speed (tokens/second)
- Latency (time-to-first-token)

**Example**:
```
Llama 4 Scout:
- Speed: 2,600 tokens/second
- Latency: 0.33s (TTFT)
- Cost: $0.11 input / $0.34 output per 1M tokens
```

**Comparison to AIBaaS**:

| Feature | Vellum AI | AIBaaS (Planned) | Winner |
|---------|-----------|------------------|--------|
| **Cost Tracking** | ✅ Provider-sourced pricing | ✅ Real $/task from actual runs | **AIBaaS** (dynamic vs static) |
| **Granularity** | Per 1M tokens | Per task (e.g., "generate function") | **AIBaaS** (more practical) |
| **Historical Costs** | ❌ No | ✅ 1-year retention | **AIBaaS** |
| **Cost-Performance Tradeoff** | ⚠️ Separate tables | ✅ Unified view (scatter plot) | **AIBaaS** |

**Vellum's Limitation**: Static pricing table updated periodically (last: Oct 21, 2025). No real-time cost monitoring or historical trends.

#### 3. **Latency Tracking** ✅

Vellum tracks:
- Tokens per second (throughput)
- Time-to-first-token (TTFT)

**Comparison to AIBaaS**:

| Metric | Vellum AI | AIBaaS (Planned) | Winner |
|--------|-----------|------------------|--------|
| **Latency Metrics** | TTFT, throughput | P50/P95/P99 response time | **AIBaaS** (percentiles more actionable) |
| **Per-Task Latency** | ❌ Generic | ✅ Per scenario (code gen, review) | **AIBaaS** |
| **Historical Latency** | ❌ No | ✅ 1-year retention | **AIBaaS** |

**Vellum's Limitation**: Generic latency metrics, not task-specific. No P95/P99 percentiles (critical for SLA monitoring).

#### 4. **Developer Focus** ✅

Vellum's marketing emphasizes:
- "Build agents in plain English"
- "Vibe-code your own agents in minutes"
- SWE Bench and HumanEval benchmarks included

**Assessment**: Developer-focused platform, but broader than just coding (includes reasoning, math, tool use).

#### 5. **Historical Data** ❌

**Not Available**: Leaderboard shows current rankings (updated Oct 21, 2025) but no visible:
- Trend analysis over time
- Model performance degradation alerts
- Time-window slider (like LiveBench)

#### 6. **API Access** ❌

**Not Mentioned**: No programmatic API for leaderboard data documented. Vellum has deployment APIs for their platform, but leaderboard data appears web-only.

#### 7. **Contamination Prevention** ⚠️

**Partial**: Vellum states they "focus on models released after April 2024 and prioritize non-saturated benchmarks."

**Comparison to AIBaaS**:

| Strategy | Vellum AI | AIBaaS (Planned) |
|----------|-----------|------------------|
| **Post-training data** | ✅ Models after April 2024 | ✅ Monthly updates with new problems |
| **Release date tracking** | ⚠️ Implicit | ✅ Explicit flagging |
| **Private test set** | ❌ Not mentioned | ✅ 80% hidden, quarterly rotation |

### Competitive Assessment: Vellum AI

**Vellum AI Positioning**:
```
┌──────────────────────────────────────────────────────────────┐
│                   Vellum AI Platform                          │
├──────────────────────────────────────────────────────────────┤
│  Core Product: LLM Development Platform                       │
│  - Prompt engineering                                         │
│  - Workflow building (visual node editor)                     │
│  - Agent creation (plain English → working agent)             │
│  - Deployment & versioning                                    │
│  - Monitoring & observability                                 │
│  - Custom evaluation framework                                │
│                                                                │
│  LLM Leaderboard: Supporting Feature                          │
│  - Helps users choose which model to use in Vellum platform   │
│  - Static reference data (provider pricing, benchmark scores) │
│  - Updated periodically (not real-time)                       │
└────────────────────────────────────────────────────────────────┘
```

**AIBaaS Positioning**:
```
┌──────────────────────────────────────────────────────────────┐
│                   AIBaaS Service                              │
├──────────────────────────────────────────────────────────────┤
│  Core Product: AI Provider Benchmarking & Monitoring          │
│  - Real-time continuous benchmarking (hourly runs)            │
│  - Dynamic $/task cost tracking (actual runs, not provider pricing) │
│  - Historical trend analysis (1-year retention)               │
│  - Alerting system (Slack, email, webhooks)                   │
│  - REST + GraphQL API (programmatic access)                   │
│  - Developer-focused tasks (code gen, review, refactor)       │
└────────────────────────────────────────────────────────────────┘
```

**Key Differences**:

| Dimension | Vellum AI | AIBaaS |
|-----------|-----------|--------|
| **Primary Business** | Development platform | Benchmarking service |
| **Leaderboard Role** | Supporting feature | Core product |
| **Update Frequency** | Periodic (manual) | Continuous (automated hourly) |
| **Cost Tracking** | Static provider pricing | Dynamic $/task from real runs |
| **Historical Data** | None | 1-year retention with trends |
| **API Access** | Platform APIs (not leaderboard) | Dedicated leaderboard API |
| **Alerting** | None (leaderboard) | Slack, email, webhooks |
| **Target User** | Developers building LLM apps | Teams choosing AI providers |

**Competitive Threat Level**: **MODERATE**

**Why Moderate (Not High)**:
1. **Different Core Business**: Vellum sells a development platform; leaderboard is a feature to support that
2. **Different User Journey**:
   - Vellum: "I want to build an LLM app → Use Vellum platform → Check leaderboard to pick model"
   - AIBaaS: "I want to choose best AI provider → Use AIBaaS → Integrate chosen provider into my app"
3. **Different Revenue Model**:
   - Vellum: Platform subscription (presumably $100s/month for workflow, deployment, monitoring)
   - AIBaaS: Benchmarking subscription ($49 Pro / $499 Enterprise)

**Where Vellum Overlaps**:
- ✅ Cost tracking (though static vs dynamic)
- ✅ Latency tracking (though generic vs percentiles)
- ✅ Developer focus (though broader than just coding)
- ✅ Coding benchmarks (SWE Bench, HumanEval)

**Where AIBaaS Wins**:
- ✅ Real-time continuous monitoring (Vellum is static snapshots)
- ✅ Historical data (Vellum has none)
- ✅ API access for leaderboard data (Vellum doesn't offer this)
- ✅ Alerting system (Vellum has none)
- ✅ Dynamic $/task costs (Vellum shows provider pricing only)
- ✅ Dedicated benchmarking focus (Vellum's leaderboard is secondary)

**Strategic Recommendation**:

**Position AIBaaS as complementary to Vellum**:
```markdown
"Use Vellum to BUILD your LLM app.
Use AIBaaS to MONITOR which AI provider performs best."

"Vellum = Development & Deployment Platform
AIBaaS = Performance & Cost Analytics"
```

**Partnership Opportunity**:
- Integrate AIBaaS data into Vellum's model selection UI
- Vellum users get real-time performance data when choosing models
- AIBaaS gets distribution through Vellum's user base

---

## Part 2: Evidently AI

### Platform Overview

**What is Evidently AI?**
- **Core Product**: LLM observability and monitoring platform
- **Category**: Similar to LangSmith (LangChain), Weights & Biases, Arize AI
- **Key Capabilities**: Testing, evaluation, monitoring for YOUR OWN LLM applications
- **Use Case**: Monitor production LLM apps, detect drift, evaluate RAG systems

**Business Model**:
- Open-source core (Apache 2.0, 25M+ downloads)
- Paid cloud version (freemium)
- Enterprise self-hosted version

### Key Features

#### 1. **Internal Evaluation** (Not Public Benchmarking)

Evidently helps teams evaluate **their own LLM applications**, not compare public models:

| Evidently AI | AIBaaS |
|-------------|--------|
| "Test YOUR app's performance" | "Compare PUBLIC models' performance" |
| Internal metrics, private data | Public leaderboard, standardized benchmarks |
| Custom evaluation criteria | Standardized developer tasks |

**Key Distinction**: Evidently is a **tool you use internally**, AIBaaS is a **public service you subscribe to**.

#### 2. **100+ Built-in Metrics**

Evidently provides metrics for:
- Text generation quality
- RAG context relevance
- Hallucination detection
- Response time (latency)
- Token usage (cost)

**Comparison to AIBaaS**:

| Feature | Evidently AI | AIBaaS |
|---------|--------------|--------|
| **Scope** | YOUR app only | Multiple providers compared |
| **Data** | Private (your users, your prompts) | Public (standardized benchmarks) |
| **Use Case** | Production monitoring | Model selection before development |

#### 3. **Educational Content**

Evidently's LLM benchmark guide covers **250 benchmarks** across categories:
- Reasoning (MMLU, ARC, HellaSwag, etc.)
- Math (GSM8K, MATH)
- Coding (HumanEval, MBPP, SWE-bench, etc.)
- Safety (HHH, HELM Safety, etc.)
- Agents/Tools (AgentBench, GAIA, BFCL)

**Value to AIBaaS**: Educational resource, methodology inspiration, but NOT a competitor.

### Competitive Assessment: Evidently AI

**Evidently AI Positioning**:
```
┌──────────────────────────────────────────────────────────────┐
│               Evidently AI Platform                           │
├──────────────────────────────────────────────────────────────┤
│  "Monitor YOUR LLM app in production"                         │
│                                                                │
│  Use Case: Internal Observability                             │
│  - Track YOUR app's performance over time                     │
│  - Detect drift in YOUR model's outputs                       │
│  - Evaluate YOUR RAG system's accuracy                        │
│  - Monitor YOUR production costs                              │
│                                                                │
│  Target User: Teams already using LLMs                        │
│  Stage: Production monitoring (after model selection)         │
└────────────────────────────────────────────────────────────────┘
```

**AIBaaS Positioning**:
```
┌──────────────────────────────────────────────────────────────┐
│               AIBaaS Service                                  │
├──────────────────────────────────────────────────────────────┤
│  "Compare PUBLIC AI providers before choosing"                │
│                                                                │
│  Use Case: Model Selection & Provider Monitoring              │
│  - Which provider is best for code generation?                │
│  - Which model gives best quality per dollar?                 │
│  - Is provider quality degrading over time?                   │
│  - What's the P95 latency for code review tasks?              │
│                                                                │
│  Target User: Teams choosing which AI provider to use         │
│  Stage: Pre-development (model selection phase)               │
└────────────────────────────────────────────────────────────────┘
```

**Competitive Threat Level**: **NONE** (Different Product Category)

**Why Not Competitive**:
1. **Different Lifecycle Stage**:
   - Evidently: AFTER you've chosen a model (production monitoring)
   - AIBaaS: BEFORE you choose a model (selection & comparison)

2. **Different Data Scope**:
   - Evidently: Private, internal (your users, your prompts, your data)
   - AIBaaS: Public, standardized (same benchmarks for all models)

3. **Different Value Prop**:
   - Evidently: "Is MY app working well?"
   - AIBaaS: "Which provider should I choose?"

4. **Complementary, Not Competitive**:
   ```
   Decision Flow:
   1. Use AIBaaS → Choose best provider (e.g., Claude 4.5)
   2. Build app with Claude 4.5
   3. Use Evidently → Monitor your app in production
   ```

**Strategic Recommendation**:

**Position as complementary**:
```markdown
"AIBaaS helps you CHOOSE the right AI provider.
Evidently helps you MONITOR your app after deployment."

"Use both: AIBaaS for selection, Evidently for production."
```

**Partnership Opportunity**:
- Evidently users often ask "which model should I use?" → AIBaaS recommendation
- AIBaaS users ask "how do I monitor my chosen model in production?" → Evidently recommendation
- Mutual referrals, co-marketing

---

## Part 3: LiveBench.ai (Website Validation)

### Website Access Issue

**Technical Limitation**: LiveBench.ai requires JavaScript to render content. WebFetch only retrieved:
- Google Analytics tracking code (`G-VRM4EP2083`)
- "You need to enable JavaScript to run this app" message
- Page title reference

**Previous Research Validation**: Our existing research (`.dev/spikes/aibaas/benchmark-research/05-LiveBench-LiveCodeBench.md`) covered LiveBench extensively. No new data extracted from website.

### Key Findings from Previous Research (Recap)

From our earlier research, LiveBench:
- **Focus**: Contamination-free benchmarks with monthly updates
- **Benchmarks**: Math, coding, reasoning, data analysis, language, instruction following
- **Update Frequency**: Monthly (new problems from recent sources)
- **API**: Limited (HuggingFace datasets)
- **Cost/Latency**: ❌ Not tracked
- **Developer Focus**: ⚠️ Partial (coding is one category among many)

**Competitive Assessment**: Already covered in previous research. LiveBench is an **adjacent competitor** (academic focus, contamination prevention), not a direct threat to AIBaaS.

---

## Synthesis: Impact on AIBaaS Strategy

### Competitive Landscape Update

| Competitor | Category | Threat Level | Why |
|------------|----------|--------------|-----|
| **Vellum AI** | Development Platform | **MODERATE** | Leaderboard is a feature (not core product), static cost data (not dynamic), no historical trends |
| **Evidently AI** | Observability Platform | **NONE** | Different lifecycle stage (production monitoring vs model selection) |
| **LiveBench** | Academic Benchmark | **LOW** | Already analyzed, academic focus, no cost/latency tracking |
| **Hugging Face v2** | Academic Benchmark | **LOW** | Already analyzed, different audience (researchers vs developers) |
| **Aider** | Coding Benchmark | **MODERATE** | Closest competitor, manual updates, no API, no alerting |

### Updated Master Comparison Table

| Feature | Vellum AI | Evidently AI | AIBaaS (Planned) | AIBaaS Advantage |
|---------|-----------|--------------|------------------|------------------|
| **Cost Tracking** | ✅ Static pricing | ✅ Internal only | ✅ Dynamic $/task | Real-time, public |
| **Latency Tracking** | ✅ TTFT, throughput | ✅ Internal only | ✅ P95 percentiles | SLA-focused |
| **Historical Data** | ❌ No | ✅ Internal only | ✅ 1-year retention | Public trends |
| **API Access** | ❌ No (leaderboard) | ✅ Internal only | ✅ REST + GraphQL | Public leaderboard API |
| **Developer Focus** | ⚠️ Partial | ✅ Generic LLMs | ✅ Code-specific | Developer workflows |
| **Alerting** | ❌ No | ✅ Internal only | ✅ Slack, email, webhooks | Public model degradation |
| **Update Frequency** | Manual (monthly?) | Real-time (internal) | Hourly (automated) | Continuous monitoring |
| **Public Leaderboard** | ✅ Yes | ❌ No | ✅ Yes | Transparent, public |
| **Custom Benchmarks** | ⚠️ Via platform | ✅ Yes (core feature) | ✅ Pro tier | Flexibility |

**Key Insight**: Vellum and Evidently serve **different use cases** than AIBaaS:
- **Vellum**: "Build LLM apps" (development platform)
- **Evidently**: "Monitor your LLM app" (observability)
- **AIBaaS**: "Choose the best AI provider" (benchmarking service)

### Recommendations for AIBaaS

#### 1. **No Strategic Changes Needed** ✅

Vellum and Evidently validate our positioning:
- ✅ Cost + latency tracking is valuable (Vellum does it statically, we do it dynamically)
- ✅ Developer focus is right (both target developers)
- ✅ Real-time monitoring is unique (Vellum is static, Evidently is internal-only)

#### 2. **Partnership Opportunities** ⚠️

**Vellum AI**:
- Integrate AIBaaS data into Vellum's model selection UI
- Vellum users get real-time cost/performance data
- AIBaaS gets distribution through Vellum's platform

**Evidently AI**:
- Mutual referrals (AIBaaS → Evidently for production monitoring)
- Co-marketing: "AIBaaS for selection, Evidently for monitoring"

#### 3. **Messaging Refinement** ➕

Add to competitive differentiation:

```markdown
# "Why AIBaaS instead of [competitor]?"

| Competitor | Their Strength | AIBaaS Advantage |
|-----------|---------------|------------------|
| **Vellum AI** | Full development platform | ✅ Dedicated benchmarking (not a side feature)<br>✅ Dynamic $/task (not static pricing)<br>✅ Historical trends (Vellum has none)<br>✅ API access for leaderboard data |
| **Evidently AI** | Production monitoring | ✅ Pre-deployment model selection<br>✅ Public model comparison (not internal-only)<br>✅ Standardized benchmarks (not custom) |
```

#### 4. **Feature Validation** ✅

Vellum and Evidently confirm our planned features are valuable:
- ✅ Cost tracking (Vellum does it, but statically)
- ✅ Latency tracking (both do it, we add percentiles)
- ✅ Developer focus (both target developers)
- ✅ API access (Evidently has it for internal use, we add it for public leaderboard)

---

## Key Takeaways

### 1. **Vellum AI: Moderate Overlap, Different Core Business**

**What They Do Well**:
- Cost transparency (static provider pricing)
- Latency metrics (TTFT, throughput)
- Developer-focused (SWE Bench, HumanEval)
- Full development platform (workflows, agents, deployment)

**Where AIBaaS Wins**:
- Dynamic $/task costs (real runs, not provider pricing)
- Historical trends (Vellum has none)
- API access for leaderboard data (Vellum doesn't offer)
- Alerting system (Vellum has none)
- Dedicated benchmarking focus (Vellum's leaderboard is a feature)

**Positioning**:
```
Vellum = "Build & deploy LLM apps"
AIBaaS = "Monitor & choose AI providers"
```

### 2. **Evidently AI: Different Product Category (Not Competitive)**

**What They Do**:
- Internal LLM app monitoring
- Production observability
- Custom evaluation frameworks
- Drift detection

**Why Not Competitive**:
- Different lifecycle stage (production vs selection)
- Different data scope (internal vs public)
- Different value prop ("Is MY app working?" vs "Which provider is best?")

**Positioning**: Complementary, not competitive. Use AIBaaS BEFORE development, Evidently DURING production.

### 3. **LiveBench: Validates Existing Research**

Couldn't extract new data due to JavaScript requirement, but existing research remains valid.

---

## Conclusion

**Impact on AIBaaS Strategy**: **MINIMAL** ✅

**Changes Required**: **NONE** (strategy validated)

**New Insights**:
1. ✅ Vellum AI validates that cost + latency tracking is valuable (they do it statically, we do it dynamically)
2. ✅ Evidently AI confirms that internal monitoring is different from public benchmarking (complementary products)
3. ✅ Our positioning as "dedicated benchmarking service" is unique (Vellum's leaderboard is a feature, Evidently is internal-only)

**Partnership Opportunities**:
- Vellum AI: Integrate AIBaaS data into their model selection UI
- Evidently AI: Mutual referrals for complementary use cases

**Competitive Moat Strengthened**:
- No existing platform offers dedicated, real-time, public AI provider benchmarking with historical trends and API access
- Vellum comes closest but their leaderboard is a supporting feature, not core product
- Evidently serves different use case (internal monitoring vs public comparison)

**Final Recommendation**: **Proceed with existing AIBaaS strategy** ✅

No major changes needed based on this research.

---

## References

1. **Vellum AI LLM Leaderboard**: https://www.vellum.ai/llm-leaderboard
2. **Vellum AI Cost Comparison**: https://www.vellum.ai/llm-cost-comparison
3. **Vellum AI Open LLM Leaderboard**: https://www.vellum.ai/open-llm-leaderboard
4. **Evidently AI LLM Benchmarks Guide**: https://www.evidentlyai.com/llm-guide/llm-benchmarks
5. **Evidently AI Product**: https://www.evidentlyai.com/
6. **Evidently AI GitHub**: https://github.com/evidentlyai/evidently
7. **LiveBench.ai**: https://livebench.ai/

---

**Document Version**: 1.0.0
**Last Updated**: 2024-11-02
**Next Review**: After Phase 1 implementation
**Status**: ✅ Complete - No strategic changes needed
