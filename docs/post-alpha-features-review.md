# Post-Alpha Features Review: Unaddressed Out-of-Scope Items

**Generated**: 2025-10-28
**Status**: Needs Review
**Epic Coverage**: Epics 1-5 (All contexted)

---

## Executive Summary

Analysis of all Epic technical specifications (1-5) reveals **27 features explicitly marked as out-of-scope** across all epics that remain **unaddressed in the alpha release**. These items represent strategic decisions to defer functionality to post-alpha iterations, future epics, or community-driven extensibility.

**Key Findings**:
- Alpha focuses on **single-tenant, English-only, Claude Code + GitHub/GitLab** deployment
- Most deferred items fall into **extensibility, enterprise features, and advanced intelligence**
- Several items will require **significant architectural changes** if added later (multi-tenancy, multi-repo support)
- Post-alpha priorities should be driven by **early adopter feedback** and **adoption blockers**

---

## 1. Extensibility & Plugin Systems (4 items)

**Status**: Deferred to future epics (post-alpha)
**Priority**: HIGH (critical for broad adoption)

### Items

1. **Additional AI Providers Beyond Anthropic Claude** (Epic 1)
   - **Current State**: Anthropic Claude API via SDK (headless calls) - Story 1-2
   - **Research Completed**: Story 1-0 created to research cost models, subscription plans, and provider selection strategy
   - **Deferred Providers**: OpenAI, GitHub Copilot, Google Gemini, OpenCode, z.ai, Zen MCP, OpenRouter, local models (Ollama/LM Studio/vLLM)
   - **Implementation Plan**: Story 1-10 created to implement all 8 deferred providers following IAIProvider interface
   - **Architectural Impact**: Epic 1's `IAIProvider` interface ready, each provider needs implementation and testing
   - **Effort**: 2-3 weeks per provider (provider adapter + integration tests). Story 1-10 covers all 8 providers.
   - **User Demand**: HIGH (users want choice, cost flexibility, subscription plan support)

2. **Additional Git Platforms Beyond GitHub/GitLab** (Epic 1)
   - **Current State**: GitHub (REST + GraphQL) and GitLab (REST) implemented - Stories 1-5 and 1-6
   - **Deferred Platforms**: Gitea, Forgejo, Bitbucket, Azure DevOps, plain Git
   - **Implementation Plan**: Story 1-11 created to implement all 5 deferred platforms following IGitPlatform interface
   - **Architectural Impact**: Epic 1's `IGitPlatform` interface ready, each platform needs implementation and API quirk handling
   - **Effort**: 1-2 weeks per platform (API adapter + platform-specific tests). Story 1-11 covers all 5 platforms.
   - **Priority Order**: Gitea/Forgejo (self-hosted users), Bitbucket (Atlassian enterprises), Azure DevOps (Microsoft enterprises), Plain Git (minimal setups)
   - **User Demand**: MEDIUM-HIGH (Gitea/Forgejo for self-hosted, Bitbucket/Azure DevOps for enterprises)

3. **Custom Quality Gate Plugin System** (Epic 3)
   - **Current State**: Hardcoded quality gates (build, test, static analysis, security scan)
   - **Deferred Capability**: User-defined quality gates via plugin API
   - **Architectural Impact**: Requires plugin lifecycle management (register, execute, retry, escalate)
   - **Effort**: 3-4 weeks (plugin SDK + examples + documentation)
   - **User Demand**: Medium (power users want custom checks, e.g., performance benchmarks, API contract tests)

4. **Custom Dashboard Widget Framework** (Epic 5)
   - **Current State**: Fixed dashboard layout (health, velocity, event trail, feedback)
   - **Deferred Capability**: User-defined dashboard widgets via React component API
   - **Architectural Impact**: Requires widget registry, data source API, layout management
   - **Effort**: 2-3 weeks (widget SDK + examples + documentation)
   - **User Demand**: Low (alpha users unlikely to need custom widgets immediately)

### Recommendation

