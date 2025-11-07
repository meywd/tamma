#!/usr/bin/env tsx

import fs from 'fs/promises';
import path from 'path';

interface StoryConfig {
  storyId: string;
  epic: string;
  title?: string;
  description?: string;
  acceptanceCriteria?: string;
}

interface StoryTemplate {
  title: string;
  description: string;
  acceptanceCriteria: string[];
  tasks: Task[];
}

interface Task {
  title: string;
  subtasks: string[];
}

// Story templates for different story types
const storyTemplates: Record<string, Partial<StoryTemplate>> = {
  '3-3-contamination-prevention-system': {
    title: 'Contamination Prevention System',
    description:
      'As a **benchmark maintainer**, I want to **prevent AI models from training on our benchmark tasks through comprehensive contamination prevention measures**, so that **benchmark results reflect genuine AI capability rather than memorization of training data**.',
    acceptanceCriteria: [
      'Private Test Suite Separation: Implement strict separation between public task descriptions and private test cases with encrypted storage and access controls',
      'Task Refreshment System: Automated periodic generation of task variations with semantic preservation to prevent pattern memorization',
      'Task Obfuscation Techniques: Advanced obfuscation methods including variable renaming, structure randomization, and semantic preservation',
      'Training Data Monitoring: Continuous monitoring of public repositories, training datasets, and model outputs for task content leakage',
      'Canary Task Deployment: Special canary tasks embedded in benchmarks to detect contamination and model memorization patterns',
      'Version Isolation Enforcement: Strict isolation between task versions with cross-contamination prevention and access logging',
      'Comprehensive Access Logging: Complete audit trail of all task access, exposure, and distribution with tamper-proof logging',
      'Automated Contamination Detection: AI-powered detection system with real-time alerts and contamination scoring',
    ],
    tasks: [
      {
        title: 'Private Test Suite Architecture',
        subtasks: [
          'Design encrypted storage system for private test cases with AES-256 encryption',
          'Implement role-based access control for test suite access with audit logging',
          'Create public/private task separation with secure API endpoints',
          'Build test suite versioning with secure distribution mechanisms',
        ],
      },
      {
        title: 'Task Refreshment Engine',
        subtasks: [
          'Develop semantic variation generation algorithms for task refreshment',
          'Implement automated scheduling system for periodic task updates',
          'Create difficulty preservation validation for refreshed tasks',
          'Build task variation tracking and lineage management system',
        ],
      },
      {
        title: 'Advanced Obfuscation System',
        subtasks: [
          'Implement variable and function name randomization with semantic preservation',
          'Create code structure randomization algorithms maintaining functionality',
          'Develop comment and documentation obfuscation techniques',
          'Build obfuscation validation system to ensure task integrity',
        ],
      },
      {
        title: 'Training Data Monitoring',
        subtasks: [
          'Implement GitHub repository scanning for task content leakage',
          'Create training dataset analysis integration with major data providers',
          'Build model output monitoring for task memorization detection',
          'Develop real-time contamination alerting system',
        ],
      },
      {
        title: 'Canary Task System',
        subtasks: [
          'Design canary task generation with unique identifiers',
          'Implement canary task embedding in benchmark workflows',
          'Create canary result analysis for contamination detection',
          'Build canary task rotation and replacement system',
        ],
      },
      {
        title: 'Version Isolation Framework',
        subtasks: [
          'Implement strict version separation with access controls',
          'Create cross-version contamination prevention mechanisms',
          'Build version access logging and monitoring',
          'Develop version retirement and archival procedures',
        ],
      },
      {
        title: 'Comprehensive Access Logging',
        subtasks: [
          'Implement tamper-proof logging system for all task access',
          'Create detailed access pattern analysis and anomaly detection',
          'Build access audit trail with immutable storage',
          'Develop access reporting and compliance documentation',
        ],
      },
      {
        title: 'Automated Contamination Detection',
        subtasks: [
          'Integrate AI models for contamination pattern recognition',
          'Create contamination scoring algorithms with confidence metrics',
          'Implement real-time alerting with escalation procedures',
          'Build contamination response and mitigation workflows',
        ],
      },
    ],
  },
  '3-4-initial-test-bank-creation': {
    title: 'Initial Test Bank Creation',
    description:
      'As a **benchmark maintainer**, I want to **create the initial comprehensive set of benchmark tasks across multiple programming languages and scenarios**, so that **we have a robust foundation for AI model evaluation with balanced coverage and validated quality**.',
    acceptanceCriteria: [
      'Comprehensive Task Coverage: Create 3,150 tasks total (7 languages × 3 scenarios × 150 tasks) with balanced distribution across all dimensions',
      'MVP Scenario Focus: Prioritize Code Generation, Testing, and Code Review scenarios for initial implementation with clear success criteria',
      'Balanced Difficulty Distribution: Ensure equal representation of Easy, Medium, and Hard difficulty levels (50 each per scenario per language)',
      'Language Priority Implementation: Complete TypeScript and Python tasks first, followed by C#, Java, Go, Ruby, and Rust implementations',
      'Quality Assurance Validation: All tasks must pass automated compilation, test execution, and code quality analysis before inclusion',
      'Comprehensive Documentation: Each task includes detailed descriptions, examples, expected outputs, and evaluation criteria',
      'Performance Baseline Establishment: Create complexity metrics and execution time baselines for each task category',
      'Complete Metadata Management: All tasks tagged with language, scenario, difficulty, dependencies, and prerequisite knowledge',
    ],
    tasks: [
      {
        title: 'Task Generation Framework',
        subtasks: [
          'Design automated task generation templates for each scenario type',
          'Implement language-specific code generation patterns and best practices',
          'Create difficulty calibration system with objective complexity metrics',
          'Build task validation pipeline with compilation and testing verification',
        ],
      },
      {
        title: 'TypeScript Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks (50 easy, 50 medium, 50 hard)',
          'Create 150 Testing tasks with unit test and integration test scenarios',
          'Develop 150 Code Review tasks with common TypeScript patterns and anti-patterns',
          'Validate all TypeScript tasks with automated quality checks',
        ],
      },
      {
        title: 'Python Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks covering Python idioms and libraries',
          'Create 150 Testing tasks with pytest, unittest, and property-based testing',
          'Develop 150 Code Review tasks focusing on Python-specific best practices',
          'Ensure Python tasks follow PEP 8 and community standards',
        ],
      },
      {
        title: 'C# Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks using .NET ecosystem patterns',
          'Create 150 Testing tasks with xUnit, NUnit, and MSTest frameworks',
          'Develop 150 Code Review tasks covering C# language features and patterns',
          'Validate C# tasks with Visual Studio and .NET CLI tooling',
        ],
      },
      {
        title: 'Java Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks using Java 17+ features and ecosystem',
          'Create 150 Testing tasks with JUnit 5, Mockito, and testing best practices',
          'Develop 150 Code Review tasks covering Java design patterns and conventions',
          'Ensure Java tasks follow Spring Boot and enterprise development patterns',
        ],
      },
      {
        title: 'Go Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks following Go idioms and concurrency patterns',
          'Create 150 Testing tasks with Go testing package and table-driven tests',
          'Develop 150 Code Review tasks focusing on Go-specific best practices',
          'Validate Go tasks with go fmt, vet, and standard tooling',
        ],
      },
      {
        title: 'Ruby Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks using Ruby on Rails and ecosystem patterns',
          'Create 150 Testing tasks with RSpec, Minitest, and testing conventions',
          'Develop 150 Code Review tasks covering Ruby idioms and metaprogramming',
          'Ensure Ruby tasks follow community standards and best practices',
        ],
      },
      {
        title: 'Rust Task Implementation',
        subtasks: [
          'Generate 150 Code Generation tasks using Rust ownership and type system',
          'Create 150 Testing tasks with built-in testing and external test frameworks',
          'Develop 150 Code Review tasks covering Rust safety patterns and performance',
          'Validate Rust tasks with cargo check, clippy, and security audits',
        ],
      },
      {
        title: 'Quality Assurance Pipeline',
        subtasks: [
          'Implement automated compilation validation for all generated tasks',
          'Create test suite execution with coverage reporting for each task',
          'Build code quality analysis with language-specific linting and formatting',
          'Develop manual review workflow for task approval and improvement',
        ],
      },
      {
        title: 'Documentation and Examples',
        subtasks: [
          'Create comprehensive task descriptions with clear objectives',
          'Generate example solutions with detailed explanations',
          'Build evaluation criteria documentation for each scenario type',
          'Develop prerequisite knowledge guides for each difficulty level',
        ],
      },
      {
        title: 'Performance Baseline Development',
        subtasks: [
          'Establish complexity metrics for each task category and language',
          'Create execution time baselines for different solution approaches',
          'Develop memory usage benchmarks for resource-intensive tasks',
          'Build performance regression detection for task validation',
        ],
      },
      {
        title: 'Metadata Management System',
        subtasks: [
          'Tag all tasks with language, scenario, difficulty, and topic metadata',
          'Create dependency tracking for tasks requiring prerequisite knowledge',
          'Build search and filtering system for task discovery and selection',
          'Develop analytics dashboard for task distribution and coverage analysis',
        ],
      },
    ],
  },
  '4-2-automated-scoring-system': {
    title: 'Automated Scoring System',
    description:
      'As a **benchmark runner**, I want to **automated scoring of AI-generated code with comprehensive evaluation metrics**, so that **we can objectively evaluate code quality, correctness, and performance across multiple dimensions**.',
    acceptanceCriteria: [
      'Compilation Success Validation: Automated checking of code compilation with proper error capture, language-specific compiler integration, and detailed failure reporting',
      'Test Suite Execution: Comprehensive test execution with pass/fail reporting, coverage analysis, performance benchmarking, and detailed test result analytics',
      'Code Quality Metrics: Multi-dimensional quality analysis including complexity metrics (cyclomatic, cognitive), maintainability indices, style compliance, and code smell detection',
      'Performance Analysis: Detailed performance profiling including execution time measurement, memory usage tracking, resource consumption analysis, and performance regression detection',
      'Security Vulnerability Scanning: Automated security assessment with vulnerability detection, dependency analysis, security pattern validation, and risk scoring',
      'Plagiarism Detection: Advanced code similarity analysis using AST comparison, token-based similarity, semantic analysis, and cross-reference checking against known solutions',
      'Normalized Scoring System: Standardized scoring across different task types with difficulty weighting, language-specific normalization, and balanced metric aggregation',
      'Score Aggregation Framework: Flexible scoring system with configurable weights, custom scoring algorithms, statistical analysis, and confidence interval calculation',
    ],
    tasks: [
      {
        title: 'Compilation Validation Engine',
        subtasks: [
          'Implement language-specific compiler integrations (TypeScript, Python, C#, Java, Go, Ruby, Rust)',
          'Create sandboxed compilation environment with resource limits and security isolation',
          'Build comprehensive error capture and parsing system with categorized error types',
          'Develop compilation timeout handling and resource management for long-running builds',
        ],
      },
      {
        title: 'Test Execution Framework',
        subtasks: [
          'Create universal test runner supporting multiple testing frameworks and languages',
          'Implement test result collection with detailed pass/fail reporting and coverage metrics',
          'Build performance benchmarking integration with execution time and memory profiling',
          'Develop test isolation and cleanup procedures for reliable test execution',
        ],
      },
      {
        title: 'Code Quality Analysis System',
        subtasks: [
          'Implement complexity analysis algorithms (cyclomatic, cognitive, halstead metrics)',
          'Create maintainability index calculation with language-specific adaptations',
          'Build code style validation with configurable linting rules and formatting checks',
          'Develop code smell detection and anti-pattern recognition system',
        ],
      },
      {
        title: 'Performance Profiling Engine',
        subtasks: [
          'Create execution time measurement with high-precision timing and statistical analysis',
          'Implement memory usage tracking with heap analysis and leak detection',
          'Build resource consumption monitoring for CPU, I/O, and network usage',
          'Develop performance regression detection with baseline comparison and trend analysis',
        ],
      },
      {
        title: 'Security Vulnerability Scanner',
        subtasks: [
          'Integrate static analysis security testing (SAST) tools for vulnerability detection',
          'Create dependency vulnerability scanning with CVE database integration',
          'Build security pattern validation for common security anti-patterns',
          'Develop risk scoring system with severity classification and remediation suggestions',
        ],
      },
      {
        title: 'Plagiarism Detection System',
        subtasks: [
          'Implement AST-based code similarity analysis with structural comparison',
          'Create token-based similarity detection with n-gram analysis and fuzzy matching',
          'Build semantic similarity analysis using code embedding and machine learning',
          'Develop cross-reference checking against known solution databases and web sources',
        ],
      },
      {
        title: 'Score Normalization Engine',
        subtasks: [
          'Create difficulty-based scoring adjustment with calibrated difficulty metrics',
          'Implement language-specific normalization to account for language characteristics',
          'Build balanced metric aggregation with configurable weight distributions',
          'Develop statistical normalization using z-scores and percentile rankings',
        ],
      },
      {
        title: 'Scoring Configuration Management',
        subtasks: [
          'Design flexible scoring configuration system with YAML/JSON configuration files',
          'Implement custom scoring algorithm support with plugin architecture',
          'Create scoring rule validation and testing framework',
          'Build scoring analytics and reporting with detailed breakdown and explanations',
        ],
      },
      {
        title: 'Result Storage and Analytics',
        subtasks: [
          'Implement efficient result storage with time-series database integration',
          'Create scoring analytics dashboard with trend analysis and visualization',
          'Build historical comparison tools for performance tracking over time',
          'Develop export capabilities for scoring data in multiple formats (JSON, CSV, PDF)',
        ],
      },
      {
        title: 'Quality Assurance and Validation',
        subtasks: [
          'Create comprehensive test suite for all scoring components with edge case coverage',
          'Implement scoring accuracy validation against known ground truth datasets',
          'Build performance testing for scoring system scalability and throughput',
          'Develop continuous monitoring and alerting for scoring system health',
        ],
      },
    ],
  },
};

