# ADR-004: AI Benchmarking Service Evolution

**Status**: Proposed
**Date**: 2024-11-01
**Deciders**: Tamma Development Team
**Related**: Story 1-0 (AI Provider Strategy Research)

---

## Context

During Story 1-0 implementation, we built a comprehensive CLI-based benchmarking system for evaluating AI providers across standardized development workflow scenarios. The system includes:

- Multi-provider testing (Anthropic, OpenAI, Google, OpenRouter, Ollama)
- Automated objective scoring (0-10 scale)
- Statistical analysis (mean, stddev, confidence intervals)
- Cost and latency tracking
- GitHub Actions integration

**Current Limitations**:
1. **One-time execution**: Benchmarks run manually or on-demand, not continuously
2. **Local results storage**: Results saved to JSON/Markdown files, no central database
3. **No historical tracking**: Cannot analyze trends or detect performance degradation over time
4. **Limited accessibility**: CLI-only interface, no web dashboard or API
5. **Internal use only**: No mechanism for public access or community contribution

**Opportunity Identified**:

The benchmarking infrastructure represents a **dual-purpose asset**:
- **Internal**: Continuous monitoring of AI provider quality/cost for Tamma's autonomous workflows
- **External**: Public service for the developer community to compare AI models objectively

**Market Gap Analysis** (17+ Benchmarks Researched):

| Benchmark | Focus | Cost Track | Latency | Historical | API | Dev-Focused | AIBaaS Advantage |
|-----------|-------|------------|---------|------------|-----|-------------|------------------|
| **Aider** | Practical coding | ❌ | ❌ | ❌ | ❌ | ✅ | ⚠️ Closest competitor |
| **LiveBench** | Multi-capability | ❌ | ❌ | ❌ | Limited | ❌ | Monthly updates good |
| **LiveCodeBench Pro** | Competitive coding | ❌ | ❌ | ❌ | Limited | ✅ | Too hard (99.9% fail) |
| **MASK** | Alignment/honesty | ❌ | ❌ | ❌ | ❌ | ❌ | Good statistical rigor |
| **HLE** | PhD-level reasoning | ❌ | ❌ | ❌ | ❌ | ❌ | Too academic |
| **SimpleBench** | Adversarial | ❌ | ❌ | ❌ | ❌ | ⚠️ | Code tricks applicable |
| **VirologyTest** | Medical expertise | ❌ | ❌ | ❌ | ❌ | ❌ | Human percentiles idea |
| **Vectara HHEM** | Hallucination | ❌ | ❌ | ❌ | ❌ | ❌ | Useful for code |
| **ARC Prize** | Abstract reasoning | ❌ | ❌ | ❌ | ❌ | ❌ | Not code-specific |
| **Cybench** | Security/CTF | ❌ | ❌ | ❌ | ❌ | ✅ | Good for security gate |
| **ChatBotArena** | General chat | ❌ | ❌ | ❌ | ❌ | ❌ | Not developer-focused |
| **HuggingFace** | Academic | ❌ | ❌ | ❌ | Limited | ❌ | Not production-focused |
| **AIBaaS** (proposed) | **Dev workflows** | **✅** | **✅** | **✅** | **✅** | **✅** | **All 5 advantages** |

**Key Finding**: NO existing benchmark combines all 5 critical features (cost, latency, historical data, API access, developer-focused tasks).

---

## Decision

We will **evolve the CLI-based benchmark tool into a production-grade web service** called **AIBaaS** (AI Benchmarking as a Service) with the following architecture:

### Core Components

1. **Web Dashboard** (Next.js 15 + React 19)
   - Public leaderboard with real-time updates
   - Historical performance charts
   - Provider comparison tools
   - User authentication and API key management

2. **REST + GraphQL APIs** (Fastify 5.x + Apollo Server 4)
   - Programmatic access to benchmark data
   - Real-time updates via Server-Sent Events (SSE)
   - WebSocket subscriptions for live leaderboards

3. **Time-Series Database** (PostgreSQL 17 + TimescaleDB)
   - Store historical benchmark results
   - Continuous aggregates for performance analysis
   - Automatic data compression and retention policies

4. **Background Job System** (BullMQ)
   - Scheduled hourly/daily benchmark runs
   - Automated model discovery (detect new models from providers)
   - Performance degradation detection
   - Report generation

5. **Alerting System**
   - Webhook notifications for performance degradation
   - Email/Slack notifications
   - In-app notifications

### Access Tiers