**Post-Alpha Phase 1** (Months 1-3):
- Prioritize **AI provider extensibility** (highest user demand)
- Add **GitHub Copilot** and **Anthropic API** providers first (most requested)
- Defer plugin systems until Phase 2 unless early adopters specifically request

---

## 2. Multi-Tenancy & Enterprise Features (3 items)

**Status**: Single-tenant deployment assumed for alpha
**Priority**: HIGH (enterprise adoption blocker)

### Items

1. **Multi-Tenant Orchestrator** (Epic 2)
   - **Current State**: Single orchestrator instance serves one team/organization
   - **Deferred Capability**: Multiple organizations sharing orchestrator with data isolation
   - **Architectural Impact**: Requires tenant ID in all database tables, workflow state, agent pools, approval queues
   - **Effort**: 4-6 weeks (data model refactor + tenant isolation + RBAC)
   - **User Demand**: HIGH for SaaS deployment, LOW for self-hosted

2. **Multi-Tenancy Event Isolation** (Epic 4)
   - **Current State**: Single event stream for all workflows
   - **Deferred Capability**: Per-tenant event streams with cross-tenant query prevention
   - **Architectural Impact**: Requires tenant ID in event store schema, query API filtering, replay isolation
   - **Effort**: 2-3 weeks (schema migration + query isolation + tests)
   - **User Demand**: HIGH for SaaS, LOW for self-hosted

3. **Multi-Tenant Observability Isolation** (Epic 5)
   - **Current State**: Single dashboard displays all metrics
   - **Deferred Capability**: Per-tenant dashboards with metric filtering
   - **Architectural Impact**: Requires tenant ID in metrics labels, dashboard authentication with tenant context
   - **Effort**: 2-3 weeks (metric label refactor + dashboard filtering + JWT tenant claims)
   - **User Demand**: HIGH for SaaS, LOW for self-hosted

### Recommendation

**Decision Point**: Determine if Tamma will be offered as **SaaS** or **self-hosted only**.

- **If SaaS planned**: Multi-tenancy is **Phase 1 priority** (Months 1-2) - required for launch
- **If self-hosted only**: Defer multi-tenancy to **Phase 3+** (Month 6+) - only if community requests shared hosting

**Estimated Total Effort**: 8-12 weeks for full multi-tenancy across all epics

---

## 3. Advanced Intelligence & ML Features (4 items)

**Status**: Post-MVP enhancements
**Priority**: MEDIUM (nice-to-have, not blockers)

### Items

1. **Advanced ML-Based Ambiguity Detection Models** (Epic 3)
   - **Current State**: Rule-based ambiguity scoring (keyword patterns, question marks, vague terms)
   - **Deferred Capability**: Fine-tuned ML model for ambiguity classification (confidence scores)
   - **Architectural Impact**: Requires ML model hosting (local or cloud), training pipeline, continuous learning
   - **Effort**: 6-8 weeks (data collection + model training + integration + evaluation)
   - **User Demand**: LOW (rule-based sufficient for alpha)

2. **Code Review Agent Integration** (Epic 3)
   - **Current State**: No automated code review beyond static analysis
   - **Deferred Capability**: AI agent specialized in code review (security, performance, best practices)
   - **Architectural Impact**: New agent role in Epic 2 orchestrator, integration with PR workflow before merge
   - **Effort**: 3-4 weeks (agent implementation + review criteria + integration)
   - **User Demand**: MEDIUM (users trust manual review initially, but AI review valuable for velocity)

3. **Performance Profiling and Optimization Suggestions** (Epic 3)
   - **Current State**: No performance profiling in autonomous loop
   - **Deferred Capability**: AI-generated performance optimizations (identify bottlenecks, suggest fixes)
   - **Architectural Impact**: Requires profiling tool integration (CPU, memory, I/O), AI analysis of profiles
   - **Effort**: 4-5 weeks (profiler integration + AI prompt engineering + validation)
   - **User Demand**: LOW (optimization rarely needed in alpha phase)

