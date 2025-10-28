# Tamma

**Autonomous Development Orchestration Platform**

Tamma is an AI-powered development orchestration system that autonomously handles software development tasks from issue selection through pull request completion.

## Overview

Tamma bridges the gap between AI coding assistants and fully autonomous development by providing a structured, event-sourced workflow that:

- 🤖 Selects and analyzes issues from your project management system
- 📋 Generates comprehensive development plans with approval checkpoints
- 🔧 Implements test-first development with automated refactoring
- 🔄 Creates pull requests and monitors CI/CD status
- 🚀 Operates in both standalone CLI and distributed orchestrator/worker modes

## Key Features

### Multi-Provider AI Abstraction
- Pluggable AI provider interface (Claude Code, with extensibility for others)
- Automatic failover and provider selection
- Streaming support for real-time feedback

### Multi-Platform Git Integration
- Unified interface for GitHub, GitLab, and other platforms
- Normalized API for issues, PRs, and repository operations
- Platform-agnostic workflow definitions

### Hybrid Architecture
- **Standalone Mode**: Direct CLI execution for single-developer workflows
- **Orchestrator Mode**: Stateful task queue and worker management
- **Worker Mode**: Distributed execution for parallel issue processing

### Event Sourcing Foundation
- Complete audit trail of all development decisions
- Time-travel debugging for workflow analysis
- Black-box replay for issue reproduction

## Architecture

Tamma uses a **Dynamic Consistency Boundary (DCB)** pattern with a single event stream that ensures deterministic replay while maintaining flexibility:

```
┌─────────────────────────────────────────────────────────┐
│                    Orchestrator                          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐ │
│  │  Task Queue  │  │ Worker Pool  │  │  Event Bus   │ │
│  └──────────────┘  └──────────────┘  └──────────────┘ │
└─────────────────────────────────────────────────────────┘
           │                  │                  │
           ▼                  ▼                  ▼
    ┌───────────┐      ┌───────────┐      ┌───────────┐
    │  Worker 1 │      │  Worker 2 │      │  Worker N │
    └───────────┘      └───────────┘      └───────────┘
           │                  │                  │
           └──────────────────┴──────────────────┘
                          │
                          ▼
                 ┌─────────────────┐
                 │  PostgreSQL     │
                 │  Event Store    │
                 └─────────────────┘
```

## Project Status

🚧 **Pre-Alpha Development** 🚧

Tamma is currently in the specification and architecture phase. We are defining:

- ✅ Product requirements and roadmap
- ✅ Epic 1 technical specifications (Foundation & Core Infrastructure)
- 🔄 Story drafting and implementation planning
- ⏳ Core implementation (starting Q2 2025)

## Roadmap

### Epic 1: Foundation & Core Infrastructure *(In Progress)*
Multi-provider AI abstraction, multi-platform Git integration, hybrid orchestrator/worker architecture

### Epic 2: Autonomous Development Workflow
Issue selection, plan generation, test-first implementation, PR creation and monitoring

### Epic 3: Intelligence & Quality Enhancement
Build/test automation with retry logic, research capability, ambiguity detection, static analysis

### Epic 4: Event Sourcing & Time-Travel
Complete event capture, time-travel debugging, black-box replay

### Epic 5: Observability & Production Readiness
Structured logging, metrics, real-time dashboards, integration testing, alpha release

## Technology Stack

- **Language**: TypeScript 5.7+
- **Runtime**: Node.js 22 LTS
- **Database**: PostgreSQL 17 (event store)
- **API Framework**: Fastify 5.x
- **Package Manager**: pnpm with workspaces
- **Testing**: Jest, integration testing suite
- **Security**: AES-256 encryption, OS keychain integration

## Getting Started

*Documentation coming soon as implementation progresses.*

### Prerequisites
- Node.js 22 LTS or later
- pnpm 9.x or later
- PostgreSQL 17 (for orchestrator mode)

## License

Apache 2.0 - See LICENSE file for details

## Contributing

Contribution guidelines will be published once the core implementation reaches alpha stage.

## Documentation

Detailed specifications and architecture documents are available in the `/docs` folder:

- [Product Requirements Document (PRD)](docs/prd.md)
- [Epic Breakdown](docs/epics.md)
- [Epic 1 Technical Specification](docs/tech-spec-epic-1.md)
- [Sprint Status](docs/sprint-status.yaml)

## Contact

**Repository**: https://github.com/meywd/tamma

---

*Built with the vision of democratizing autonomous software development*