function parseArgs(): StoryConfig {
  const args = process.argv.slice(2);
  const config: StoryConfig = {
    storyId: '',
    epic: '3',
  };

  for (let i = 0; i < args.length; i += 2) {
    const flag = args[i];
    const value = args[i + 1];

    switch (flag) {
      case '--story-id':
        config.storyId = value;
        break;
      case '--epic':
        config.epic = value;
        break;
      case '--title':
        config.title = value;
        break;
      case '--description':
        config.description = value;
        break;
      case '--acceptance-criteria':
        config.acceptanceCriteria = value;
        break;
    }
  }

  if (!config.storyId) {
    console.error('❌ Error: --story-id is required');
    process.exit(1);
  }

  return config;
}

function generateTitleFromId(storyId: string): string {
  return storyId
    .split('-')
    .slice(1)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

function generateStoryContent(config: StoryConfig): string {
  const template = storyTemplates[config.storyId] || {};

  const title = config.title || template.title || generateTitleFromId(config.storyId);
  const description =
    config.description ||
    template.description ||
    `As a **user**, I want to **implement the required functionality**, so that **I can achieve the desired outcome efficiently and effectively**.`;

  let acceptanceCriteria: string[];
  if (config.acceptanceCriteria) {
    try {
      acceptanceCriteria = JSON.parse(config.acceptanceCriteria);
    } catch {
      acceptanceCriteria = [config.acceptanceCriteria];
    }
  } else {
    acceptanceCriteria = template.acceptanceCriteria || [
      'Core functionality implemented according to specifications',
      'Comprehensive test coverage with unit and integration tests',
      'Error handling and edge case management',
      'Performance requirements met and documented',
      'Security best practices implemented',
      'Documentation complete and up to date',
    ];
  }

  const tasks = template.tasks || [
    {
      title: 'Core Implementation',
      subtasks: [
        'Implement primary functionality',
        'Add error handling and validation',
        'Create necessary data models',
        'Implement business logic',
      ],
    },
    {
      title: 'Integration',
      subtasks: [
        'Connect with existing systems',
        'Implement API endpoints',
        'Add database integration',
        'Configure external services',
      ],
    },
    {
      title: 'Testing',
      subtasks: [
        'Write unit tests',
        'Create integration tests',
        'Add performance tests',
        'Implement security tests',
      ],
    },
    {
      title: 'Documentation',
      subtasks: [
        'Update API documentation',
        'Create user guides',
        'Document technical decisions',
        'Add deployment instructions',
      ],
    },
  ];

  return `# Story ${config.storyId.replace('-', '.')}: ${title}

Status: ready-for-dev

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:

- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

${description}

## Acceptance Criteria

${acceptanceCriteria.map((criteria, index) => `${index + 1}. **${criteria}**`).join('\n')}

## Tasks / Subtasks

${tasks
  .map(
    (task, taskIndex) => `- [ ] Task ${taskIndex + 1}: ${task.title}
${task.subtasks.map((subtask, subtaskIndex) => `  - [ ] Subtask ${taskIndex + 1}.${subtaskIndex + 1}: ${subtask}`).join('\n')}`
  )
  .join('\n')}

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**

1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched \`.dev/\` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in \`docs/\` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)

### Requirements Context Summary

**Epic Context:** This story is part of Epic ${config.epic} and implements critical functionality for the test platform. The story delivers specific value while building on previous work and enabling future capabilities.

**Technical Context:** The implementation must integrate with existing systems and follow established patterns while delivering the specified functionality.

**Integration Points:**

- Previous stories in Epic ${config.epic} for foundational functionality
- Database schema from Story 1.1 for data persistence
- Authentication system from Story 1.2 for security
- API infrastructure from Story 1.4 for service exposure

### Implementation Guidance

**Key Design Decisions:**

- Follow established architectural patterns from previous stories
- Implement comprehensive error handling and logging
- Ensure scalability and performance requirements are met
- Maintain security best practices throughout implementation

**Technical Specifications:**

**Core Interface:**

\`\`\`typescript
interface StoryInterface {
  // Define core interface based on story requirements
  id: string;
  status: string;
  createdAt: string;
  updatedAt: string;
}
\`\`\`

**Implementation Pipeline:**

1. **Setup**: Initialize project structure and dependencies
2. **Core Logic**: Implement primary functionality
3. **Integration**: Connect with existing systems
4. **Testing**: Comprehensive test coverage
5. **Documentation**: Update technical documentation
6. **Deployment**: Prepare for production deployment

**Configuration Requirements:**

- Environment-specific configuration management
- Feature flags for gradual rollout
- Monitoring and alerting configuration
- Security and access control settings

**Performance Considerations:**

- Efficient data processing and storage
- Optimized query performance
- Scalable architecture for growth
- Resource management and cleanup

**Security Requirements:**

- Input validation and sanitization
- Authentication and authorization
- Data encryption at rest and in transit
- Audit logging and compliance

### Testing Strategy

**Unit Test Requirements:**

- Core functionality testing with edge cases
- Error handling and validation testing
- Performance and load testing
- Security testing and vulnerability assessment

**Integration Test Requirements:**

- End-to-end workflow testing
- API integration testing
- Database integration testing
- Third-party service integration testing

**Performance Test Requirements:**

- Load testing with expected traffic
- Stress testing beyond normal limits
- Scalability testing for growth scenarios
- Resource utilization optimization

**Edge Cases to Consider:**

- Network failures and timeouts
- Data corruption and recovery
- Concurrent access and race conditions
- Resource exhaustion and degradation

### Dependencies

**Internal Dependencies:**

- Previous stories in Epic ${config.epic}
- Database schema and migration system
- Authentication and authorization framework
- API infrastructure and documentation

**External Dependencies:**

- Third-party APIs and services
- Database systems and storage
- Monitoring and logging services
- Security and compliance tools

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | -------- | ---------- |
| Technical complexity | Medium | Incremental development, thorough testing |
| Integration challenges | Medium | Early integration testing, clear interfaces |
| Performance bottlenecks | Low | Performance monitoring, optimization |
| Security vulnerabilities | High | Security reviews, penetration testing |

### Success Metrics

- [ ] Metric 1: Functional completeness - 100% of acceptance criteria met
- [ ] Metric 2: Test coverage - 90%+ code coverage achieved
- [ ] Metric 3: Performance - Meets specified performance requirements
- [ ] Metric 4: Security - Passes security assessment
- [ ] Metric 5: Documentation - Complete technical documentation

## Related

- Related story: \`docs/stories/\` - Previous/next story in epic
- Related epic: \`docs/epics.md#Epic-${config.epic}\` - Epic context
- Related architecture: \`docs/ARCHITECTURE.md\` - Technical specifications

## References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../README.md) - Search spikes, bugs, findings, decisions
- **Epic Specification:** [test-platform/docs/epics.md](epics.md)
- **Architecture Document:** [test-platform/docs/ARCHITECTURE.md](ARCHITECTURE.md)
- **Product Requirements:** [test-platform/docs/PRD.md](PRD.md)
`;
}

async function main() {
  try {
    const config = parseArgs();

    console.log(`🎯 Creating story: ${config.storyId}`);

    const content = generateStoryContent(config);
    const filePath = path.join(
      process.cwd(),
      'test-platform',
      'docs',
      'stories',
      `${config.storyId}.md`
    );

    // Ensure directory exists
    await fs.mkdir(path.dirname(filePath), { recursive: true });

    // Write story file
    await fs.writeFile(filePath, content, 'utf8');

    console.log(`✅ Story created successfully: ${filePath}`);

    // Display summary
    const lines = content.split('\n');
    const titleLine = lines.find((line) => line.startsWith('# Story'));
    const descriptionStart = lines.findIndex((line) => line.includes('As a'));
    const descriptionEnd = lines.findIndex((line) => line.includes('## Acceptance Criteria'));

    if (titleLine) {
      console.log(`\n📋 ${titleLine}`);
    }

    if (descriptionStart >= 0 && descriptionEnd > descriptionStart) {
      const description = lines.slice(descriptionStart, descriptionEnd).join('\n');
      console.log(`\n📝 ${description}`);
    }

    const acceptanceCount = content.match(/^\d+\./gm)?.length || 0;
    const taskCount = content.match(/^- \[ \]/gm)?.length || 0;

    console.log(`\n📊 Summary:`);
    console.log(`   Acceptance Criteria: ${acceptanceCount}`);
    console.log(`   Tasks: ${taskCount}`);
  } catch (error) {
    console.error('❌ Error creating story:', error);
    process.exit(1);
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main();
}