4. **Machine Learning-Based Anomaly Detection** (Epic 5)
   - **Current State**: Threshold-based alerting (e.g., alert if escalations > 5/hour)
   - **Deferred Capability**: ML model learns normal behavior, alerts on statistical anomalies
   - **Architectural Impact**: Requires ML model hosting, historical data for training, real-time inference
   - **Effort**: 6-8 weeks (data collection + model training + integration + tuning)
   - **User Demand**: LOW (threshold alerts sufficient for alpha)

### Recommendation

**Defer all to Phase 3+** (Month 6+). Focus post-alpha efforts on extensibility and enterprise features first. Revisit ML features if early adopters report specific pain points (e.g., "Too many false escalations" → ML anomaly detection).

---

## 4. Infrastructure & Operations (6 items)

**Status**: User-managed infrastructure
**Priority**: LOW (alpha users expected to self-manage)

### Items

1. **Production-Grade Monitoring Setup** (Epic 5)
   - **Current State**: Tamma exposes `/metrics` endpoint, users deploy Prometheus/Grafana themselves
   - **Deferred Capability**: Bundled monitoring stack (Prometheus + Grafana + Alertmanager + ELK)
   - **Architectural Impact**: Docker Compose with monitoring services, pre-configured dashboards
   - **Effort**: 1-2 weeks (Docker Compose + Grafana dashboards + documentation)
   - **User Demand**: MEDIUM (reduces setup friction, but users may prefer existing monitoring)

2. **Event Schema Migration Automation** (Epic 4)
   - **Current State**: Manual SQL migration scripts for event schema changes
   - **Deferred Capability**: Automated migration tool (versioned migrations, rollback support)
   - **Architectural Impact**: Migration framework (like Flyway/Liquibase), version tracking table
   - **Effort**: 2-3 weeks (migration tool integration + testing + documentation)
   - **User Demand**: LOW (alpha unlikely to require many schema changes)

3. **Advanced Blob Storage Backends (S3/Azure Blob)** (Epic 4)
   - **Current State**: Local filesystem for large event payloads (code diffs, binary artifacts)
   - **Deferred Capability**: Cloud blob storage (S3, Azure Blob, GCS) with lifecycle policies
   - **Architectural Impact**: Abstraction layer for blob storage (local vs. cloud), configuration management
   - **Effort**: 2-3 weeks (blob storage interface + S3/Azure implementations + tests)
   - **User Demand**: MEDIUM (cloud deployments need scalable blob storage)

4. **Event Encryption at Rest** (Epic 4)
   - **Current State**: PostgreSQL Transparent Data Encryption (TDE) acceptable for alpha
   - **Deferred Capability**: Application-level encryption for event payloads (field-level encryption)
   - **Architectural Impact**: Key management system (KMS), encryption/decryption in event store
   - **Effort**: 3-4 weeks (KMS integration + encryption layer + key rotation + tests)
   - **User Demand**: LOW for alpha, HIGH for enterprise compliance (HIPAA, SOC2)

5. **Event Compression for Long-Term Storage** (Epic 4)
   - **Current State**: Events stored uncompressed in PostgreSQL JSONB
   - **Deferred Capability**: Automatic compression for old events (e.g., >90 days old)
   - **Architectural Impact**: Background job for compression, decompression on query
   - **Effort**: 1-2 weeks (compression job + decompression logic + tests)
   - **User Demand**: LOW (optimize post-MVP based on actual data volumes)

6. **Multi-Platform CI/CD Orchestration Beyond GitHub Actions** (Epic 2)
   - **Current State**: GitHub Actions only for build/test execution
   - **Deferred Capability**: GitLab CI, CircleCI, Jenkins, Azure Pipelines support
   - **Architectural Impact**: Epic 3 quality gates need platform-agnostic CI/CD API
   - **Effort**: 2-3 weeks per platform (CI/CD adapter + platform-specific tests)
   - **User Demand**: MEDIUM (GitLab users need GitLab CI, but GitHub Actions covers 80%+ of users)

### Recommendation