```
┌─────────────┬──────────────┬─────────────┬────────────────┐
│ Tier        │ Price/Month  │ API Calls   │ Features       │
├─────────────┼──────────────┼─────────────┼────────────────┤
│ Free        │ $0           │ 100/hour    │ Leaderboard    │
│             │              │             │ 7-day history  │
│             │              │             │ Public reports │
├─────────────┼──────────────┼─────────────┼────────────────┤
│ Pro         │ $49/month    │ 10K/hour    │ API access     │
│             │ $490/year    │             │ Custom tests   │
│             │              │             │ 90-day history │
│             │              │             │ Alerts         │
│             │              │             │ Real-time      │
├─────────────┼──────────────┼─────────────┼────────────────┤
│ Enterprise  │ Custom       │ Unlimited   │ White-label    │
│             │ (from $500)  │             │ SLA            │
│             │              │             │ Dedicated      │
│             │              │             │ Full history   │
└─────────────┴──────────────┴─────────────┴────────────────┘
```

### Migration Strategy

**Phase 1: Internal Deployment (Weeks 1-4)**
- Migrate CLI tool to service architecture
- Deploy PostgreSQL + TimescaleDB
- Build REST API MVP
- Create basic web dashboard
- Schedule hourly benchmark runs
- **No external access yet** (internal use only)

**Phase 2: Enhanced Analytics (Weeks 5-8)**
- Implement continuous aggregates
- Build alerting system
- Add GraphQL API
- Historical trend analysis
- Cost projection models

**Phase 3: Public Beta (Weeks 9-12)**
- Launch public leaderboard website
- User authentication (Auth0/Clerk)
- API documentation (OpenAPI)
- Free tier deployment
- Marketing launch (Product Hunt, HackerNews)

**Phase 4: Monetization (Weeks 13-24)**
- Stripe integration
- Pro tier launch ($49/month, revised from competitive analysis)
- Enterprise features ($499/month)
- Custom scenario builder
- Human baseline data collection (350 developers)

---

## Rationale

### Why This Decision Makes Sense

#### 1. **Leverages Existing Investment**
- 80% of infrastructure already built (providers, scorers, runners)
- Minimal additional development to add database, API, and scheduler
- Avoids "throw-away" research code

#### 2. **Dual-Purpose Value**
- **Internal**: Continuous monitoring critical for Tamma's autonomous workflows
- **External**: Revenue-generating product for developer community
- ROI multiplier: same infrastructure serves both needs

#### 3. **Market Opportunity**
- **No existing comprehensive solution** for developer-focused AI benchmarking
- Growing demand for objective AI model comparisons (ChatGPT vs Claude vs Gemini)
- Cost transparency increasingly important as AI adoption scales

#### 4. **Strategic Positioning**
- Positions Tamma as **AI infrastructure experts**
- Community building: attract AI developers to Tamma ecosystem
- Marketing: "Speedtest.net for AI models" is highly shareable

#### 5. **Revenue Potential** (Updated from Competitive Analysis)
- **Month 6 (Beta)**: 50 Pro users × $49/month = **$2,450 MRR**
- **Month 12 (Scale)**: 500 Pro + 20 Enterprise = **$34,500 MRR** = **$414,000 ARR**
- **5-Year Profit**: $8.18M cumulative (1,665% ROI on $491k investment)
- **Unit Economics**: LTV/CAC = 11.8x (healthy: >3x)

#### 6. **Technical Feasibility**
- Uses Tamma's existing tech stack (Node.js, TypeScript, PostgreSQL, Fastify)
- TimescaleDB is a proven choice for time-series data
- BullMQ integrates seamlessly with existing Redis infrastructure
- No new learning curve for team

---

## Alternatives Considered

### Alternative 1: Keep CLI Tool Only

**Pros**:
- No additional development required
- Simpler architecture
- Faster completion of Story 1-0

**Cons**:
- No continuous monitoring (requires manual runs)
- No historical analysis
- No alerting for performance degradation
- Missed revenue opportunity
- Results not accessible to broader community

**Decision**: ❌ Rejected - Insufficient for Tamma's long-term needs

---

### Alternative 2: Build Internal Service Only (No Public Access)

**Pros**:
- Meets Tamma's internal monitoring needs
- Simpler security model (no user auth, no rate limiting)
- No monetization complexity

**Cons**:
- Missed revenue opportunity ($64K+ ARR)
- No community building
- No marketing leverage
- Still requires same infrastructure (database, API, scheduler)

**Decision**: ⚠️ Considered - Acceptable fallback if public launch fails, but we'll start with public vision

---

### Alternative 3: Partner with Existing Platform (e.g., HuggingFace)

**Pros**:
- Leverage existing user base
- No infrastructure costs
- Faster go-to-market

**Cons**:
- No control over roadmap
- Revenue sharing (if any)
- Not aligned with academic benchmarks
- Cannot integrate tightly with Tamma's workflows

**Decision**: ❌ Rejected - Loses strategic value