**Phase 2** (Months 3-4):
- Add **cloud blob storage** (S3/Azure) if early adopters deploy to AWS/Azure
- Add **GitLab CI** support if GitLab platform usage is high
- Defer monitoring stack, encryption, compression until **Phase 3+** unless specific user requests

---

## 5. Advanced Observability (5 items)

**Status**: MVP observability only
**Priority**: LOW (basic metrics sufficient for alpha)

### Items

1. **Distributed Tracing with OpenTelemetry** (Epic 5)
   - **Current State**: Correlation IDs for request tracing across services
   - **Deferred Capability**: Full distributed tracing (spans, trace visualization, latency analysis)
   - **Architectural Impact**: OpenTelemetry SDK integration, trace exporter (Jaeger/Zipkin/Tempo)
   - **Effort**: 3-4 weeks (OTel integration + span instrumentation + trace backend + visualization)
   - **User Demand**: LOW for alpha, MEDIUM for production debugging

2. **Real-User Monitoring (RUM) for Web Dashboard** (Epic 5)
   - **Current State**: Backend observability only (server metrics, logs)
   - **Deferred Capability**: Frontend performance monitoring (Core Web Vitals, JS errors, user sessions)
   - **Architectural Impact**: RUM SDK integration (Sentry, Datadog RUM, New Relic Browser), privacy considerations
   - **Effort**: 1-2 weeks (RUM SDK integration + dashboard performance tracking)
   - **User Demand**: LOW (dashboard performance not critical for alpha)

3. **A/B Testing Framework for Autonomous Loop Variations** (Epic 5)
   - **Current State**: Single autonomous loop implementation
   - **Deferred Capability**: Experiment framework to test workflow variations (e.g., different retry strategies)
   - **Architectural Impact**: Experiment configuration, user bucketing, metrics comparison
   - **Effort**: 3-4 weeks (A/B framework + experiment SDK + statistical analysis)
   - **User Demand**: LOW (product optimization feature, not needed for alpha)

4. **Advanced Analytics (Cohort Analysis, Retention Curves)** (Epic 5)
   - **Current State**: Basic metrics (issue count, PR count, velocity trends)
   - **Deferred Capability**: User cohort analysis, retention tracking, funnel analysis
   - **Architectural Impact**: Analytics database (data warehouse), ETL pipeline, visualization tools
   - **Effort**: 4-6 weeks (data warehouse + ETL + analytics dashboards + reports)
   - **User Demand**: LOW (product analytics, not needed for alpha)

5. **GraphQL Query API for Events** (Epic 4)
   - **Current State**: REST API for event queries (`GET /api/v1/events`)
   - **Deferred Capability**: GraphQL API for flexible event queries (reduce over-fetching)
   - **Architectural Impact**: GraphQL schema for events, GraphQL server (Apollo/GraphQL Yoga)
   - **Effort**: 2-3 weeks (GraphQL schema + resolvers + query optimization + documentation)
   - **User Demand**: LOW (REST API sufficient for alpha dashboard)

### Recommendation

**Defer all to Phase 3+** (Month 6+). Basic observability (logs, metrics, event trail) is sufficient for alpha. Only add advanced observability if early adopters report specific needs (e.g., "Cannot debug distributed workflows" → OpenTelemetry tracing).

---

## 6. User Experience Enhancements (3 items)

**Status**: Deferred to Phase 2 or post-alpha
**Priority**: LOW (alpha focuses on core functionality)

### Items

1. **Advanced CLI Features (Interactive Workflows, TUI Dashboards)** (Epic 1)
   - **Current State**: Basic CLI commands (`tamma start`, `tamma replay`, etc.)
   - **Deferred Capability**: Interactive CLI (prompts, progress bars, TUI dashboard with real-time updates)
   - **Architectural Impact**: TUI framework (Ink, Blessed), interactive state management
   - **Effort**: 2-3 weeks (TUI framework + interactive commands + styling)
   - **User Demand**: MEDIUM (better CLI UX appreciated, but not blocker)