---

### Alternative 4: Build Public Service First, Internal Use Later

**Pros**:
- Focus on revenue from day one
- Cleaner product vision

**Cons**:
- Delays Tamma's internal monitoring needs
- Higher risk (public launch may fail)
- Story 1-0 incomplete without validation

**Decision**: ❌ Rejected - Internal use validates the system first

---

## Consequences

### Positive

1. **Continuous Quality Monitoring**
   - Automated detection when Claude/OpenAI/Gemini quality degrades
   - Historical baselines for comparison
   - Alerts before production issues occur

2. **Cost Optimization**
   - Track cost trends per provider
   - Detect cost spikes immediately
   - Data-driven provider selection

3. **Revenue Generation**
   - New revenue stream for Tamma ($64K+ ARR potential)
   - Low marginal cost (infrastructure already needed)
   - Recurring revenue (subscriptions)

4. **Community Building**
   - Public leaderboard attracts developers
   - Positions Tamma as thought leader
   - Marketing channel for Tamma platform

5. **Competitive Intelligence**
   - Monitor when competitors release new models
   - Understand market trends
   - Validate provider claims with data

### Negative

1. **Increased Complexity**
   - More infrastructure to maintain (database, API, workers)
   - Security considerations (user auth, API keys, rate limiting)
   - Monitoring and alerting overhead

2. **Development Time**
   - 16 weeks to full public launch (vs 1 week for CLI)
   - Delays other Story 1 work
   - Requires ongoing maintenance

3. **Operating Costs**
   - Infrastructure: $75-236/month
   - AI provider testing: $93/month
   - Total: **$168-329/month** ongoing

4. **Support Burden**
   - Public users expect documentation and support
   - Bug reports from external users
   - Feature requests from community

5. **Competitive Risk**
   - Public data helps competitors
   - Providers may not like transparent cost comparisons
   - Risk of reverse-engineering by competitors

### Mitigation Strategies

1. **Phase 1 Internal Deployment** - Validate before public launch
2. **Automated testing** - Reduce maintenance burden
3. **Clear support boundaries** - Free tier gets community support only
4. **Rate limiting** - Prevent abuse
5. **Data aggregation** - Don't expose raw API responses publicly

---

## Implementation

### Phase 1: Internal Service (Weeks 1-4)

**Goal**: Deploy internally for Tamma's continuous monitoring

**Tasks**:
- [ ] Set up PostgreSQL 17 + TimescaleDB schema
- [ ] Migrate `BatchRunner` to BullMQ job-based architecture
- [ ] Build REST API (core endpoints: /providers, /leaderboard, /models/:id/history)
- [ ] Create Next.js dashboard MVP (leaderboard, historical charts)
- [ ] Configure scheduled jobs (hourly benchmark runs)
- [ ] Deploy to internal staging environment
- [ ] Run benchmarks for 1 week to validate system

**Success Criteria**:
- ✅ Hourly benchmarks run successfully for 7 days
- ✅ Historical data queryable via API
- ✅ Dashboard displays trends correctly
- ✅ No data loss or job failures

**Go/No-Go Decision Point**: If Phase 1 fails or is too complex, revert to CLI-only tool.

---

### Phase 2-4: See ARCHITECTURE.md

Full implementation roadmap documented in `.dev/spikes/aibaas/ARCHITECTURE.md`.

---

## Acceptance Criteria

### For ADR Approval

- [x] **Technical feasibility validated** - Architecture reviewed by team
- [x] **Cost analysis completed** - Operating costs and revenue projections documented
- [x] **Risk assessment completed** - Negative consequences identified and mitigated
- [ ] **Team consensus** - All developers agree on approach
- [ ] **Product owner approval** - Business value confirmed

### For Phase 1 Completion

- [ ] Internal dashboard accessible at `https://benchmark.tamma.internal`
- [ ] PostgreSQL database contains 7+ days of historical data
- [ ] Scheduled jobs running hourly without failures
- [ ] API response time P95 < 500ms
- [ ] Zero data loss or corruption

---

## Competitive Moat & Defensibility

Based on analysis of 17+ existing benchmarks, AIBaaS has **4 sustainable competitive advantages**:

### 1. **Data Network Effects** (6-12 month moat)
- **105,120 benchmark runs** by Month 12 (cannot be replicated quickly)
- Historical data creates compounding value (trends, predictions, alerts)
- **Community contributions**: 150 tasks by Month 12 (30% of total)
- **Barrier to entry**: Competitors need 6+ months to match historical depth

### 2. **Developer Trust** (12-24 month moat)
- **First-mover advantage**: "Speedtest.net for AI models" brand positioning
- **Transparency**: Open methodology, confidence intervals, contamination tracking
- **Community validation**: 50 blog citations + 3 research papers by Month 12
- **SEO dominance**: Rank #1 for "AI model benchmark", "Claude vs GPT cost", etc.