2. **Mobile App for Dashboard (iOS/Android)** (Epic 5)
   - **Current State**: Responsive web dashboard (works on mobile browsers)
   - **Deferred Capability**: Native mobile apps (React Native, Flutter)
   - **Architectural Impact**: Mobile app development, app store distribution, push notifications
   - **Effort**: 8-12 weeks (mobile app + platform-specific features + app store submission)
   - **User Demand**: LOW (web dashboard sufficient for alpha)

3. **Cost Tracking and Billing Integration** (Epic 5)
   - **Current State**: Telemetry collected (AI token usage, API calls), but no cost calculation
   - **Deferred Capability**: Cost tracking dashboard (per-issue cost, monthly spend), billing integration (Stripe)
   - **Architectural Impact**: Cost calculation logic, pricing model, billing system integration
   - **Effort**: 3-4 weeks (cost tracking + dashboard + billing integration)
   - **User Demand**: LOW for alpha, HIGH for SaaS offering

### Recommendation

**Phase 2** (Months 3-4):
- Add **interactive CLI** if early adopters report poor CLI UX
- Defer mobile app and billing to **Phase 4+** (Month 9+) unless SaaS launch requires billing

---

## 7. Multi-Repository & Multi-Language (2 items)

**Status**: Single repo, English only for alpha
**Priority**: HIGH (adoption blocker for some users)

### Items

1. **Multi-Repository Workflows** (Epic 2 Assumption)
   - **Current State**: Each autonomous loop targets single Git repository
   - **Deferred Capability**: Microservices workflows (coordinate changes across multiple repos)
   - **Architectural Impact**: Multi-repo workflow orchestration, dependency graph, atomic commits across repos
   - **Effort**: 6-8 weeks (multi-repo workflow design + coordination logic + tests)
   - **User Demand**: HIGH for microservices teams, LOW for monolith teams

2. **Multi-Language Support (Non-English)** (Epic 2 Assumption)
   - **Current State**: Issue descriptions, code comments, AI-generated content assumed English
   - **Deferred Capability**: Support for non-English repositories (code, comments, prompts)
   - **Architectural Impact**: Multi-language AI prompts, code understanding in non-English comments
   - **Effort**: 4-6 weeks (prompt translation + AI provider multi-language support + tests)
   - **User Demand**: MEDIUM (international teams need non-English support)

### Recommendation

**Phase 1-2** (Months 1-4):
- **Multi-repo support** is critical for microservices adoption - prioritize if early feedback indicates this is blocker
- **Multi-language support** can wait until Phase 3+ unless significant international user demand

---

## Strategic Implications

### Alpha Release Scope Validation

✅ **Alpha Scope is Well-Defined**: Core autonomous loop with Claude Code + GitHub/GitLab
✅ **Deferred Items are Strategic**: Focus on MVP, defer complexity and enterprise features
✅ **Technical Debt Manageable**: Most deferred items can be added without major refactoring (except multi-tenancy, multi-repo)

### Post-Alpha Priority Framework

**Phase 1 (Months 1-3): Extensibility & Adoption Blockers**
1. Additional AI providers (GitHub Copilot, Anthropic API)
2. Multi-tenant architecture (if SaaS planned)
3. Multi-repository workflows (if microservices adoption critical)
4. GitLab CI support (if GitLab users significant)

**Phase 2 (Months 3-6): Enterprise & Operations**
1. Cloud blob storage (S3/Azure)
2. Interactive CLI/TUI
3. Additional Git platforms (Bitbucket, Azure DevOps)
4. Event schema migration automation

**Phase 3 (Months 6-9): Advanced Features**
1. Multi-language support (if international demand)
2. Distributed tracing (OpenTelemetry)
3. Custom quality gate plugins
4. Code review agent

**Phase 4 (Months 9-12): Optimization & Scale**
1. ML-based intelligence features
2. Advanced analytics
3. Custom dashboard widgets
4. Cost tracking and billing (if SaaS)

### Decision Gates

**Before Phase 1**:
- [ ] Survey early adopters for top feature requests
- [ ] Determine SaaS vs. self-hosted strategy (affects multi-tenancy priority)
- [ ] Measure AI provider usage (Claude Code adoption rate)
- [ ] Analyze repository types (monolith vs. microservices split)