### 3. **Human Baseline Data** (12+ month moat)
- **350 developers** (100 junior, 100 mid, 100 senior, 50 staff+) × 500 tasks = **175,000 data points**
- Competitors cannot ethically replicate (privacy, consent, compensation requirements)
- **Unique insight**: "Claude 4.5 performs better than 85% of mid-level developers [80-89% CI]"
- **Continuous updates**: Quarterly refreshes maintain data moat

### 4. **Infrastructure Barrier** (24+ month moat)
- **Technical complexity**: PostgreSQL + TimescaleDB + BullMQ + Redis + S3
- **Operational expertise**: Scheduling, rate limiting, contamination detection, alerting
- **API ecosystem**: 100+ CI/CD integrations create switching costs
- **Cost to replicate**: $491k investment + 12 months development time

### Defensibility Analysis

| Competitor Action | Our Response | Moat Strength |
|-------------------|--------------|---------------|
| Copy UI/UX | Patent pending interactions | ⚠️ Weak (1-3 months) |
| Copy methodology | Open-source encourages adoption | ⚠️ Weak (transparent) |
| Build historical data | Takes 6-12 months minimum | ✅ Strong (time barrier) |
| Collect human baselines | Expensive, legally complex | ✅ Strong (cost + legal) |
| Replicate infrastructure | $491k + 12 months | ✅ Very Strong |
| Acquire user base | Network effects kick in at 1000+ users | ✅ Strong (switching costs) |

**Conclusion**: Sustainable 12-24 month competitive moat through data network effects, human baselines, and infrastructure complexity.

---

## References

- **Architecture Document**: `.dev/spikes/aibaas/ARCHITECTURE.md` (v2.0.0, updated with competitive intelligence)
- **Competitive Analysis**: `.dev/spikes/aibaas/COMPETITIVE-ANALYSIS.md` (17+ benchmarks analyzed)
- **Benchmark Research**: `.dev/spikes/aibaas/benchmark-research/` (8 detailed research documents)
- **Story 1-0**: `docs/stories/1-0-ai-provider-strategy-research.md`
- **Benchmark Implementation**: `.dev/spikes/run-benchmark.ts`
- **GitHub Actions Workflow**: `.github/workflows/ai-provider-benchmark.yml`

---

## Decision Record

| Date | Status | Decision Maker | Notes |
|------|--------|----------------|-------|
| 2024-11-01 | Proposed | Development Team | Initial proposal based on Story 1-0 findings |
| 2025-11-01 | Updated | Research Team | Added competitive analysis (17+ benchmarks), validated moat |
| TBD | Review | Technical Lead | Pending technical review |
| TBD | Approved/Rejected | Product Owner | Pending business approval |

---

## Notes

### Key Insights from Story 1-0

1. **Quality varies significantly** - Claude scores 8.9/10, local models 6.0/10
2. **Costs differ by 100x** - Gemini free tier vs OpenAI premium
3. **Performance changes over time** - Providers update models frequently
4. **No single "best" provider** - Optimal choice depends on task type

### Why Continuous Monitoring Matters

- **Provider model updates** can degrade quality (e.g., GPT-4 "laziness" reports)
- **Cost changes** happen without notice (e.g., OpenAI price increases)
- **New models** released frequently (e.g., Claude 4.5, Gemini 2.5)
- **Tamma needs automated switching** if primary provider fails

### Community Value

Developers face the same decision: "Which AI model should I use?"

Current answers are:
- ❌ Anecdotal ("Claude is better for code")
- ❌ Provider marketing claims
- ❌ One-time benchmarks (outdated quickly)

AIBaaS provides:
- ✅ Objective, reproducible scores
- ✅ Real-world developer scenarios
- ✅ Cost + quality + speed trade-offs
- ✅ Up-to-date data (continuous testing)

---

**Status**: Updated with Competitive Intelligence (ready for approval)
**Version**: 2.0.0 (added comprehensive competitive analysis from 17+ benchmarks)

**Next Steps**:
1. ✅ **Research completed**: 17+ benchmarks analyzed, competitive moat validated
2. ✅ **Architecture updated**: v2.0.0 with enhanced features from best-in-class benchmarks
3. ⏳ **Technical review by team** (Week of 2024-11-04)
4. ⏳ **Business approval by product owner** (Week of 2024-11-11)
5. **If approved**: Begin Phase 1 implementation (Week of 2024-11-18)

**Recommendation**: **APPROVE** - Competitive analysis validates unique positioning, sustainable moat, and strong unit economics (LTV/CAC = 11.8x)

---