**Before Phase 2**:
- [ ] Review technical debt from alpha (any architectural refactors needed?)
- [ ] Validate Phase 1 features with users (did extensibility unblock adoption?)
- [ ] Re-prioritize based on adoption metrics

**Before Phase 3**:
- [ ] Evaluate enterprise customer pipeline (does it justify enterprise features?)
- [ ] Assess competition landscape (what features differentiate Tamma?)

---

## Recommendations

### Immediate Actions (Post-Alpha Release)

1. **Create `ROADMAP.md`** - Public roadmap with Phase 1-4 features for transparency
2. **User Feedback Survey** - Survey first 100 alpha users on top feature requests
3. **Usage Analytics** - Instrument telemetry to track:
   - AI provider usage (Claude Code vs. others)
   - Git platform usage (GitHub vs. GitLab ratio)
   - Repository types (monolith vs. microservices)
   - Workflow failures (escalation reasons, retry patterns)
4. **Technical Debt Review** - Identify any alpha implementation shortcuts requiring Phase 1 refactoring

### Post-Alpha Epic Planning

**Candidate Epics for Post-Alpha**:

- **Epic 6: Extensibility Framework** - AI provider plugins, Git platform plugins, quality gate plugins
- **Epic 7: Enterprise & Multi-Tenancy** - Multi-tenant architecture, RBAC, SSO, audit logs
- **Epic 8: Multi-Repository Orchestration** - Microservices workflows, cross-repo coordination, monorepo support
- **Epic 9: Advanced Intelligence** - ML-based features, code review agent, performance profiling
- **Epic 10: Production Operations** - Cloud infrastructure, advanced observability, cost optimization

### Success Metrics for Post-Alpha Features

**Track adoption impact of each feature**:
- **AI Provider Extensibility**: % users using non-Claude Code providers
- **Multi-Tenancy**: # organizations sharing single Tamma instance
- **Multi-Repo Support**: % workflows spanning multiple repositories
- **Advanced Observability**: Reduction in MTTR (mean time to resolution) for workflow issues

---

## Appendix: Complete Out-of-Scope Item List

### Epic 1: Foundation & Core Infrastructure (3 items)
1. Additional AI providers beyond Claude Code
2. Additional Git platforms beyond GitHub/GitLab
3. Advanced CLI features (interactive workflows, TUI dashboards)

### Epic 2: Autonomous Development Workflow (2 items)
1. Multi-repository workflows (microservices coordination)
2. Multi-language support (non-English repositories)

### Epic 3: Intelligence & Quality Enhancement (5 items)
1. Advanced ML-based ambiguity detection models
2. Code review agent integration
3. Performance profiling and optimization suggestions
4. Custom quality gate plugin system
5. Multi-platform CI/CD beyond GitHub Actions

### Epic 4: Event Sourcing & Time-Travel (6 items)
1. Event schema migration automation
2. Advanced blob storage backends (S3/Azure Blob)
3. Event encryption at rest (application-level)
4. Multi-tenancy event isolation
5. Event compression for long-term storage
6. GraphQL query API for events

### Epic 5: Observability & Production Readiness (11 items)
1. Production-grade monitoring infrastructure (Grafana, Alertmanager, ELK hosting)
2. Custom dashboard widget framework
3. Machine learning-based anomaly detection
4. Multi-tenant observability isolation
5. Distributed tracing with OpenTelemetry
6. Real-user monitoring (RUM) for web dashboard
7. Cost tracking and billing integration
8. A/B testing framework for autonomous loop variations
9. Mobile app for dashboard (iOS/Android)
10. Advanced analytics (cohort analysis, retention curves)
11. Multi-platform CI/CD orchestration beyond GitHub Actions (duplicate from Epic 3)

**Total**: 27 unaddressed out-of-scope items

---

**Document Owner**: Product/Engineering Leadership
**Next Review**: After alpha launch (3 months post-release)
**Status**: Draft - Awaiting stakeholder review and prioritization
